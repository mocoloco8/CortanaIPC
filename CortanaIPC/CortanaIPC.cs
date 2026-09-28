using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Cortana
{
    // App side of the link to the Windower "cortana" addon.
    //
    //   var ipc = new CortanaIPC();
    //   ipc.CharacterLinked += c => Console.WriteLine(c.Name + " linked");
    //   ipc.StateChanged += (c, what) => { if ((what & StateChanges.Vitals) != 0) Console.WriteLine(c.Name + " HP " + c.State.Player.Hpp); };
    //   ipc.Start();
    //   ...
    //   var me = ipc.GetCharacter("Meggi");
    //   me.Chat("/ma \"Cure IV\" <t>");
    //
    // Each character's addon connects on its own (HELLO/WELCOME, then PING/PONG heartbeats) and
    // streams its game state; the library keeps a CortanaCharacter per character name.
    //
    // Threading: one background thread receives and dispatches; events are raised ON THAT THREAD,
    // in arrival order — keep handlers short (hand heavy work to your own thread). A throwing
    // handler is isolated (reported through HandlerError) and never stops the link. A second
    // background thread probes latency and detects characters going quiet.
    //
    // Several apps can be linked to the same addon, each running independently: each binds its own
    // port in the range Protocol.DefaultPort .. +PortRange-1, gets the full feed and may send any
    // command. The addon is the only source of character state — an app never changes State itself,
    // it sees the effect of its own (and other apps') commands when the addon reports it.
    public sealed class CortanaIPC : IDisposable
    {
        // Binds the first free port in the app range (Port tells which, after Start).
        public CortanaIPC() : this(Protocol.DefaultPort)
        {
            _autoPort = true;
        }

        // Binds exactly this port (Start throws if it's taken).
        public CortanaIPC(int port)
        {
            Port = port;
            BindAddress = IPAddress.Loopback;
            LinkTimeout = TimeSpan.FromSeconds(6);
            ProbeInterval = TimeSpan.FromSeconds(2);
            AppName = DefaultAppName();
        }

        private readonly bool _autoPort;

        // The bound port — also this app's identity to the addon.
        public int Port { get; private set; }
        // Shown in game (//cortana status, link / control messages). Set before Start().
        public string AppName { get; set; }
        // Loopback by default: the addon only talks to 127.0.0.1, and nothing else should reach the port.
        public IPAddress BindAddress { get; set; }
        // A character counts as linked while it was heard from within this window.
        public TimeSpan LinkTimeout { get; set; }
        // How often each linked addon is sent a latency probe.
        public TimeSpan ProbeInterval { get; set; }

        public bool IsRunning { get { return _running; } }

        // Spell / ability / item data, available once an addon has reported its Windower path.
        public WindowerResources Resources { get { return _resources; } }

        // ================================================================ events
        public event Action<CortanaCharacter> CharacterLinked;
        public event Action<CortanaCharacter> CharacterUnlinked;
        // Every message, after the library processed it (sender is null for PBUFF).
        public event Action<CortanaCharacter, string[]> MessageReceived;
        // A handler threw; the link keeps running.
        public event Action<string, Exception> HandlerError;

        // Game state
        public event Action<CortanaCharacter, StateChanges> StateChanged;
        public event Action<CortanaCharacter, ChatLine> ChatReceived;
        public event Action<CortanaCharacter> BuffTimersUpdated;
        public event Action<string, IReadOnlyCollection<int>, IReadOnlyCollection<int>> PartyBuffsUpdated;  // member, now, before
        public event Action<CortanaCharacter> InventoryCountsUpdated;
        public event Action<CortanaCharacter, Currency> CurrencyUpdated;
        public event Action<CortanaCharacter, bool> TypingChanged;
        public event Action<CortanaCharacter> CapabilitiesChanged;
        public event Action<CortanaCharacter> ExtrasUpdated;                     // CortanaCharacter.Extras replaced

        // Combat
        public event Action<CortanaCharacter, CombatReport> CombatReported;
        public event Action<CortanaCharacter, SkillchainReport> SkillchainDetected;
        public event Action<CortanaCharacter, MobAction> MobActionStarted;
        public event Action<CortanaCharacter, MobActionResult> MobActionResolved;
        public event Action<CortanaCharacter, PlayerAction> PlayerActionReported;
        public event Action<CortanaCharacter, CastReport> CastReported;         // own spell / item: begin, interrupt, finish
        public event Action<CortanaCharacter, CastResult> CastResolved;         // own action's outcome + amount
        public event Action<CortanaCharacter, ActionMessage> ActionMessageReceived; // any party action message
        public event Action<CortanaCharacter, EquipmentState> EquipmentChanged;
        public event Action<CortanaCharacter, TreasureItem> TreasureFound;
        public event Action<CortanaCharacter, TreasureResult> TreasureResolved;
        public event Action<CortanaCharacter, IReadOnlyList<WidescanMob>> WidescanCompleted; // a sweep went quiet
        public event Action<CortanaCharacter, DebuffWear> DebuffWoreOff;
        public event Action<CortanaCharacter, JobAbilityResult> JobAbilityResolved;
        public event Action<CortanaCharacter, int, float> AutoTargetReported;   // entity index, distance
        public event Action<CortanaCharacter, HoverReport> HoverReported;
        public event Action<CortanaCharacter> EngagementDesync;

        // NPCs / dialogs
        public event Action<CortanaCharacter, int> DialogOpened;                 // menu id
        public event Action<CortanaCharacter, string> DialogFailed;
        public event Action<CortanaCharacter, DialogChoice> DialogChoiceSent;
        public event Action<CortanaCharacter, NpcEvent> NpcEventStarted;
        public event Action<CortanaCharacter, string> NpcEventUpdated;           // params hex
        public event Action<CortanaCharacter, RecordedExchange> ExchangeRecorded;
        public event Action<CortanaCharacter, SellResult> SellCompleted;
        public event Action<CortanaCharacter, ReplayResult> ReplayCompleted;

        // Misc
        public event Action<CortanaCharacter, AddonCommand> CommandReceived;     // //cortana <feature> <action> [target]
        public event Action<CortanaCharacter, int, int> JobChangeSent;           // main, sub
        public event Action<CortanaCharacter, string> JobChangeFailed;

        // Extra per-type handlers (runs for built-in and custom message types alike).
        private readonly ConcurrentDictionary<string, List<Action<CortanaCharacter, string[]>>> _handlers =
            new ConcurrentDictionary<string, List<Action<CortanaCharacter, string[]>>>(StringComparer.Ordinal);

        public void On(string messageType, Action<CortanaCharacter, string[]> handler)
        {
            var list = _handlers.GetOrAdd(messageType, t => new List<Action<CortanaCharacter, string[]>>());
            lock (list) list.Add(handler);
        }

        public void Off(string messageType, Action<CortanaCharacter, string[]> handler)
        {
            List<Action<CortanaCharacter, string[]>> list;
            if (_handlers.TryGetValue(messageType, out list)) lock (list) list.Remove(handler);
        }

        // ================================================================ characters
        private readonly ConcurrentDictionary<string, CortanaCharacter> _chars =
            new ConcurrentDictionary<string, CortanaCharacter>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _wasLinked =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<CortanaCharacter> Characters { get { return _chars.Values.ToList(); } }
        public IReadOnlyList<CortanaCharacter> LinkedCharacters { get { return _chars.Values.Where(c => c.IsLinked).ToList(); } }

        public bool TryGetCharacter(string name, out CortanaCharacter character)
        {
            character = null;
            return !string.IsNullOrEmpty(name) && _chars.TryGetValue(name.Trim(), out character);
        }

        // The character by name, created (unlinked, empty state) if its addon hasn't connected yet —
        // so you can hold a reference before it links.
        public CortanaCharacter GetCharacter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
            return _chars.GetOrAdd(name.Trim(), n => new CortanaCharacter(this, n));
        }

        // ---- party buffs (0x076): reported by any character for every party member ----
        private sealed class PartyBuffEntry { public HashSet<int> Ids; public DateTime At; }
        private readonly ConcurrentDictionary<string, PartyBuffEntry> _partyBuffs =
            new ConcurrentDictionary<string, PartyBuffEntry>(StringComparer.OrdinalIgnoreCase);

        // Buff ids a party member currently has, per the server's party-buff packet.
        public bool TryGetPartyBuffs(string memberName, out IReadOnlyCollection<int> buffIds, out DateTime receivedAt)
        {
            buffIds = null; receivedAt = DateTime.MinValue;
            PartyBuffEntry e;
            if (string.IsNullOrEmpty(memberName) || !_partyBuffs.TryGetValue(memberName.Trim(), out e)) return false;
            buffIds = e.Ids; receivedAt = e.At;
            return true;
        }

        // ================================================================ transport
        private UdpClient _udp;
        private volatile bool _running;
        private volatile WindowerResources _resources;

        public void Start()
        {
            if (_running) return;
            _udp = Bind();
            // The state feed can reach dozens of datagrams a second per character (several KB for
            // entity resyncs) — size the kernel queue so a pause on our side can't drop them.
            _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            _running = true;
            new Thread(ReceiveLoop) { IsBackground = true, Name = "CortanaIPC-rx" }.Start();
            new Thread(Housekeeping) { IsBackground = true, Name = "CortanaIPC-probe" }.Start();
        }

        // The first free port in the app range, or exactly Port when it was given.
        private UdpClient Bind()
        {
            var address = BindAddress ?? IPAddress.Loopback;
            if (!_autoPort) return new UdpClient(new IPEndPoint(address, Port));

            SocketException last = null;
            for (int port = Protocol.DefaultPort; port < Protocol.DefaultPort + Protocol.PortRange; port++)
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(address, port));
                    Port = port;
                    return udp;
                }
                catch (SocketException ex) { last = ex; }
            }
            throw last;
        }

        public void Stop()
        {
            if (!_running) return;
            // Let every addon drop us now rather than after its timeout.
            try { Broadcast(Protocol.Out.Bye); } catch { }
            _running = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
        }

        public void Dispose() { Stop(); }

        // Sends one raw protocol message to a character's addon (no-op while unlinked).
        public void Send(string characterName, string message)
        {
            CortanaCharacter c;
            if (TryGetCharacter(characterName, out c)) Send(c, message);
        }

        internal void Send(CortanaCharacter c, string message)
        {
            var udp = _udp;
            var ep = c.Endpoint;
            if (udp == null || ep == null || string.IsNullOrEmpty(message)) return;
            try { var b = Encoding.UTF8.GetBytes(message); udp.Send(b, b.Length, ep); }
            catch (Exception ex) { Report("send to " + c.Name, ex); }
        }

        public void Broadcast(string message)
        {
            foreach (var c in _chars.Values) if (c.IsLinked) Send(c, message);
        }

        private void Reply(IPEndPoint ep, string message)
        {
            var udp = _udp;
            if (udp == null) return;
            try { var b = Encoding.UTF8.GetBytes(message); udp.Send(b, b.Length, ep); } catch { }
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref ep);
                    Dispatch(Encoding.UTF8.GetString(data), ep);
                }
                catch (SocketException) { if (!_running) break; }
                catch (ObjectDisposedException) { break; }
                catch (NullReferenceException) { if (!_running) break; }
                catch (Exception ex) { Report("receive", ex); }
            }
        }

        private readonly ConcurrentDictionary<string, DateTime> _scanReported =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private void Housekeeping()
        {
            var lastProbe = Stopwatch.StartNew();
            while (_running)
            {
                Thread.Sleep(250);
                try
                {
                    foreach (var c in _chars.Values)
                    {
                        bool linked = c.IsLinked, was;
                        _wasLinked.TryGetValue(c.Name, out was);
                        if (was && !linked) { _wasLinked[c.Name] = false; Fire(CharacterUnlinked, c); }

                        // A widescan sweep has no end marker: it is done once the mobs stop arriving.
                        if (c.WidescanAt > DateTime.MinValue && c.WidescanAt > _scanReported.GetOrAdd(c.Name, DateTime.MinValue)
                            && (DateTime.UtcNow - c.WidescanAt).TotalMilliseconds >= 600)
                        {
                            _scanReported[c.Name] = c.WidescanAt;
                            Fire(WidescanCompleted, c, c.WidescanMobs);
                        }
                    }
                    if (lastProbe.Elapsed >= ProbeInterval)
                    {
                        lastProbe.Restart();
                        foreach (var c in _chars.Values)
                            if (c.IsLinked) Send(c, Protocol.Out.Echo + "|" + Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex) { Report("housekeeping", ex); }
            }
        }

        private CortanaCharacter Touch(string name, IPEndPoint ep)
        {
            var c = GetCharacter(name);
            c.Endpoint = ep;
            c.LastSeen = DateTime.UtcNow;
            bool was;
            if (!_wasLinked.TryGetValue(c.Name, out was) || !was)
            {
                _wasLinked[c.Name] = true;
                Fire(CharacterLinked, c);
            }
            return c;
        }

        // ================================================================ dispatch
        private void Dispatch(string msg, IPEndPoint ep)
        {
            if (string.IsNullOrEmpty(msg)) return;
            var p = msg.Split('|');
            string type = p[0];
            string first = p.Length > 1 ? p[1].Trim() : "";

            CortanaCharacter c = null;
            // Field 2 is the sender for every message except PBUFF (there it's the party member).
            if (type != Protocol.In.PartyBuffs && first.Length > 0) c = Touch(first, ep);

            try { Handle(type, p, c, ep); }
            catch (Exception ex) { Report(type, ex); }

            List<Action<CortanaCharacter, string[]>> extra;
            if (_handlers.TryGetValue(type, out extra))
            {
                Action<CortanaCharacter, string[]>[] snapshot;
                lock (extra) snapshot = extra.ToArray();
                foreach (var h in snapshot) { try { h(c, p); } catch (Exception ex) { Report("On(" + type + ")", ex); } }
            }
            Fire(MessageReceived, c, p);
        }

        private void Handle(string type, string[] p, CortanaCharacter c, IPEndPoint ep)
        {
            StateChanges changes;
            switch (type)
            {
                // ---- link ----
                case Protocol.In.Hello:            // HELLO|name|serverId|zone|version[|state1|windowerPath]
                    if (c == null) return;
                    c.ServerId = Protocol.UInt(p, 2);
                    c.AddonVersion = Protocol.Str(p, 4);
                    if (p.Length > 6 && p[5] == "state1")
                    {
                        c.WindowerPath = p[6];
                        if (_resources == null && p[6].Length > 0) _resources = new WindowerResources(p[6]);
                    }
                    Reply(ep, Protocol.Out.Welcome + "|cortana|" + WireText(AppName));
                    return;
                case Protocol.In.Ping:
                    Reply(ep, Protocol.Out.Pong);
                    return;
                case Protocol.In.EchoReply:        // ECHOR|name|timestamp
                    if (c == null) return;
                    long sent;
                    if (long.TryParse(Protocol.Str(p, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out sent) && sent > 0)
                    {
                        double ms = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                        if (ms >= 0 && ms < 60000) c.NoteRoundTrip(ms);
                    }
                    return;

                // ---- state feed ----
                case Protocol.In.Player:
                    if (c == null) return;
                    changes = c.State.IngestPlayer(p);
                    if (changes != StateChanges.None) Fire(StateChanged, c, changes);
                    return;
                case Protocol.In.Pet:
                    if (c == null) return;
                    changes = c.State.IngestPet(p);
                    if (changes != StateChanges.None) Fire(StateChanged, c, changes);
                    return;
                case Protocol.In.Party:
                    if (c == null) return;
                    changes = c.State.IngestParty(p);
                    if (changes != StateChanges.None) Fire(StateChanged, c, changes);
                    return;
                case Protocol.In.Recasts:
                    if (c == null) return;
                    changes = c.State.IngestRecasts(p);
                    if (changes != StateChanges.None) Fire(StateChanged, c, changes);
                    return;
                case Protocol.In.Entities: if (c != null) c.State.IngestEntities(p); return;
                case Protocol.In.EntityExit: if (c != null) c.State.IngestEntityExit(p); return;
                case Protocol.In.KnownSpells: if (c != null) c.State.IngestSpells(p); return;
                case Protocol.In.KnownAbilities: if (c != null) c.State.IngestAbilities(p); return;
                case Protocol.In.Inventory: if (c != null) c.State.IngestInventory(p); return;
                case Protocol.In.Chat:
                    if (c == null) return;
                    Fire(ChatReceived, c, c.State.IngestChat(p));
                    return;

                // ---- side feeds ----
                case Protocol.In.Buffs:            // BUFFS|name|id:remSec,id:remSec,...  (no ':' = permanent)
                {
                    if (c == null) return;
                    var list = new List<BuffTimer>();
                    var now = DateTime.UtcNow;
                    foreach (var tok in Protocol.Str(p, 2).Split(','))
                    {
                        if (tok.Length == 0) continue;
                        int colon = tok.IndexOf(':'), id, rem;
                        if (colon <= 0) { if (!int.TryParse(tok, out id)) continue; rem = 60000; }
                        else { if (!int.TryParse(tok.Substring(0, colon), out id)) continue; if (!int.TryParse(tok.Substring(colon + 1), out rem)) rem = 0; }
                        if (id <= 0 || id == 255) continue;
                        list.Add(new BuffTimer { BuffId = id, ExpiresAt = now.AddSeconds(rem) });
                    }
                    c.SetBuffTimers(list);
                    Fire(BuffTimersUpdated, c);
                    return;
                }
                case Protocol.In.PartyBuffs:       // PBUFF|memberName|id,id,...
                {
                    string member = Protocol.Str(p, 1).Trim();
                    if (member.Length == 0) return;
                    var set = new HashSet<int>();
                    foreach (var tok in Protocol.Str(p, 2).Split(','))
                    {
                        int id;
                        if (int.TryParse(tok, out id) && id > 0 && id != 255) set.Add(id);
                    }
                    PartyBuffEntry prev;
                    _partyBuffs.TryGetValue(member, out prev);
                    _partyBuffs[member] = new PartyBuffEntry { Ids = set, At = DateTime.UtcNow };
                    Fire(PartyBuffsUpdated, member, (IReadOnlyCollection<int>)set, prev != null ? (IReadOnlyCollection<int>)prev.Ids : new HashSet<int>());
                    return;
                }
                case Protocol.In.InventoryCounts:  // INV|name|slot:id:count,...
                {
                    if (c == null) return;
                    var d = new Dictionary<int, int>();
                    foreach (var tok in Protocol.Str(p, 2).Split(','))
                    {
                        var f = tok.Split(':');
                        if (f.Length < 3) continue;
                        int id = Protocol.Int(f, 1), cnt = Protocol.Int(f, 2);
                        if (id <= 0) continue;
                        int cur; d.TryGetValue(id, out cur);
                        d[id] = cur + (cnt > 0 ? cnt : 1);
                    }
                    c.SetInventoryCounts(d);
                    Fire(InventoryCountsUpdated, c);
                    return;
                }
                case Protocol.In.Currency:         // CUR|name|sparks|accolades  (-1 = not in this packet)
                    if (c == null) return;
                    c.MergeCurrency(p.Length > 2 ? Protocol.Int(p, 2) : -1, p.Length > 3 ? Protocol.Int(p, 3) : -1);
                    Fire(CurrencyUpdated, c, c.Currency);
                    return;
                case Protocol.In.PlayerExtras:     // SPX|name|jobId=spent,...|base stats|added stats|skill=level,...|itemId:count,...
                {
                    if (c == null) return;
                    var jp = new Dictionary<int, int>();
                    foreach (var tok in Protocol.Str(p, 2).Split(','))
                    {
                        var f = tok.Split('=');
                        if (f.Length == 2 && Protocol.Int(f, 0) > 0) jp[Protocol.Int(f, 0)] = Protocol.Int(f, 1);
                    }
                    var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tok in Protocol.Str(p, 5).Split(','))
                    {
                        var f = tok.Split('=');
                        if (f.Length == 2 && f[0].Length > 0) skills[f[0]] = Protocol.Int(f, 1);
                    }
                    var temp = new Dictionary<int, int>();
                    foreach (var tok in Protocol.Str(p, 6).Split(','))
                    {
                        var f = tok.Split(':');
                        if (f.Length == 2 && Protocol.Int(f, 0) > 0) temp[Protocol.Int(f, 0)] = Protocol.Int(f, 1);
                    }
                    c.SetExtras(new PlayerExtras
                    {
                        JobPointsSpent = jp, BaseStats = CharacterStats.Parse(Protocol.Str(p, 3)),
                        AddedStats = CharacterStats.Parse(Protocol.Str(p, 4)), Skills = skills, TemporaryItems = temp,
                        ReceivedAt = DateTime.UtcNow,
                    });
                    Fire(ExtrasUpdated, c);
                    return;
                }
                case Protocol.In.CastResult:       // CASTRES|name|cat=;id=;target=;targets=;msg=;param=;sum=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    var r = new CastResult
                    {
                        Category = Protocol.KvInt(kv, "cat"), ActionId = Protocol.KvInt(kv, "id"),
                        TargetId = (uint)Protocol.KvLong(kv, "target"), Targets = Protocol.KvInt(kv, "targets"),
                        MessageId = Protocol.KvInt(kv, "msg"), Param = Protocol.KvInt(kv, "param"),
                        TotalValue = Protocol.KvInt(kv, "sum"), Name = Protocol.KvStr(kv, "name"),
                        At = DateTime.UtcNow,
                    };
                    c.SetCastResult(r);
                    Fire(CastResolved, c, r);
                    return;
                }
                case Protocol.In.ActionMessage:    // AMSG|name|msg=;actor=;target=;aindex=;tindex=;p1=;p2=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    Fire(ActionMessageReceived, c, new ActionMessage
                    {
                        MessageId = Protocol.KvInt(kv, "msg"),
                        ActorId = (uint)Protocol.KvLong(kv, "actor"), TargetId = (uint)Protocol.KvLong(kv, "target"),
                        ActorIndex = Protocol.KvInt(kv, "aindex"), TargetIndex = Protocol.KvInt(kv, "tindex"),
                        Param1 = Protocol.KvInt(kv, "p1"), Param2 = Protocol.KvInt(kv, "p2"),
                        At = DateTime.UtcNow,
                    });
                    return;
                }
                case Protocol.In.TreasureFound:    // TPOOL|name|slot=;item=;count=;dropper=;old=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    var t = new TreasureItem
                    {
                        Slot = Protocol.KvInt(kv, "slot"), ItemId = Protocol.KvInt(kv, "item"),
                        Count = Protocol.KvInt(kv, "count"), DropperId = (uint)Protocol.KvLong(kv, "dropper"),
                        Old = Protocol.KvInt(kv, "old") == 1, Name = Protocol.KvStr(kv, "name"),
                        FoundAt = DateTime.UtcNow,
                    };
                    c.AddTreasure(t);
                    Fire(TreasureFound, c, t);
                    return;
                }
                case Protocol.In.TreasureResult:   // TLOTR|name|slot=;drop=;lot=;winner=;lotter=;lotted=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    var r = new TreasureResult
                    {
                        Slot = Protocol.KvInt(kv, "slot"), Drop = Protocol.KvInt(kv, "drop"),
                        HighestLot = Protocol.KvInt(kv, "lot"), WinnerName = Protocol.KvStr(kv, "winner"),
                        LotterName = Protocol.KvStr(kv, "lotter"), Lot = Protocol.KvInt(kv, "lotted"),
                        At = DateTime.UtcNow,
                    };
                    if (r.Finished) c.ClearTreasureSlot(r.Slot);
                    Fire(TreasureResolved, c, r);
                    return;
                }
                case Protocol.In.WidescanMob:      // WSMOB|name|index=;level=;type=;x=;y=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    c.AddWidescan(new WidescanMob
                    {
                        Index = Protocol.KvInt(kv, "index"), Level = Protocol.KvInt(kv, "level"),
                        Type = Protocol.KvStr(kv, "type"), X = Protocol.KvInt(kv, "x"), Y = Protocol.KvInt(kv, "y"),
                        Name = Protocol.KvStr(kv, "name"), At = DateTime.UtcNow,
                    });
                    return;
                }
                case Protocol.In.Equipment:        // EQP|name|main=;mainname=;mainskill=;sub=;...;ammocount=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    var e = new EquipmentState
                    {
                        MainId = Protocol.KvInt(kv, "main"), MainName = Protocol.KvStr(kv, "mainname"),
                        MainSkill = Protocol.KvInt(kv, "mainskill"),
                        SubId = Protocol.KvInt(kv, "sub"), SubName = Protocol.KvStr(kv, "subname"),
                        RangedId = Protocol.KvInt(kv, "range"), RangedName = Protocol.KvStr(kv, "rangename"),
                        RangedSkill = Protocol.KvInt(kv, "rangeskill"),
                        AmmoId = Protocol.KvInt(kv, "ammo"), AmmoName = Protocol.KvStr(kv, "ammoname"),
                        AmmoCount = Protocol.KvInt(kv, "ammocount"),
                        At = DateTime.UtcNow,
                    };
                    var was = c.Equipment;
                    c.SetEquipment(e);
                    if (was == null || was.MainId != e.MainId || was.SubId != e.SubId
                        || was.RangedId != e.RangedId || was.AmmoId != e.AmmoId || was.AmmoCount != e.AmmoCount)
                        Fire(EquipmentChanged, c, e);
                    return;
                }
                case Protocol.In.Cast:             // CAST|name|begin/interrupt/finish|spell/item|id
                {
                    if (c == null) return;
                    string phase = Protocol.Str(p, 2);
                    Fire(CastReported, c, new CastReport
                    {
                        Phase = phase == "begin" ? CastPhase.Begin : phase == "interrupt" ? CastPhase.Interrupt : CastPhase.Finish,
                        IsItem = Protocol.Str(p, 3) == "item",
                        Id = Protocol.Int(p, 4),
                    });
                    return;
                }
                case Protocol.In.Typing:           // TYPING|name|0/1
                    if (c == null) return;
                    bool open = Protocol.Str(p, 2).Trim() == "1";
                    c.SetTyping(open);
                    Fire(TypingChanged, c, open);
                    return;
                case Protocol.In.KeyAck: if (c != null && !c.KeysConfirmed) { c.KeysConfirmed = true; Fire(CapabilitiesChanged, c); } return;
                case Protocol.In.KeyNak: if (c != null && !c.KeysUnsupported) { c.KeysUnsupported = true; Fire(CapabilitiesChanged, c); } return;
                case Protocol.In.FollowAck: if (c != null && !c.FollowConfirmed) { c.FollowConfirmed = true; Fire(CapabilitiesChanged, c); } return;
                case Protocol.In.FollowNak: if (c != null && !c.FollowUnsupported) { c.FollowUnsupported = true; Fire(CapabilitiesChanged, c); } return;

                // ---- combat ----
                case Protocol.In.Combat:           // COMBAT|name|kv
                {
                    if (c == null) return;
                    string raw = Protocol.Str(p, 2);
                    var kv = Protocol.KeyValues(raw);
                    Fire(CombatReported, c, new CombatReport { Fields = kv, Raw = raw, At = DateTime.UtcNow });
                    if (Protocol.KvStr(kv, "type") == "SC")
                        Fire(SkillchainDetected, c, new SkillchainReport
                        { Name = Protocol.KvStr(kv, "name"), TargetId = Protocol.KvInt(kv, "target"), Source = Protocol.KvStr(kv, "src") });
                    return;
                }
                case Protocol.In.MobAction:        // MOBACT|name|cat=;id=;actor=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    Fire(MobActionStarted, c, new MobAction
                    { Category = Protocol.KvInt(kv, "cat"), ActionId = Protocol.KvInt(kv, "id"), ActorId = (uint)Protocol.KvLong(kv, "actor"), Name = Protocol.KvStr(kv, "name") });
                    return;
                }
                case Protocol.In.MobResult:        // MOBRES|name|cat=;id=;actor=;name=;targets=;dmg=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    Fire(MobActionResolved, c, new MobActionResult
                    {
                        Category = Protocol.KvInt(kv, "cat"), ActionId = Protocol.KvInt(kv, "id"), ActorId = (uint)Protocol.KvLong(kv, "actor"),
                        Name = Protocol.KvStr(kv, "name"), Targets = Protocol.KvInt(kv, "targets"), TotalDamage = Protocol.KvLong(kv, "dmg"),
                    });
                    return;
                }
                case Protocol.In.PlayerAction:     // PLRACT|name|cat=;id=;actor=;target=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    Fire(PlayerActionReported, c, new PlayerAction
                    {
                        Category = Protocol.KvInt(kv, "cat"), ActionId = Protocol.KvInt(kv, "id"), ActorId = (uint)Protocol.KvLong(kv, "actor"),
                        TargetId = (uint)Protocol.KvLong(kv, "target"), Name = Protocol.KvStr(kv, "name"),
                    });
                    return;
                }
                case Protocol.In.DebuffWear:       // DEBUFFWEAR|name|target=;tindex=;buff=;msg=;name=
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    Fire(DebuffWoreOff, c, new DebuffWear
                    {
                        TargetId = (uint)Protocol.KvLong(kv, "target"), TargetIndex = Protocol.KvInt(kv, "tindex"),
                        BuffId = Protocol.KvInt(kv, "buff"), MessageId = Protocol.KvInt(kv, "msg"), Name = Protocol.KvStr(kv, "name"),
                    });
                    return;
                }
                case Protocol.In.JobAbility:       // ROLL|name|abilityId|value|abilityName
                    if (c == null) return;
                    Fire(JobAbilityResolved, c, new JobAbilityResult { AbilityId = Protocol.Int(p, 2), Value = Protocol.Int(p, 3), Name = Protocol.Str(p, 4) });
                    return;
                case Protocol.In.AutoTarget:       // AUTOTARGET|name|serverId|index|dist
                    if (c == null) return;
                    Fire(AutoTargetReported, c, Protocol.Int(p, 3), Protocol.Float(p, 4));
                    return;
                case Protocol.In.Hover:            // HOVER|name|kind=shot|ws;particle=0/1
                {
                    if (c == null) return;
                    var kv = Protocol.KeyValues(Protocol.Str(p, 2));
                    var r = new HoverReport { Kind = Protocol.KvStr(kv, "kind"), Particle = Protocol.KvStr(kv, "particle") == "1" };
                    if (r.Kind == "ws") c.LastHoverWeaponSkillAt = DateTime.UtcNow;
                    if (r.Particle) c.LastHoverParticleAt = DateTime.UtcNow;
                    Fire(HoverReported, c, r);
                    return;
                }
                case Protocol.In.Desync: if (c != null) Fire(EngagementDesync, c); return;

                // ---- NPCs / dialogs ----
                case Protocol.In.DialogOpen:       // DLGOPEN|name|menuId
                    if (c == null) return;
                    c.LastDialogMenuId = Protocol.Int(p, 2);
                    c.LastDialogOpenedAt = DateTime.UtcNow;
                    c.LastDialogOpenedTick = CortanaCharacter.Tick();
                    Fire(DialogOpened, c, c.LastDialogMenuId);
                    return;
                case Protocol.In.DialogFail: if (c != null) Fire(DialogFailed, c, Protocol.Str(p, 2)); return;
                case Protocol.In.DialogChoice:     // DLGOPT|name|menuId|option|unknown1|automated
                {
                    if (c == null) return;
                    var choice = new DialogChoice
                    {
                        MenuId = Protocol.Int(p, 2), Option = Protocol.Int(p, 3), Unknown1 = Protocol.Int(p, 4),
                        Automated = Protocol.Int(p, 5) != 0, SentAt = DateTime.UtcNow, Tick = CortanaCharacter.Tick(),
                    };
                    c.NoteChoice(choice);
                    Fire(DialogChoiceSent, c, choice);
                    var done = c.RecordChoice(choice);
                    if (done != null) Fire(ExchangeRecorded, c, done);
                    return;
                }
                case Protocol.In.NpcEvent:         // EVT|name|packetId|menuId|npcId|npcIndex|paramsHex
                {
                    if (c == null) return;
                    var e = new NpcEvent
                    {
                        PacketId = Protocol.Int(p, 2), MenuId = Protocol.Int(p, 3), NpcId = Protocol.UInt(p, 4),
                        NpcIndex = Protocol.Int(p, 5), ParamsHex = Protocol.Str(p, 6), At = DateTime.UtcNow,
                    };
                    c.RecordEventStart(e);
                    Fire(NpcEventStarted, c, e);
                    return;
                }
                case Protocol.In.NpcEventUpdate: if (c != null) Fire(NpcEventUpdated, c, Protocol.Str(p, 2)); return;
                case Protocol.In.SellDone:         // SELLDONE|name|sold|reason
                {
                    if (c == null) return;
                    var r = new SellResult { StacksSold = Protocol.Int(p, 2), Error = Protocol.Str(p, 3) };
                    c.SetSellResult(r);
                    Fire(SellCompleted, c, r);
                    return;
                }
                case Protocol.In.ReplayDone:       // REPLAYDONE|name|ok|reason
                {
                    if (c == null) return;
                    var r = new ReplayResult { Success = Protocol.Str(p, 2) == "1", Error = Protocol.Str(p, 3) };
                    c.SetReplayResult(r);
                    Fire(ReplayCompleted, c, r);
                    return;
                }

                // ---- misc ----
                case Protocol.In.Command:          // CMD|name|feature|action|target
                    if (c == null) return;
                    Fire(CommandReceived, c, new AddonCommand
                    { Feature = Protocol.Str(p, 2), Action = p.Length > 3 ? p[3] : "toggle", Target = Protocol.Str(p, 4) });
                    return;
                case Protocol.In.JobSent: if (c != null) Fire(JobChangeSent, c, Protocol.Int(p, 2), Protocol.Int(p, 3)); return;
                case Protocol.In.JobFail: if (c != null) Fire(JobChangeFailed, c, Protocol.Str(p, 2)); return;
            }
        }

        // ================================================================ helpers
        private static string DefaultAppName()
        {
            try { return Process.GetCurrentProcess().ProcessName; } catch { return "app"; }
        }

        // Free text inside one wire field: no field separators.
        private static string WireText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "app";
            return s.Replace('|', '/').Replace('~', '-').Trim();
        }

        private void Report(string where, Exception ex)
        {
            var h = HandlerError;
            if (h == null) return;
            try { h(where, ex); } catch { }
        }

        // Each subscriber runs isolated: one throwing handler can't starve the others or the link.
        private void Fire<T>(Action<T> ev, T a)
        {
            if (ev == null) return;
            foreach (Action<T> h in ev.GetInvocationList()) { try { h(a); } catch (Exception ex) { Report(h.Method.Name, ex); } }
        }

        private void Fire<T1, T2>(Action<T1, T2> ev, T1 a, T2 b)
        {
            if (ev == null) return;
            foreach (Action<T1, T2> h in ev.GetInvocationList()) { try { h(a, b); } catch (Exception ex) { Report(h.Method.Name, ex); } }
        }

        private void Fire<T1, T2, T3>(Action<T1, T2, T3> ev, T1 a, T2 b, T3 c)
        {
            if (ev == null) return;
            foreach (Action<T1, T2, T3> h in ev.GetInvocationList()) { try { h(a, b, c); } catch (Exception ex) { Report(h.Method.Name, ex); } }
        }
    }
}
