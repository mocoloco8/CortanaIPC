using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;

namespace Cortana
{
    // One character running the cortana addon: link status, its live game state, the side feeds
    // (buff timers, currency, dialogs, ...) and every command the addon accepts.
    //
    // Commands are fire-and-forget UDP sends; a few (Sell, NpcReplay, QueryCurrency) also wait
    // for the addon's reply. All are safe to call from any thread and are no-ops while unlinked.
    public sealed class CortanaCharacter
    {
        private readonly CortanaIPC _ipc;

        internal CortanaCharacter(CortanaIPC ipc, string name)
        {
            _ipc = ipc;
            Name = name;
            State = new CharacterState();
        }

        public string Name { get; private set; }
        public uint ServerId { get; internal set; }
        public string AddonVersion { get; internal set; }
        public string WindowerPath { get; internal set; }       // Windower install folder (from HELLO)
        public CharacterState State { get; private set; }

        internal volatile IPEndPoint Endpoint;
        private long _lastSeenTicks;                             // DateTime.UtcNow.Ticks
        public DateTime LastSeen
        {
            get { return new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc); }
            internal set { Interlocked.Exchange(ref _lastSeenTicks, value.Ticks); }
        }

        // Linked while the addon has been heard from within the IPC's LinkTimeout.
        public bool IsLinked { get { return Endpoint != null && DateTime.UtcNow - LastSeen <= _ipc.LinkTimeout; } }

        // ---------------------------------------------------------------- link latency
        private readonly LinkStats _latency = new LinkStats();
        private readonly object _latencyLock = new object();

        // Round-trip snapshot (null until the first probe answer).
        public LinkStats Latency
        {
            get
            {
                lock (_latencyLock)
                    return _latency.Samples == 0 ? null : new LinkStats
                    { LastMs = _latency.LastMs, AverageMs = _latency.AverageMs, PeakMs = _latency.PeakMs, Samples = _latency.Samples };
            }
        }

        internal void NoteRoundTrip(double ms)
        {
            lock (_latencyLock)
            {
                _latency.AverageMs = _latency.Samples == 0 ? ms : _latency.AverageMs * 0.8 + ms * 0.2;
                _latency.PeakMs = _latency.Samples == 0 ? ms : Math.Max(ms, _latency.PeakMs * 0.95);
                _latency.LastMs = ms;
                _latency.Samples++;
            }
        }

        // ---------------------------------------------------------------- capabilities / flags
        // Addon capability probes: nothing is assumed until the addon answers.
        public bool KeysConfirmed { get; internal set; }
        public bool KeysUnsupported { get; internal set; }
        public bool FollowConfirmed { get; internal set; }
        public bool FollowUnsupported { get; internal set; }

        // True while the player has the chat input open (typed keys would land in the text).
        // A stale "open" expires on its own, so one lost datagram can't freeze key input.
        public static readonly TimeSpan TypingStaleAfter = TimeSpan.FromSeconds(6);
        private long _typingSinceTicks;
        public bool IsTyping
        {
            get
            {
                long t = Interlocked.Read(ref _typingSinceTicks);
                return t != 0 && DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) < TypingStaleAfter;
            }
        }
        internal void SetTyping(bool open) { Interlocked.Exchange(ref _typingSinceTicks, open ? DateTime.UtcNow.Ticks : 0); }

        // ---------------------------------------------------------------- buff timers (0x063)
        private volatile List<BuffTimer> _buffTimers = new List<BuffTimer>();
        public DateTime BuffTimersAt { get; internal set; }
        public IReadOnlyList<BuffTimer> BuffTimers { get { return _buffTimers; } }
        internal void SetBuffTimers(List<BuffTimer> list) { _buffTimers = list; BuffTimersAt = DateTime.UtcNow; }

        // ---------------------------------------------------------------- inventory counts (GETINV)
        private volatile Dictionary<int, int> _invCounts;
        public DateTime InventoryCountsAt { get; internal set; }
        // item id -> total count in the main inventory, from the last RequestInventory() reply (null = none yet).
        public IReadOnlyDictionary<int, int> InventoryCounts { get { return _invCounts; } }
        internal void SetInventoryCounts(Dictionary<int, int> d) { _invCounts = d; InventoryCountsAt = DateTime.UtcNow; }

        // ---- weapons (EQP) ----
        private volatile EquipmentState _equipment;
        public EquipmentState Equipment { get { return _equipment; } }
        internal void SetEquipment(EquipmentState e) { _equipment = e; }

        // ---- own action results (CASTRES) ----
        private volatile CastResult _lastCast;
        public CastResult LastCastResult { get { return _lastCast; } }
        internal void SetCastResult(CastResult r) { _lastCast = r; }

        // ---- treasure pool (TPOOL / TLOTR) ----
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TreasureItem> _pool =
            new System.Collections.Concurrent.ConcurrentDictionary<int, TreasureItem>();

        // Everything currently in the pool, lowest slot first.
        public IReadOnlyList<TreasureItem> TreasurePool
        {
            get { var l = new List<TreasureItem>(_pool.Values); l.Sort((a, b) => a.Slot.CompareTo(b.Slot)); return l; }
        }
        internal void AddTreasure(TreasureItem t) { _pool[t.Slot] = t; }
        internal void ClearTreasureSlot(int slot) { TreasureItem gone; _pool.TryRemove(slot, out gone); }

        // ---- widescan (WSMOB) ----
        private volatile List<WidescanMob> _scan = new List<WidescanMob>();
        private DateTime _scanStarted = DateTime.MinValue;
        public IReadOnlyList<WidescanMob> WidescanMobs { get { return _scan; } }
        public DateTime WidescanAt { get; internal set; }
        internal void AddWidescan(WidescanMob m)
        {
            // Each sweep resends the whole zone: a mob arriving after a quiet gap starts a new list.
            var now = DateTime.UtcNow;
            if ((now - WidescanAt).TotalMilliseconds > 1500) { _scan = new List<WidescanMob>(); _scanStarted = now; }
            var list = new List<WidescanMob>(_scan);
            list.RemoveAll(x => x.Index == m.Index);
            list.Add(m);
            _scan = list;
            WidescanAt = now;
        }
        internal DateTime WidescanStartedAt { get { return _scanStarted; } }

        // ---------------------------------------------------------------- job points / stats / skills / temp items (SPX)
        private volatile PlayerExtras _extras;
        // null until the addon's first SPX (about a second after linking).
        public PlayerExtras Extras { get { return _extras; } }
        internal void SetExtras(PlayerExtras e) { _extras = e; }

        // ---------------------------------------------------------------- currency
        private volatile Currency _currency;
        public Currency Currency { get { return _currency; } }
        internal void MergeCurrency(int sparks, int accolades)
        {
            var prev = _currency;
            _currency = new Currency
            {
                Sparks = sparks >= 0 ? sparks : (prev != null ? prev.Sparks : -1),
                Accolades = accolades >= 0 ? accolades : (prev != null ? prev.Accolades : -1),
                ReceivedAt = DateTime.UtcNow,
            };
            _replySignal.Set();
        }

        // ---------------------------------------------------------------- NPC dialogs
        public int LastDialogMenuId { get; internal set; }
        public DateTime LastDialogOpenedAt { get; internal set; }
        internal int LastDialogOpenedTick;
        private volatile DialogChoice _lastChoice;
        public DialogChoice LastDialogChoice { get { return _lastChoice; } }
        internal void NoteChoice(DialogChoice c) { _lastChoice = c; }

        // Did an NPC dialog open after `sinceTick` (Environment.TickCount)? menu id out.
        public bool DialogOpenedSince(int sinceTick, out int menuId)
        {
            menuId = LastDialogMenuId;
            int at = LastDialogOpenedTick;
            return at != 0 && at - sinceTick >= 0;
        }

        // Did the client send a dialog choice after `sinceTick`?
        public bool DialogChoiceSince(int sinceTick, out DialogChoice choice)
        {
            choice = _lastChoice;
            return choice != null && choice.Tick - sinceTick >= 0;
        }

        // ---- exchange recorder: event + choices up to the final (non-automated) one ----
        private int _recNpcIndex, _recMenu;
        private List<DialogChoice> _recChoices;
        private volatile RecordedExchange _lastExchange;
        public RecordedExchange LastRecordedExchange { get { return _lastExchange; } }

        internal void RecordEventStart(NpcEvent e)
        {
            _recNpcIndex = e.NpcIndex; _recMenu = e.MenuId;
            _recChoices = new List<DialogChoice>();
        }

        // Returns the finished exchange when this choice completes one.
        internal RecordedExchange RecordChoice(DialogChoice c)
        {
            var rec = _recChoices;
            if (rec == null || c.MenuId != _recMenu) return null;
            rec.Add(c);
            if (c.Automated) return null;
            var done = new RecordedExchange { NpcIndex = _recNpcIndex, MenuId = _recMenu, Choices = rec.ToArray() };
            _recChoices = null;
            _lastExchange = done;
            return done;
        }

        // ---------------------------------------------------------------- hover shot
        public DateTime LastHoverParticleAt { get; internal set; }
        public DateTime LastHoverWeaponSkillAt { get; internal set; }

        // ---------------------------------------------------------------- reply waiting
        private readonly AutoResetEvent _replySignal = new AutoResetEvent(false);
        private volatile SellResult _sell; private int _sellTick;
        private volatile ReplayResult _replay; private int _replayTick;

        internal void SetSellResult(SellResult r) { _sellTick = Tick(); _sell = r; _replySignal.Set(); }
        internal void SetReplayResult(ReplayResult r) { _replayTick = Tick(); _replay = r; _replySignal.Set(); }

        internal static int Tick() { int t = Environment.TickCount; return t == 0 ? 1 : t; }

        private bool WaitFor(Func<bool> done, int timeoutMs)
        {
            int deadline = Tick() + timeoutMs;
            while (true)
            {
                if (done()) return true;
                int left = deadline - Tick();
                if (left <= 0) return false;
                _replySignal.WaitOne(Math.Min(left, 100));
            }
        }

        // ================================================================ commands

        public void Send(string message) { _ipc.Send(this, message); }

        // Movement. Run is a LEASE: the addon stops the character if not renewed within ~2s, so
        // keep calling it (e.g. every 0.5s) while you want it moving. Headings are Windower's.
        public void Run(double heading) { Send(Protocol.Out.Run + "|" + Protocol.F(heading)); }
        public void StopRunning() { Send(Protocol.Out.RunStop); }
        public void Turn(double heading) { Send(Protocol.Out.Turn + "|" + Protocol.F(heading)); }

        // Heading that runs from (x, y) toward (targetX, targetY) — Windower ground coordinates.
        public static double HeadingTo(double x, double y, double targetX, double targetY)
        {
            return -Math.Atan2(targetY - y, targetX - x);
        }

        // Keys (Windower `setkey` names: enter, escape, up, a, numpad4, f1, ...).
        public void KeyPress(string key, int holdMs = 80) { Send(Protocol.Out.KeyRaw + "|" + key + "|press|" + holdMs); }
        public void KeyDown(string key) { Send(Protocol.Out.KeyRaw + "|" + key + "|down"); }
        public void KeyUp(string key) { Send(Protocol.Out.KeyRaw + "|" + key + "|up"); }
        public void MenuKey(string key, int holdMs = 80) { Send(Protocol.Out.Key + "|" + key + "|" + holdMs); }
        public void MenuKeySequence(IEnumerable<string> keys, int holdMs = 80) { Send(Protocol.Out.KeySequence + "|" + string.Join(",", keys) + "|" + holdMs); }

        // Chat input / game commands: "/ma \"Cure IV\" <t>", "/echo hi"; "//cmd" runs a Windower command.
        public void Chat(string text) { if (!string.IsNullOrEmpty(text)) Send(Protocol.Out.ChatInput + "|" + text); }

        // Targeting by client entity index.
        public void SetTarget(int index) { Send(Protocol.Out.SetTarget + "|" + index); }          // reticle only
        public void EngageOrSwitch(int index) { Send(Protocol.Out.Target + "|" + index); }       // server-notified
        public void Disengage() { Send(Protocol.Out.Disengage); }
        public void AcceptRaise() { Send(Protocol.Out.AcceptRaise); }
        public void Follow(int index) { Send(Protocol.Out.Follow + "|" + index); }                // Windower native follow
        public void StopFollow() { Send(Protocol.Out.FollowStop); }
        public void ChangeJob(int mainJobId, int subJobId) { Send(Protocol.Out.JobChange + "|" + mainJobId + "|" + subJobId); }

        // On-screen output in the game client.
        public void Notify(string text) { Send(Protocol.Out.Notify + "|" + text); }
        public void Banner(string text) { Send(Protocol.Out.Verbose + "|" + text); }
        public void Hud(string line1, string line2) { Send(Protocol.Out.Hud + "|" + line1 + "~" + line2); }

        // NPCs.
        public void PokeNpc(int index, string npcName = null) { Send(Protocol.Out.NpcPoke + "|" + index + "|" + (npcName ?? "")); }

        // Sells every main-inventory stack of these item ids to the shop NPC; blocks for the result.
        public SellResult Sell(int npcIndex, IEnumerable<int> itemIds, int timeoutMs)
        {
            int since = Tick();
            Send(Protocol.Out.Sell + "|" + npcIndex + "|" + string.Join(",", itemIds));
            if (WaitFor(() => _sell != null && _sellTick - since >= 0, timeoutMs)) return _sell;
            return new SellResult { Error = "timed out waiting for the addon" };
        }

        // Replays a recorded NPC exchange (RecordedExchange.ReplayChoices) with the menu hidden.
        public ReplayResult NpcReplay(int npcIndex, int menuId, string replayChoices, int timeoutMs, bool hideMenu = true)
        {
            int since = Tick();
            Send(Protocol.Out.NpcReplay + "|" + npcIndex + "|" + menuId + "|" + (hideMenu ? 1 : 0) + "|" + replayChoices);
            if (WaitFor(() => _replay != null && _replayTick - since >= 0, timeoutMs)) return _replay;
            return new ReplayResult { Success = false, Error = "timed out waiting for the addon" };
        }

        public void RequestCurrency() { Send(Protocol.Out.CurrencyRequest); }

        // Asks the server for fresh currency totals and waits for them (null on timeout).
        public Currency QueryCurrency(int timeoutMs)
        {
            var before = _currency;
            RequestCurrency();
            if (WaitFor(() => _currency != null && !ReferenceEquals(_currency, before), timeoutMs)) return _currency;
            return null;
        }

        public void RequestInventory() { Send(Protocol.Out.InventoryRequest); }

        // Treasure pool: lot or pass one slot, or every slot currently pooled.
        public void Lot(int slot) { Send(Protocol.Out.Lot + "|" + slot); }
        public void Pass(int slot) { Send(Protocol.Out.Pass + "|" + slot); }
        public void LotAll() { foreach (var t in TreasurePool) Lot(t.Slot); }
        public void PassAll() { foreach (var t in TreasurePool) Pass(t.Slot); }

        // Ask the server for a zone-wide mob sweep; results arrive as WidescanMobs / WidescanCompleted.
        public void Widescan() { Send(Protocol.Out.Widescan); }

        public override string ToString() { return Name + (IsLinked ? " (linked)" : " (not linked)"); }
    }
}
