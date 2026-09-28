using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cortana
{
    // Wire protocol between an app and the Windower "cortana" addon: UDP on localhost,
    // one message per datagram, UTF-8, fields separated by '|':  TYPE|field|field|...
    // For messages FROM the addon the second field is almost always the sender's character name.
    // Full field-by-field reference: README.md.
    public static class Protocol
    {
        // Apps bind one port in DefaultPort .. DefaultPort + PortRange - 1; the addon looks for
        // apps on every port in that range, so several apps can be linked at once.
        public const int DefaultPort = 59332;
        public const int PortRange = 10;

        // ---- addon -> app ----
        public static class In
        {
            public const string Hello = "HELLO";            // HELLO|name|serverId|zone|version|state1|windowerPath
            public const string Ping = "PING";              // PING|name
            public const string EchoReply = "ECHOR";        // ECHOR|name|timestamp
            public const string Player = "SPL";             // player snapshot
            public const string Entities = "SEN";           // entity records (deltas / framed full resync)
            public const string EntityExit = "SEX";         // entities that left the client's array
            public const string Party = "SPT";              // party / alliance
            public const string Pet = "PET";                // own pet: index, HP%, MP%, TP, target
            public const string Recasts = "SRC";            // ability + spell recasts
            public const string KnownSpells = "SSP";        // spells the character knows
            public const string KnownAbilities = "SAB";     // job abilities the character has
            public const string PlayerExtras = "SPX";       // job points, stats, skills, temporary items
            public const string Cast = "CAST";              // own spell / item use: begin, interrupt, finish
            public const string CastResult = "CASTRES";     // own action resolved (0x028): outcome + amount
            public const string ActionMessage = "AMSG";     // action message involving the party (0x029)
            public const string TreasureFound = "TPOOL";    // item added to the treasure pool (0x0D2)
            public const string TreasureResult = "TLOTR";   // pool slot lotted / resolved (0x0D3)
            public const string WidescanMob = "WSMOB";      // one mob from a widescan sweep (0x0F4)
            public const string Equipment = "EQP";          // equipped weapons + ammo
            public const string Chat = "SCH";               // one chat-log line
            public const string Inventory = "SIV";          // main inventory by slot (streamed)
            public const string InventoryCounts = "INV";    // main inventory (on request, GETINV)
            public const string Buffs = "BUFFS";            // own buffs + remaining seconds (0x063)
            public const string PartyBuffs = "PBUFF";       // one party member's buff ids (0x076) — field 2 is the MEMBER
            public const string Combat = "COMBAT";          // combat capture, key=value fields (skillchains: type=SC)
            public const string MobAction = "MOBACT";
            public const string MobResult = "MOBRES";
            public const string PlayerAction = "PLRACT";
            public const string DebuffWear = "DEBUFFWEAR";
            public const string JobAbility = "ROLL";        // own job ability resolved (roll totals)
            public const string AutoTarget = "AUTOTARGET";  // the game auto-targeted (message 234)
            public const string Command = "CMD";            // //cortana command typed in game
            public const string DialogOpen = "DLGOPEN";
            public const string DialogFail = "DLGFAIL";
            public const string DialogChoice = "DLGOPT";
            public const string NpcEvent = "EVT";
            public const string NpcEventUpdate = "EVTUPD";
            public const string Currency = "CUR";
            public const string SellDone = "SELLDONE";
            public const string ReplayDone = "REPLAYDONE";
            public const string Typing = "TYPING";
            public const string KeyAck = "KEYACK";
            public const string KeyNak = "KEYNAK";
            public const string FollowAck = "FOLLOWACK";
            public const string FollowNak = "FOLLOWNAK";
            public const string Hover = "HOVER";
            public const string Desync = "DESYNC";
            public const string JobSent = "JOBSENT";
            public const string JobFail = "JOBFAIL";
        }

        // ---- app -> addon ----
        public static class Out
        {
            public const string Welcome = "WELCOME";        // WELCOME|cortana|appName
            public const string Bye = "BYE";                // leaving: the addon drops this app at once
            public const string Pong = "PONG";
            public const string Echo = "ECHO";
            public const string Run = "RUN";                // RUN|heading   (lease: renew within 2s)
            public const string RunStop = "RUNSTOP";
            public const string Turn = "TURN";              // TURN|heading
            public const string KeyRaw = "KEYRAW";          // KEYRAW|key|down/up/press[|holdMs]
            public const string Key = "KEY";                // KEY|name|holdMs  (menu-navigation key names)
            public const string KeySequence = "KEYSEQ";     // KEYSEQ|k1,k2,...|holdMs
            public const string ChatInput = "CHATIN";       // CHATIN|text  ("//cmd" runs a Windower command)
            public const string SetTarget = "SMTARGET";     // SMTARGET|index   (reticle only)
            public const string Target = "TARGET";         // TARGET|index     (engage if idle / switch if engaged)
            public const string Disengage = "DISENGAGE";
            public const string AcceptRaise = "RAISE";
            public const string Follow = "FOLLOW";          // FOLLOW|index
            public const string FollowStop = "FOLLOWSTOP";
            public const string NpcPoke = "NPCPOKE";        // NPCPOKE|index|name
            public const string Sell = "SELL";              // SELL|npcIndex|itemId,itemId,...
            public const string NpcReplay = "NPCREPLAY";    // NPCREPLAY|npcIndex|menuId|hide|opt:unk:auto,...
            public const string CurrencyRequest = "CURREQ";
            public const string InventoryRequest = "GETINV";
            public const string Lot = "LOT";                // LOT|slot   (treasure pool)
            public const string Pass = "PASS";              // PASS|slot
            public const string Widescan = "WIDESCAN";      // request a zone-wide mob sweep
            public const string JobChange = "JOBCHANGE";    // JOBCHANGE|mainJobId|subJobId  (0 = leave as is)
            public const string Hud = "HUD";                // HUD|line1~line2
            public const string Verbose = "VERBOSE";        // VERBOSE|text  (big on-screen banner)
            public const string Notify = "NOTIFY";          // NOTIFY|text   (chat-log echo)
        }

        // ---- field helpers (invariant culture; missing / bad fields read as 0 / "") ----
        public static string Str(string[] f, int i) { return i < f.Length ? f[i] : ""; }
        public static int Int(string[] f, int i) { int v; return i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
        public static uint UInt(string[] f, int i) { uint v; return i < f.Length && uint.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0u; }
        public static long Long(string[] f, int i) { long v; return i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0L; }
        public static float Float(string[] f, int i) { float v; return i < f.Length && float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f; }

        // "a=1;b=two" -> { a: "1", b: "two" }
        public static Dictionary<string, string> KeyValues(string kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(kv)) return d;
            foreach (var pair in kv.Split(';'))
            {
                int i = pair.IndexOf('=');
                if (i > 0) d[pair.Substring(0, i)] = pair.Substring(i + 1);
            }
            return d;
        }

        internal static int KvInt(Dictionary<string, string> kv, string key)
        {
            string s; int v;
            return kv.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        internal static long KvLong(Dictionary<string, string> kv, string key)
        {
            string s; long v;
            return kv.TryGetValue(key, out s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0L;
        }

        internal static string KvStr(Dictionary<string, string> kv, string key)
        {
            string s;
            return kv.TryGetValue(key, out s) ? s : "";
        }

        // Formats a float for the wire (invariant, compact).
        internal static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    }
}
