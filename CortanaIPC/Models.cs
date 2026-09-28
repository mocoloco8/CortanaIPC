using System;
using System.Collections.Generic;

namespace Cortana
{
    // Data models for everything the Windower "cortana" addon reports.
    //
    // Conventions (Windower's own, so they read the same as the Lua side):
    //   * positions: x / y = ground plane, z = height; headings in radians (Windower's `facing`);
    //   * ids are Windower ids (spell id, job ability id, item id, buff id, zone id, job id);
    //   * entity "index" = the client's entity-table index, "id" = the server id;
    //   * every timestamp is UTC.
    // All models are read-only snapshots: the library replaces them, it never mutates one you hold.

    public enum LoginState
    {
        LoggedOut = 0,
        Loading = 1,   // logged in but zoning / no player entity yet
        InWorld = 2,
    }

    // What changed in a state update — only decision-relevant changes are flagged (positions and
    // cast progress are deliberately not, so handlers aren't woken at frame rate while moving).
    [Flags]
    public enum StateChanges
    {
        None = 0,
        Vitals = 1 << 0,        // HP / HP% changed
        Status = 1 << 1,        // idle / engaged / dead / event / ...
        Buffs = 1 << 2,         // a buff or debuff gained / lost
        Target = 1 << 3,        // target or pet changed
        Zone = 1 << 4,
        Login = 1 << 5,
        Casting = 1 << 6,       // a cast started or ended
        TpReady = 1 << 7,       // TP crossed 1000 either way
        Party = 1 << 8,         // membership or a member's HP changed
        RecastReady = 1 << 9,   // a job ability or spell came off cooldown
        Pet = 1 << 10,          // pet summoned / released, or its HP%, MP%, target changed, or its TP crossed 1000
        Tp = 1 << 11,           // TP moved at all (own, a party member's, or the pet's) — fires on every change
    }

    // What the server said about an action, decoded from its action-message id.
    public enum ActionOutcome
    {
        Unknown = 0,
        Landed,         // it worked (damage, cure, a status applied)
        Resisted,
        NoEffect,       // immune / already has it
        Interrupted,    // the cast was broken
        Missed,
        OutOfRange,     // too far, or the target can't be seen
        Unable,         // can't act at all: silenced, stunned, wrong state, requirement unmet
    }

    // Maps FFXI action-message ids to an outcome. Ids come from Windower's res/action_messages.
    public static class ActionMessages
    {
        private static readonly HashSet<int> _resisted = new HashSet<int> { 85, 197, 284, 653, 654, 655, 656, 671 };
        private static readonly HashSet<int> _noEffect = new HashSet<int> { 75, 156, 189, 248, 283, 312, 323, 336, 355, 408, 422, 423, 425, 659 };
        private static readonly HashSet<int> _interrupted = new HashSet<int> { 16, 68, 218, 220 };
        private static readonly HashSet<int> _missed = new HashSet<int> { 15, 63, 158, 188, 245, 324, 354, 592, 658 };
        private static readonly HashSet<int> _outOfRange = new HashSet<int> { 4, 5, 78, 154, 308, 313 };
        private static readonly HashSet<int> _unable = new HashSet<int> { 17, 18, 49, 55, 56, 87, 88, 89, 90, 104, 198, 328, 477, 540, 545, 561, 579, 580, 581, 695 };
        private static readonly HashSet<int> _gained = new HashSet<int>
        {
            142, 144, 145, 146, 147, 166, 186, 194, 205, 230, 237, 243, 266, 267, 268, 269, 278, 280,
            319, 320, 375, 412, 414, 415, 416, 420, 421, 424, 432, 433, 441, 532, 602, 668, 672, 755, 804
        };

        public static ActionOutcome Classify(int messageId)
        {
            if (_resisted.Contains(messageId)) return ActionOutcome.Resisted;
            if (_noEffect.Contains(messageId)) return ActionOutcome.NoEffect;
            if (_interrupted.Contains(messageId)) return ActionOutcome.Interrupted;
            if (_missed.Contains(messageId)) return ActionOutcome.Missed;
            if (_outOfRange.Contains(messageId)) return ActionOutcome.OutOfRange;
            if (_unable.Contains(messageId)) return ActionOutcome.Unable;
            if (messageId > 0) return ActionOutcome.Landed;
            return ActionOutcome.Unknown;
        }

        // TRUE for the messages that announce a status effect being gained.
        public static bool IsStatusGained(int messageId) { return _gained.Contains(messageId); }

        // TRUE when the action failed for a reason that will still be true if it is repeated at once.
        public static bool IsHardFailure(ActionOutcome o)
        {
            return o == ActionOutcome.NoEffect || o == ActionOutcome.Unable;
        }
    }

    // The result of one of OUR OWN actions, from the action packet (0x028).
    public sealed class CastResult
    {
        public int Category { get; internal set; }   // 2 ranged, 3 weapon skill, 4 spell, 5 item, 6 job ability
        public int ActionId { get; internal set; }
        public string Name { get; internal set; }
        public uint TargetId { get; internal set; }
        public int Targets { get; internal set; }
        public int MessageId { get; internal set; }
        public int Param { get; internal set; }      // damage, cure amount, or the status applied
        public int TotalValue { get; internal set; } // summed over every target
        public DateTime At { get; internal set; }

        public ActionOutcome Outcome { get { return ActionMessages.Classify(MessageId); } }
        public bool IsSpell { get { return Category == 4; } }
    }

    // One action message (0x029) involving our party — the game's own wording for what happened.
    public sealed class ActionMessage
    {
        public int MessageId { get; internal set; }
        public uint ActorId { get; internal set; }
        public uint TargetId { get; internal set; }
        public int ActorIndex { get; internal set; }
        public int TargetIndex { get; internal set; }
        public int Param1 { get; internal set; }
        public int Param2 { get; internal set; }
        public DateTime At { get; internal set; }

        public ActionOutcome Outcome { get { return ActionMessages.Classify(MessageId); } }
    }

    // An item sitting in the party's treasure pool.
    public sealed class TreasureItem
    {
        public int Slot { get; internal set; }
        public int ItemId { get; internal set; }
        public string Name { get; internal set; }
        public int Count { get; internal set; }
        public uint DropperId { get; internal set; }
        public bool Old { get; internal set; }       // was already in the pool when we joined
        public DateTime FoundAt { get; internal set; }
    }

    // How a pool slot ended: won, passed by everyone, or timed out to the floor.
    public sealed class TreasureResult
    {
        public int Slot { get; internal set; }
        public int Drop { get; internal set; }        // 0 still pooled, 1 went to a player, 3 floored
        public int HighestLot { get; internal set; }
        public string WinnerName { get; internal set; }
        public string LotterName { get; internal set; }
        public int Lot { get; internal set; }         // this lotter's roll (65535 = passed)
        public DateTime At { get; internal set; }

        public bool Passed { get { return Lot == 65535; } }
        public bool Finished { get { return Drop != 0; } }
    }

    // One mob from a widescan sweep: zone-wide, including mobs the client hasn't rendered.
    public sealed class WidescanMob
    {
        public int Index { get; internal set; }
        public int Level { get; internal set; }
        public string Type { get; internal set; }
        public int X { get; internal set; }          // map offsets, not world coordinates
        public int Y { get; internal set; }
        public string Name { get; internal set; }
        public DateTime At { get; internal set; }
    }

    // What the character is wearing in the weapon slots.
    public sealed class EquipmentState
    {
        public int MainId { get; internal set; }
        public string MainName { get; internal set; }
        public int MainSkill { get; internal set; }   // weapon skill type, for the usable-WS list
        public int SubId { get; internal set; }
        public string SubName { get; internal set; }
        public int RangedId { get; internal set; }
        public string RangedName { get; internal set; }
        public int RangedSkill { get; internal set; }
        public int AmmoId { get; internal set; }
        public string AmmoName { get; internal set; }
        public int AmmoCount { get; internal set; }
        public DateTime At { get; internal set; }

        public bool HasRanged { get { return RangedId > 0; } }
        public bool HasAmmo { get { return AmmoId > 0 && AmmoCount > 0; } }
    }

    // The character's own pet (avatar, wyvern, jug pet, automaton, luopan...).
    // HP% is always known; MP% and TP come from the server's pet status packets (0x067/0x068) and
    // read -1 until the first one arrives after the pet appears.
    public sealed class PetState
    {
        public int Index { get; internal set; }         // entity index, 0 = no pet
        public int Hpp { get; internal set; }
        public int Mpp { get; internal set; }           // -1 = not reported yet
        public int Tp { get; internal set; }            // -1 = not reported yet
        public uint TargetId { get; internal set; }     // server id of the pet's target, 0 = none
        public string Name { get; internal set; }
        public DateTime ReceivedAt { get; internal set; }

        public bool Exists { get { return Index > 0; } }
        public bool HasVitals { get { return Mpp >= 0; } }
    }

    public sealed class PlayerState
    {
        public uint Id { get; internal set; }
        public int Index { get; internal set; }
        public int Hp { get; internal set; }
        public int Hpp { get; internal set; }
        public int Mp { get; internal set; }
        public int Mpp { get; internal set; }
        public int MaxMp { get; internal set; }
        public int Tp { get; internal set; }
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Z { get; internal set; }
        public float Heading { get; internal set; }
        public int Status { get; internal set; }            // 0 idle, 1 engaged, 2/3 dead, 4 event, 33 resting, ...
        public int Zone { get; internal set; }
        public int MainJob { get; internal set; }
        public int MainJobLevel { get; internal set; }
        public int SubJob { get; internal set; }
        public int SubJobLevel { get; internal set; }
        public LoginState Login { get; internal set; }
        public float CastProgress { get; internal set; }    // 0 = not casting, 0.01..0.95 casting (estimated), 1.0 = just finished
        public int PetIndex { get; internal set; }          // 0 = no pet
        public int TargetIndex { get; internal set; }       // 0 = no target
        public int SubTargetIndex { get; internal set; }
        public bool TargetLocked { get; internal set; }
        public IReadOnlyList<int> Buffs { get; internal set; }  // active buff ids (no padding)
        public int EventMenuId { get; internal set; }       // NPC event menu while Status == 4, else 0
        public DateTime ReceivedAt { get; internal set; }

        public bool IsCasting { get { return CastProgress > 0f && CastProgress < 0.99f; } }
        public bool HasBuff(int buffId) { foreach (var b in Buffs) if (b == buffId) return true; return false; }
    }

    public sealed class EntityState
    {
        public int Index { get; internal set; }
        public uint Id { get; internal set; }
        public string Name { get; internal set; }
        public int Hpp { get; internal set; }
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Z { get; internal set; }
        public float Heading { get; internal set; }
        public float Distance { get; internal set; }        // yalms from the reporting character
        public int Status { get; internal set; }
        public uint ClaimId { get; internal set; }          // server id of the claiming player, 0 = unclaimed
        public int SpawnType { get; internal set; }         // Windower spawn_type bit flags
        public float ModelSize { get; internal set; }
        public int PetIndex { get; internal set; }
        public int TargetIndex { get; internal set; }       // what this entity is targeting
        public bool ValidTarget { get; internal set; }      // targetable / rendered
        public int Race { get; internal set; }
        public DateTime UpdatedAt { get; internal set; }

        public bool IsPlayerCharacter { get { return (SpawnType & 0x01) != 0; } }
        public bool IsMonster { get { return (SpawnType & 0x10) != 0; } }
    }

    public sealed class PartyMemberState
    {
        public int Slot { get; internal set; }              // 0-5 party, 6-11 alliance 1, 12-17 alliance 2
        public string Name { get; internal set; }
        public uint Id { get; internal set; }               // 0 when the member is out of range
        public int Index { get; internal set; }
        public int Hp { get; internal set; }
        public int Hpp { get; internal set; }
        public int Mp { get; internal set; }
        public int Mpp { get; internal set; }
        public int Tp { get; internal set; }
        public int Zone { get; internal set; }
        public int MainJob { get; internal set; }           // only known for the reporting character itself
        public int MainJobLevel { get; internal set; }
        public int SubJob { get; internal set; }
        public int SubJobLevel { get; internal set; }
    }

    public sealed class RecastState
    {
        // Job-ability recasts by Windower recast id (0 = ready), and spell recasts by spell id
        // (only spells still cooling down are listed). Whole seconds, rounded up.
        public IReadOnlyDictionary<int, float> AbilitySeconds { get; internal set; }
        public IReadOnlyDictionary<int, float> SpellSeconds { get; internal set; }
        public DateTime ReceivedAt { get; internal set; }

        public float AbilityRecast(int recastId) { float s; return AbilitySeconds.TryGetValue(recastId, out s) ? s : 0f; }
        public float SpellRecast(int spellId) { float s; return SpellSeconds.TryGetValue(spellId, out s) ? s : 0f; }
    }

    public sealed class InventoryItem
    {
        public int Slot { get; internal set; }
        public int ItemId { get; internal set; }
        public int Count { get; internal set; }
    }

    public sealed class InventoryState
    {
        public int Capacity { get; internal set; }
        public IReadOnlyDictionary<int, InventoryItem> Slots { get; internal set; }  // occupied slots only
        public DateTime ReceivedAt { get; internal set; }

        public int CountOf(int itemId)
        {
            int n = 0;
            foreach (var it in Slots.Values) if (it.ItemId == itemId) n += it.Count;
            return n;
        }
    }

    public sealed class ChatLine
    {
        public int Mode { get; internal set; }      // the chat log mode (same numbers Windower's 'incoming text' reports)
        public string Text { get; internal set; }   // color / auto-translate codes stripped
        public DateTime ReceivedAt { get; internal set; }
    }

    public sealed class Currency
    {
        public int Sparks { get; internal set; }    // -1 = not reported yet
        public int Accolades { get; internal set; }
        public DateTime ReceivedAt { get; internal set; }
    }

    // Slow-moving character data (SPX): job points, stats, skills and the temporary-items bag.
    public sealed class PlayerExtras
    {
        public IReadOnlyDictionary<int, int> JobPointsSpent { get; internal set; }   // job id -> spent JP (jobs with none are absent)
        public CharacterStats BaseStats { get; internal set; }                     // from the last 0x061 (zeros until one was seen)
        public CharacterStats AddedStats { get; internal set; }                    // gear / buff bonuses
        public IReadOnlyDictionary<string, int> Skills { get; internal set; }      // Windower skill keys: healing_magic, enhancing_magic, ...
        public IReadOnlyDictionary<int, int> TemporaryItems { get; internal set; } // item id -> count
        public DateTime ReceivedAt { get; internal set; }

        public int SpentJobPoints(int jobId) { int v; return JobPointsSpent.TryGetValue(jobId, out v) ? v : 0; }
        public int Skill(string key) { int v; return Skills.TryGetValue(key, out v) ? v : 0; }
        public int TemporaryItemCount(int itemId) { int v; return TemporaryItems.TryGetValue(itemId, out v) ? v : 0; }
        public CharacterStats TotalStats { get { return CharacterStats.Sum(BaseStats, AddedStats); } }
    }

    public sealed class CharacterStats
    {
        public int Str { get; internal set; }
        public int Dex { get; internal set; }
        public int Vit { get; internal set; }
        public int Agi { get; internal set; }
        public int Int { get; internal set; }
        public int Mnd { get; internal set; }
        public int Chr { get; internal set; }

        internal static CharacterStats Parse(string csv)
        {
            var f = (csv ?? "").Split(',');
            return new CharacterStats
            {
                Str = Protocol.Int(f, 0), Dex = Protocol.Int(f, 1), Vit = Protocol.Int(f, 2), Agi = Protocol.Int(f, 3),
                Int = Protocol.Int(f, 4), Mnd = Protocol.Int(f, 5), Chr = Protocol.Int(f, 6),
            };
        }

        internal static CharacterStats Sum(CharacterStats a, CharacterStats b)
        {
            return new CharacterStats
            {
                Str = a.Str + b.Str, Dex = a.Dex + b.Dex, Vit = a.Vit + b.Vit, Agi = a.Agi + b.Agi,
                Int = a.Int + b.Int, Mnd = a.Mnd + b.Mnd, Chr = a.Chr + b.Chr,
            };
        }
    }

    public enum CastPhase { Begin, Interrupt, Finish }

    // The character's own spell cast or item use (action categories 8/4 for spells, 9/5 for items).
    public sealed class CastReport
    {
        public CastPhase Phase { get; internal set; }
        public bool IsItem { get; internal set; }
        public int Id { get; internal set; }            // spell or item id (0 when the packet didn't carry one)
    }

    public sealed class BuffTimer
    {
        public int BuffId { get; internal set; }
        public DateTime ExpiresAt { get; internal set; }
        public double RemainingSeconds { get { return (ExpiresAt - DateTime.UtcNow).TotalSeconds; } }
    }

    // A dialog choice the client sent (outgoing 0x05B). Option values are menu-specific
    // encodings, not list positions — replay them exactly as recorded.
    public sealed class DialogChoice
    {
        public int MenuId { get; internal set; }
        public int Option { get; internal set; }
        public int Unknown1 { get; internal set; }
        public bool Automated { get; internal set; }
        public DateTime SentAt { get; internal set; }
        internal int Tick;

        public override string ToString()
        {
            return "menu " + MenuId + " option " + Option + " unknown1 " + Unknown1 + (Automated ? " (automated)" : "");
        }
    }

    // An NPC event that started (incoming 0x032/0x033/0x034).
    public sealed class NpcEvent
    {
        public int PacketId { get; internal set; }
        public int MenuId { get; internal set; }
        public uint NpcId { get; internal set; }
        public int NpcIndex { get; internal set; }
        public string ParamsHex { get; internal set; }   // the packet's "Menu Parameters" bytes, hex
        public DateTime At { get; internal set; }
    }

    // A complete hand-made NPC exchange (event + the choices through the final one), ready to
    // replay with CortanaCharacter.NpcReplay.
    public sealed class RecordedExchange
    {
        public int NpcIndex { get; internal set; }
        public int MenuId { get; internal set; }
        public IReadOnlyList<DialogChoice> Choices { get; internal set; }

        // "option:unknown1:auto,..." — the wire form NpcReplay takes.
        public string ReplayChoices
        {
            get
            {
                var parts = new List<string>();
                foreach (var c in Choices) parts.Add(c.Option + ":" + c.Unknown1 + ":" + (c.Automated ? 1 : 0));
                return string.Join(",", parts);
            }
        }
    }

    public sealed class SellResult
    {
        public int StacksSold { get; internal set; }
        public string Error { get; internal set; }      // "" = finished normally
        public bool Success { get { return string.IsNullOrEmpty(Error); } }
    }

    public sealed class ReplayResult
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
    }

    // `//cortana <feature> <action> [target]` typed in game, forwarded to the app.
    public sealed class AddonCommand
    {
        public string Feature { get; internal set; }
        public string Action { get; internal set; }     // on / off / toggle / free text (e.g. a profile name)
        public string Target { get; internal set; }     // optional character name, "" = the sender
    }

    // A monster started a TP move (category 7) or a spell (category 8).
    public sealed class MobAction
    {
        public int Category { get; internal set; }
        public int ActionId { get; internal set; }
        public uint ActorId { get; internal set; }
        public string Name { get; internal set; }
    }

    // A monster's TP move (category 11) or spell (category 4) finished.
    public sealed class MobActionResult
    {
        public int Category { get; internal set; }
        public int ActionId { get; internal set; }
        public uint ActorId { get; internal set; }
        public string Name { get; internal set; }
        public int Targets { get; internal set; }
        public long TotalDamage { get; internal set; }
    }

    // A player started casting (category 8) or a player's spell resolved (category 4) on a mob.
    public sealed class PlayerAction
    {
        public int Category { get; internal set; }
        public int ActionId { get; internal set; }
        public uint ActorId { get; internal set; }
        public uint TargetId { get; internal set; }
        public string Name { get; internal set; }
    }

    // A debuff wore off a mob (0x029 action message).
    public sealed class DebuffWear
    {
        public uint TargetId { get; internal set; }
        public int TargetIndex { get; internal set; }
        public int BuffId { get; internal set; }
        public int MessageId { get; internal set; }
        public string Name { get; internal set; }
    }

    // One of the character's own job abilities resolved (0x028 category 6). For Phantom Roll /
    // Double-Up, Value is the roll total; for other abilities it's the ability's result value.
    public sealed class JobAbilityResult
    {
        public int AbilityId { get; internal set; }     // as the packet carries it (Windower id + 0x200)
        public int Value { get; internal set; }
        public string Name { get; internal set; }
    }

    // Raw combat capture (weapon skill uses, 0x028 summaries): ';'-separated key=value fields.
    public sealed class CombatReport
    {
        public IReadOnlyDictionary<string, string> Fields { get; internal set; }
        public string Raw { get; internal set; }
        public DateTime At { get; internal set; }
        public string Get(string key) { string v; return Fields.TryGetValue(key, out v) ? v : null; }
    }

    public sealed class SkillchainReport
    {
        public string Name { get; internal set; }       // skillchain property, e.g. "Light"
        public int TargetId { get; internal set; }
        public string Source { get; internal set; }
    }

    // RNG Hover Shot confirmation: a ranged shot / ranged WS finished, and whether the hover
    // particle was seen.
    public sealed class HoverReport
    {
        public string Kind { get; internal set; }       // "shot" / "ws"
        public bool Particle { get; internal set; }
    }

    // Addon round trip, measured by the library's ECHO probe.
    public sealed class LinkStats
    {
        public double LastMs { get; internal set; }
        public double AverageMs { get; internal set; }  // smoothed
        public double PeakMs { get; internal set; }     // slowly decaying peak
        public int Samples { get; internal set; }
    }
}
