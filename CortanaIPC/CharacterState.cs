using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Cortana
{
    // Live game state of one character, kept current by the addon's state feed (state.lua).
    //
    // Threading: the IPC receive thread is the only writer. Snapshots (Player, Party, Recasts,
    // Inventory, KnownSpells) are immutable objects swapped by reference, so reads never lock and
    // never see a half-updated value. The entity table is the one mutable structure (own lock);
    // the EntityState objects in it are immutable, so GetEntity hands them out directly.
    public sealed class CharacterState
    {
        // Past these, data is treated as absent (link down / client zoning / addon unloaded).
        public static readonly TimeSpan PlayerStaleAfter = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan EntityFeedStaleAfter = TimeSpan.FromSeconds(4);   // no entity message at all
        public static readonly TimeSpan EntityMaxAge = TimeSpan.FromSeconds(8);           // one entity not refreshed

        private static readonly IReadOnlyList<int> NoBuffs = new int[0];
        private static readonly PetState NoPet = new PetState { Mpp = -1, Tp = -1, Name = "" };

        public PlayerState Player { get { return _player; } }
        public PetState Pet { get { return _pet; } }
        public IReadOnlyList<PartyMemberState> Party { get { return _party; } }
        public RecastState Recasts { get { return _recasts; } }
        public InventoryState Inventory { get { return _inventory; } }
        public IReadOnlyCollection<int> KnownSpells { get { return _knownSpells; } }
        public IReadOnlyCollection<int> KnownAbilities { get { return _knownAbilities; } }

        private volatile PlayerState _player;
        private volatile PetState _pet = NoPet;
        private volatile List<PartyMemberState> _party = new List<PartyMemberState>();
        private volatile RecastState _recasts;
        private volatile InventoryState _inventory;
        private volatile HashSet<int> _knownSpells = new HashSet<int>();
        private volatile HashSet<int> _knownAbilities = new HashSet<int>();

        // True while the player snapshot is recent (the addon is linked and streaming).
        public bool IsFresh
        {
            get { var p = _player; return p != null && DateTime.UtcNow - p.ReceivedAt <= PlayerStaleAfter; }
        }

        // Login state as the app should treat it: a stale feed reads as Loading.
        public LoginState Login
        {
            get
            {
                var p = _player;
                if (p == null) return LoginState.LoggedOut;
                return IsFresh ? p.Login : LoginState.Loading;
            }
        }

        public TimeSpan? FeedAge
        {
            get { var p = _player; return p == null ? (TimeSpan?)null : DateTime.UtcNow - p.ReceivedAt; }
        }

        public bool KnowsSpell(int spellId) { return _knownSpells.Contains(spellId); }
        public bool KnowsAbility(int jobAbilityId) { return _knownAbilities.Contains(jobAbilityId); }

        // ---------------------------------------------------------------- player
        // SPL|name|id|index|hp|hpp|mp|mpp|maxmp|tp|x|y|z|h|status|zone|mjob|mlvl|sjob|slvl|login|castpct|petidx|tidx|stidx|locked|buffs|menu
        internal StateChanges IngestPlayer(string[] a)
        {
            var buffs = new List<int>();
            string list = Protocol.Str(a, 26);
            if (list.Length > 0)
                foreach (var tok in list.Split(','))
                {
                    int b;
                    if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) buffs.Add(b);
                }
            var s = new PlayerState
            {
                Id = Protocol.UInt(a, 2), Index = Protocol.Int(a, 3),
                Hp = Protocol.Int(a, 4), Hpp = Protocol.Int(a, 5), Mp = Protocol.Int(a, 6), Mpp = Protocol.Int(a, 7),
                MaxMp = Protocol.Int(a, 8), Tp = Protocol.Int(a, 9),
                X = Protocol.Float(a, 10), Y = Protocol.Float(a, 11), Z = Protocol.Float(a, 12), Heading = Protocol.Float(a, 13),
                Status = Protocol.Int(a, 14), Zone = Protocol.Int(a, 15),
                MainJob = Protocol.Int(a, 16), MainJobLevel = Protocol.Int(a, 17),
                SubJob = Protocol.Int(a, 18), SubJobLevel = Protocol.Int(a, 19),
                Login = (LoginState)Math.Max(0, Math.Min(2, Protocol.Int(a, 20))),
                CastProgress = Protocol.Float(a, 21),
                PetIndex = Protocol.Int(a, 22), TargetIndex = Protocol.Int(a, 23), SubTargetIndex = Protocol.Int(a, 24),
                TargetLocked = Protocol.Int(a, 25) == 1,
                Buffs = buffs.Count == 0 ? NoBuffs : buffs.ToArray(),
                EventMenuId = Protocol.Int(a, 27),
                ReceivedAt = DateTime.UtcNow,
            };
            var prev = _player;
            _player = s;
            return Diff(prev, s);
        }

        private static StateChanges Diff(PlayerState a, PlayerState b)
        {
            if (a == null) return StateChanges.Vitals | StateChanges.Status | StateChanges.Login | StateChanges.Zone | StateChanges.Tp;
            var c = StateChanges.None;
            if (a.Hp != b.Hp || a.Hpp != b.Hpp) c |= StateChanges.Vitals;
            if (a.Status != b.Status) c |= StateChanges.Status;
            if (a.Zone != b.Zone) c |= StateChanges.Zone;
            if (a.Login != b.Login) c |= StateChanges.Login;
            if (a.TargetIndex != b.TargetIndex || a.PetIndex != b.PetIndex) c |= StateChanges.Target;
            if (a.Tp != b.Tp) c |= StateChanges.Tp;
            if ((a.Tp >= 1000) != (b.Tp >= 1000)) c |= StateChanges.TpReady;
            if (a.IsCasting != b.IsCasting) c |= StateChanges.Casting;
            if (a.Buffs.Count != b.Buffs.Count) c |= StateChanges.Buffs;
            else for (int i = 0; i < a.Buffs.Count; i++) if (a.Buffs[i] != b.Buffs[i]) { c |= StateChanges.Buffs; break; }
            return c;
        }

        // ---------------------------------------------------------------- pet
        // PET|name|index|hpp|mpp|tp|targetId|petName   (mpp/tp -1 = not reported yet; index 0 = no pet)
        internal StateChanges IngestPet(string[] a)
        {
            var s = new PetState
            {
                Index = Protocol.Int(a, 2), Hpp = Protocol.Int(a, 3),
                Mpp = a.Length > 4 ? Protocol.Int(a, 4) : -1, Tp = a.Length > 5 ? Protocol.Int(a, 5) : -1,
                TargetId = Protocol.UInt(a, 6), Name = Protocol.Str(a, 7),
                ReceivedAt = DateTime.UtcNow,
            };
            if (s.Index <= 0) s = NoPet;
            var prev = _pet;
            _pet = s;
            var c = StateChanges.None;
            if (prev.Index != s.Index || prev.Hpp != s.Hpp || prev.Mpp != s.Mpp || prev.TargetId != s.TargetId
                || (prev.Tp >= 1000) != (s.Tp >= 1000))
                c |= StateChanges.Pet;
            if (prev.Tp != s.Tp) c |= StateChanges.Tp;
            return c;
        }

        // ---------------------------------------------------------------- entities
        private sealed class Slot { public EntityState E; public int Frame; }
        private readonly object _entLock = new object();
        private readonly Dictionary<int, Slot> _ents = new Dictionary<int, Slot>();
        private int _frame, _framePartsSeen, _frameParts;
        private DateTime _entityFeedAt = DateTime.MinValue;

        // SEN|name|frame|part|parts|rec;rec;...   frame 0 = delta (upsert only); frame > 0 = framed full resync
        // rec = index,id,name,hpp,x,y,z,h,dist,status,claim,spawn,model,petidx,tidx,valid,race
        internal void IngestEntities(string[] a)
        {
            int frame = Protocol.Int(a, 2), parts = Protocol.Int(a, 4);
            string body = Protocol.Str(a, 5);
            var now = DateTime.UtcNow;
            lock (_entLock)
            {
                _entityFeedAt = now;
                if (body.Length > 0)
                {
                    foreach (var rec in body.Split(';'))
                    {
                        var e = ParseEntity(rec.Split(','), now);
                        if (e == null) continue;
                        Slot cur;
                        if (!_ents.TryGetValue(e.Index, out cur)) { cur = new Slot(); _ents[e.Index] = cur; }
                        cur.E = e;
                        if (frame > 0) cur.Frame = frame;
                    }
                }
                if (frame <= 0) return;

                if (frame != _frame) { _frame = frame; _framePartsSeen = 0; _frameParts = parts; }
                if (++_framePartsSeen < _frameParts) return;

                // Full resync complete: drop entities it doesn't list, unless a delta touched them
                // after the resync began.
                var gone = new List<int>();
                foreach (var kv in _ents)
                    if (kv.Value.Frame != frame && (now - kv.Value.E.UpdatedAt).TotalSeconds > 0.3) gone.Add(kv.Key);
                foreach (var k in gone) _ents.Remove(k);
            }
        }

        // SEX|name|index,index,...
        internal void IngestEntityExit(string[] a)
        {
            lock (_entLock)
            {
                _entityFeedAt = DateTime.UtcNow;
                foreach (var tok in Protocol.Str(a, 2).Split(','))
                {
                    int idx;
                    if (int.TryParse(tok, out idx)) _ents.Remove(idx);
                }
            }
        }

        private static EntityState ParseEntity(string[] f, DateTime now)
        {
            if (f.Length < 17) return null;
            int index = Protocol.Int(f, 0);
            if (index <= 0) return null;
            return new EntityState
            {
                Index = index, Id = Protocol.UInt(f, 1), Name = Protocol.Str(f, 2), Hpp = Protocol.Int(f, 3),
                X = Protocol.Float(f, 4), Y = Protocol.Float(f, 5), Z = Protocol.Float(f, 6), Heading = Protocol.Float(f, 7),
                Distance = Protocol.Float(f, 8), Status = Protocol.Int(f, 9), ClaimId = Protocol.UInt(f, 10),
                SpawnType = Protocol.Int(f, 11), ModelSize = Protocol.Float(f, 12), PetIndex = Protocol.Int(f, 13),
                TargetIndex = Protocol.Int(f, 14), ValidTarget = Protocol.Int(f, 15) == 1, Race = Protocol.Int(f, 16),
                UpdatedAt = now,
            };
        }

        private bool Live(Slot s, DateTime now)
        {
            return now - _entityFeedAt <= EntityFeedStaleAfter && now - s.E.UpdatedAt <= EntityMaxAge;
        }

        // The entity at this client index, or null when absent / stale.
        public EntityState GetEntity(int index)
        {
            lock (_entLock)
            {
                Slot s;
                return _ents.TryGetValue(index, out s) && Live(s, DateTime.UtcNow) ? s.E : null;
            }
        }

        // Every live entity (typically 50-300). Prefer this to probing indices.
        public List<EntityState> Entities()
        {
            var list = new List<EntityState>();
            var now = DateTime.UtcNow;
            lock (_entLock)
                foreach (var s in _ents.Values)
                    if (Live(s, now)) list.Add(s.E);
            return list;
        }

        public EntityState FindEntityById(uint serverId)
        {
            var now = DateTime.UtcNow;
            lock (_entLock)
                foreach (var s in _ents.Values)
                    if (s.E.Id == serverId && Live(s, now)) return s.E;
            return null;
        }

        // ---------------------------------------------------------------- party
        // SPT|name|rec;rec   rec = slot,name,id,index,hp,hpp,mp,mpp,tp,zone,mjob,mlvl,sjob,slvl
        internal StateChanges IngestParty(string[] a)
        {
            var list = new List<PartyMemberState>();
            string body = Protocol.Str(a, 2);
            if (body.Length > 0)
                foreach (var rec in body.Split(';'))
                {
                    var f = rec.Split(',');
                    if (f.Length < 14) continue;
                    list.Add(new PartyMemberState
                    {
                        Slot = Protocol.Int(f, 0), Name = Protocol.Str(f, 1), Id = Protocol.UInt(f, 2), Index = Protocol.Int(f, 3),
                        Hp = Protocol.Int(f, 4), Hpp = Protocol.Int(f, 5), Mp = Protocol.Int(f, 6), Mpp = Protocol.Int(f, 7),
                        Tp = Protocol.Int(f, 8), Zone = Protocol.Int(f, 9),
                        MainJob = Protocol.Int(f, 10), MainJobLevel = Protocol.Int(f, 11),
                        SubJob = Protocol.Int(f, 12), SubJobLevel = Protocol.Int(f, 13),
                    });
                }
            var prev = _party;
            _party = list;
            var c = StateChanges.None;
            if (PartyChanged(prev, list)) c |= StateChanges.Party;
            if (PartyTpChanged(prev, list)) c |= StateChanges.Tp;
            return c;
        }

        // TP is deliberately not part of PartyChanged: it moves on its own (StateChanges.Tp) so a TP
        // tick does not look like a membership or HP change to anyone filtering on Party.
        private static bool PartyTpChanged(List<PartyMemberState> a, List<PartyMemberState> b)
        {
            if (a == null || a.Count != b.Count) return b.Count > 0;
            for (int i = 0; i < b.Count; i++)
                if (a[i].Name != b[i].Name || a[i].Tp != b[i].Tp) return true;
            return false;
        }

        private static bool PartyChanged(List<PartyMemberState> a, List<PartyMemberState> b)
        {
            if (a == null || a.Count != b.Count) return true;
            for (int i = 0; i < b.Count; i++)
                if (a[i].Name != b[i].Name || a[i].Hp != b[i].Hp || a[i].Hpp != b[i].Hpp || a[i].Zone != b[i].Zone)
                    return true;
            return false;
        }

        // ---------------------------------------------------------------- recasts
        // SRC|name|rid=secs,...|sid=frames,...
        internal StateChanges IngestRecasts(string[] a)
        {
            var ab = new Dictionary<int, float>();
            foreach (var kv in Protocol.Str(a, 2).Split(','))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                int id; float secs;
                if (int.TryParse(kv.Substring(0, eq), out id)
                    && float.TryParse(kv.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                    ab[id] = secs;
            }
            var sp = new Dictionary<int, float>();
            foreach (var kv in Protocol.Str(a, 3).Split(','))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                int id, frames;
                if (int.TryParse(kv.Substring(0, eq), out id) && int.TryParse(kv.Substring(eq + 1), out frames))
                    sp[id] = frames / 60f;   // the feed sends 1/60 s frames
            }
            var prev = _recasts;
            _recasts = new RecastState { AbilitySeconds = ab, SpellSeconds = sp, ReceivedAt = DateTime.UtcNow };
            return SomethingCameReady(prev, ab, sp) ? StateChanges.RecastReady : StateChanges.None;
        }

        private static bool SomethingCameReady(RecastState prev, Dictionary<int, float> ab, Dictionary<int, float> sp)
        {
            if (prev == null) return true;
            foreach (var kv in prev.AbilitySeconds)
            {
                float now;
                if (kv.Value > 0 && (!ab.TryGetValue(kv.Key, out now) || now <= 0)) return true;
            }
            foreach (var id in prev.SpellSeconds.Keys)
                if (!sp.ContainsKey(id)) return true;   // only non-zero spell recasts are sent
            return false;
        }

        // ---------------------------------------------------------------- inventory / spells
        // SIV|name|max|slot:id:count,...
        internal void IngestInventory(string[] a)
        {
            var slots = new Dictionary<int, InventoryItem>();
            foreach (var rec in Protocol.Str(a, 3).Split(','))
            {
                var f = rec.Split(':');
                if (f.Length < 3) continue;
                int slot = Protocol.Int(f, 0);
                slots[slot] = new InventoryItem { Slot = slot, ItemId = Protocol.Int(f, 1), Count = Protocol.Int(f, 2) };
            }
            _inventory = new InventoryState { Capacity = Protocol.Int(a, 2), Slots = slots, ReceivedAt = DateTime.UtcNow };
        }

        // SSP|name|id,id,...
        internal void IngestSpells(string[] a)
        {
            var set = new HashSet<int>();
            foreach (var tok in Protocol.Str(a, 2).Split(','))
            {
                int id;
                if (int.TryParse(tok, out id)) set.Add(id);
            }
            _knownSpells = set;
        }

        // SAB|name|id,id,...   (job ability ids, Windower's res/job_abilities)
        internal void IngestAbilities(string[] a)
        {
            var set = new HashSet<int>();
            foreach (var tok in Protocol.Str(a, 2).Split(','))
            {
                int id;
                if (int.TryParse(tok, out id)) set.Add(id);
            }
            _knownAbilities = set;
        }

        // ---------------------------------------------------------------- chat
        public const int ChatQueueCapacity = 1000;
        private readonly ConcurrentQueue<ChatLine> _chat = new ConcurrentQueue<ChatLine>();
        private readonly AutoResetEvent _chatArrived = new AutoResetEvent(false);

        // SCH|name|mode|text   (text may itself have contained '|' — the tail is rejoined)
        internal ChatLine IngestChat(string[] a)
        {
            string text = a.Length > 3 ? string.Join("|", a, 3, a.Length - 3) : "";
            var line = new ChatLine { Mode = Protocol.Int(a, 2), Text = text, ReceivedAt = DateTime.UtcNow };
            _chat.Enqueue(line);
            ChatLine drop;
            while (_chat.Count > ChatQueueCapacity && _chat.TryDequeue(out drop)) { }
            _chatArrived.Set();
            return line;
        }

        // Chat lines are also queued for pollers (the CortanaIPC.ChatReceived event fires too).
        // Meant for ONE consuming thread per character.
        public bool TryReadChat(out ChatLine line) { return _chat.TryDequeue(out line); }

        // Blocks until a chat line is queued or the timeout passes (true = one is available).
        public bool WaitForChat(int timeoutMs)
        {
            if (!_chat.IsEmpty) return true;
            return _chatArrived.WaitOne(timeoutMs);
        }

        internal void Reset()
        {
            _player = null;
            _party = new List<PartyMemberState>();
            _recasts = null;
            _inventory = null;
            lock (_entLock) { _ents.Clear(); _frame = 0; }
            ChatLine drop;
            while (_chat.TryDequeue(out drop)) { }
        }
    }
}
