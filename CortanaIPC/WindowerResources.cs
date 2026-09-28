using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cortana
{
    public sealed class SpellInfo
    {
        public int Id { get; internal set; }
        public int RecastId { get; internal set; }
        public string Name { get; internal set; }           // English
        public string NameJa { get; internal set; }
        public int MpCost { get; internal set; }
        public float CastTime { get; internal set; }        // seconds (base)
        public float Recast { get; internal set; }          // seconds (base)
        public int Element { get; internal set; }
        public int Skill { get; internal set; }
        public int Targets { get; internal set; }           // Windower target flags
        public int Range { get; internal set; }
        public int Requirements { get; internal set; }
        public string Type { get; internal set; }           // "WhiteMagic", "BlackMagic", "SummonerPact", ...
        public IReadOnlyDictionary<int, int> Levels { get; internal set; }  // job id -> level learned
    }

    public enum AbilityKind { JobAbility, WeaponSkill }

    public sealed class AbilityInfo
    {
        public AbilityKind Kind { get; internal set; }
        public int Id { get; internal set; }                // Windower id within its kind
        public int RecastId { get; internal set; }
        public string Name { get; internal set; }
        public string NameJa { get; internal set; }
        public int MpCost { get; internal set; }
        public int TpCost { get; internal set; }
        public int Element { get; internal set; }
        public int Targets { get; internal set; }
        public int Range { get; internal set; }
        public string Type { get; internal set; }           // "JobAbility", "BloodPactRage", "Monster", "WeaponSkill", ...
        public int Status { get; internal set; }            // buff the ability puts on its user (res "status"), 0 = none listed
    }

    public sealed class ItemInfo
    {
        public int Id { get; internal set; }
        public string Name { get; internal set; }
        public string NameJa { get; internal set; }
        public string LogName { get; internal set; }        // singular, as the chat log prints it
        public string LogNameJa { get; internal set; }
        public int Stack { get; internal set; }
        public int Flags { get; internal set; }
        public int Type { get; internal set; }
        public int Targets { get; internal set; }
        public int Level { get; internal set; }
        public int ItemLevel { get; internal set; }
        public float CastTime { get; internal set; }
        public float Recast { get; internal set; }
        public int MaxCharges { get; internal set; }
    }

    // Spell / ability / item data from Windower's generated resource tables (<windower>/res/*.lua).
    // Each table loads lazily on first use (items.lua is large) and is then kept in memory.
    // CortanaIPC creates one from the path the addon reports; you can also construct your own.
    public sealed class WindowerResources
    {
        private readonly string _res;
        private readonly object _lock = new object();
        private Dictionary<int, SpellInfo> _spells;
        private Dictionary<string, SpellInfo> _spellsByName;
        private Dictionary<int, AbilityInfo> _jobAbilities, _weaponSkills;
        private Dictionary<string, AbilityInfo> _abilitiesByName;
        private Dictionary<int, ItemInfo> _items;
        private Dictionary<string, ItemInfo> _itemsByName;

        public WindowerResources(string windowerPath)
        {
            WindowerPath = windowerPath ?? "";
            _res = Path.Combine(WindowerPath.Trim(), "res");
        }

        public string WindowerPath { get; private set; }
        public bool Available { get { return Directory.Exists(_res); } }

        public SpellInfo GetSpell(int id) { EnsureSpells(); SpellInfo s; return _spells.TryGetValue(id, out s) ? s : null; }
        public SpellInfo FindSpell(string name) { EnsureSpells(); SpellInfo s; return name != null && _spellsByName.TryGetValue(name.Trim(), out s) ? s : null; }
        public AbilityInfo GetJobAbility(int id) { EnsureAbilities(); AbilityInfo a; return _jobAbilities.TryGetValue(id, out a) ? a : null; }
        public AbilityInfo GetWeaponSkill(int id) { EnsureAbilities(); AbilityInfo a; return _weaponSkills.TryGetValue(id, out a) ? a : null; }
        // Job abilities are preferred when a name exists in both tables.
        public AbilityInfo FindAbility(string name) { EnsureAbilities(); AbilityInfo a; return name != null && _abilitiesByName.TryGetValue(name.Trim(), out a) ? a : null; }
        public ItemInfo GetItem(int id) { EnsureItems(); ItemInfo i; return _items.TryGetValue(id, out i) ? i : null; }
        public ItemInfo FindItem(string name) { EnsureItems(); ItemInfo i; return name != null && _itemsByName.TryGetValue(name.Trim(), out i) ? i : null; }

        // ---- loaders ----

        private void EnsureSpells()
        {
            if (_spells != null) return;
            lock (_lock)
            {
                if (_spells != null) return;
                var byId = new Dictionary<int, SpellInfo>();
                var byName = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in Load("spells.lua"))
                {
                    var t = kv.Value;
                    var levels = new Dictionary<int, int>();
                    var lv = t.Table("levels");
                    if (lv != null)
                        foreach (var e in lv.Entries)
                            if (e.Key is double && e.Value is double) levels[(int)(double)e.Key] = (int)(double)e.Value;
                    var s = new SpellInfo
                    {
                        Id = kv.Key, RecastId = t.Int("recast_id"), Name = t.Str("en"), NameJa = t.Str("ja"),
                        MpCost = t.Int("mp_cost"), CastTime = (float)t.Num("cast_time"), Recast = (float)t.Num("recast"),
                        Element = t.Int("element"), Skill = t.Int("skill"), Targets = t.Int("targets"),
                        Range = t.Int("range"), Requirements = t.Int("requirements"), Type = t.Str("type"), Levels = levels,
                    };
                    byId[s.Id] = s;
                    if (s.Name.Length > 0 && !byName.ContainsKey(s.Name)) byName[s.Name] = s;
                }
                _spellsByName = byName;
                _spells = byId;
            }
        }

        private void EnsureAbilities()
        {
            if (_jobAbilities != null) return;
            lock (_lock)
            {
                if (_jobAbilities != null) return;
                var byName = new Dictionary<string, AbilityInfo>(StringComparer.OrdinalIgnoreCase);
                Func<string, AbilityKind, Dictionary<int, AbilityInfo>> load = (file, kind) =>
                {
                    var d = new Dictionary<int, AbilityInfo>();
                    foreach (var kv in Load(file))
                    {
                        var t = kv.Value;
                        var a = new AbilityInfo
                        {
                            Kind = kind, Id = kv.Key, RecastId = t.Int("recast_id"), Name = t.Str("en"), NameJa = t.Str("ja"),
                            MpCost = t.Int("mp_cost"), TpCost = t.Int("tp_cost"), Element = t.Int("element"),
                            Targets = t.Int("targets"), Range = t.Int("range"),
                            Type = kind == AbilityKind.WeaponSkill ? "WeaponSkill" : t.Str("type"),
                            Status = t.Int("status"),
                        };
                        d[a.Id] = a;
                        if (a.Name.Length > 0 && !byName.ContainsKey(a.Name)) byName[a.Name] = a;
                    }
                    return d;
                };
                var ja = load("job_abilities.lua", AbilityKind.JobAbility);
                _weaponSkills = load("weapon_skills.lua", AbilityKind.WeaponSkill);
                _abilitiesByName = byName;
                _jobAbilities = ja;
            }
        }

        private void EnsureItems()
        {
            if (_items != null) return;
            lock (_lock)
            {
                if (_items != null) return;
                var byId = new Dictionary<int, ItemInfo>();
                var byName = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in Load("items.lua"))
                {
                    var t = kv.Value;
                    var i = new ItemInfo
                    {
                        Id = kv.Key, Name = t.Str("en"), NameJa = t.Str("ja"), LogName = t.Str("enl"), LogNameJa = t.Str("jal"),
                        Stack = t.Int("stack"), Flags = t.Int("flags"), Type = t.Int("type"), Targets = t.Int("targets"),
                        Level = t.Int("level"), ItemLevel = t.Int("item_level"), CastTime = (float)t.Num("cast_time"),
                        Recast = (float)t.Num("recast"), MaxCharges = t.Int("max_charges"),
                    };
                    byId[i.Id] = i;
                    if (i.Name.Length > 0 && !byName.ContainsKey(i.Name)) byName[i.Name] = i;
                }
                _itemsByName = byName;
                _items = byId;
            }
        }

        // Top-level `return { [id] = {...}, ... }` -> id -> entry table. Missing / unreadable -> empty.
        private List<KeyValuePair<int, LuaTable>> Load(string file)
        {
            var result = new List<KeyValuePair<int, LuaTable>>();
            string path = Path.Combine(_res, file);
            try
            {
                if (!File.Exists(path)) return result;
                var root = new LuaTableParser(File.ReadAllText(path, Encoding.UTF8)).ParseReturn();
                if (root != null)
                    foreach (var e in root.Entries)
                    {
                        var t = e.Value as LuaTable;
                        if (t != null && e.Key is double) result.Add(new KeyValuePair<int, LuaTable>((int)(double)e.Key, t));
                    }
            }
            catch { }
            return result;
        }

        // ---- minimal Lua table-literal reader ----
        // Handles exactly what Windower's generated res files contain: nested tables, [n]= / key= /
        // positional entries, quoted strings with escapes, numbers, booleans, nil, `--` comments,
        // and constructor calls like S{...} / L{...} (read as plain tables).

        private sealed class LuaTable
        {
            public readonly List<KeyValuePair<object, object>> Entries = new List<KeyValuePair<object, object>>();
            private Dictionary<string, object> _named;

            public object Get(string key)
            {
                if (_named == null)
                {
                    _named = new Dictionary<string, object>();
                    foreach (var e in Entries) { var k = e.Key as string; if (k != null) _named[k] = e.Value; }
                }
                object v;
                return _named.TryGetValue(key, out v) ? v : null;
            }
            public string Str(string key) { return Get(key) as string ?? ""; }
            public double Num(string key) { var v = Get(key); return v is double ? (double)v : 0; }
            public int Int(string key) { return (int)Num(key); }
            public LuaTable Table(string key) { return Get(key) as LuaTable; }
        }

        private sealed class LuaTableParser
        {
            private readonly string s;
            private int i;
            public LuaTableParser(string text) { s = text; }

            public LuaTable ParseReturn()
            {
                Skip();
                if (Match("return")) Skip();
                return ParseValue() as LuaTable;
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length;
                return true;
            }

            private void Skip()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                    {
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    break;
                }
            }

            private object ParseValue()
            {
                Skip();
                if (i >= s.Length) throw new FormatException("unexpected end");
                char c = s[i];
                if (c == '{') return ParseTable();
                if (c == '"' || c == '\'') return ParseString();
                if (c == '-' || c == '.' || char.IsDigit(c)) return ParseNumber();
                string word = ParseIdent();
                if (word == "true") return true;
                if (word == "false") return false;
                if (word == "nil") return null;
                Skip();
                if (i < s.Length && s[i] == '{') return ParseTable();     // S{...}, L{...}, T{...}
                throw new FormatException("unexpected '" + word + "' at " + i);
            }

            private LuaTable ParseTable()
            {
                var t = new LuaTable();
                i++; // {
                double pos = 1;
                while (true)
                {
                    Skip();
                    if (i >= s.Length) throw new FormatException("unterminated table");
                    if (s[i] == '}') { i++; return t; }
                    object key;
                    if (s[i] == '[')
                    {
                        i++;
                        key = ParseValue();
                        Skip(); Expect(']'); Skip(); Expect('=');
                        t.Entries.Add(new KeyValuePair<object, object>(key, ParseValue()));
                    }
                    else if (IsIdentStart(s[i]) && IsAssignmentAhead())
                    {
                        key = ParseIdent();
                        Skip(); Expect('=');
                        t.Entries.Add(new KeyValuePair<object, object>(key, ParseValue()));
                    }
                    else
                    {
                        t.Entries.Add(new KeyValuePair<object, object>(pos++, ParseValue()));
                    }
                    Skip();
                    if (i < s.Length && (s[i] == ',' || s[i] == ';')) i++;
                }
            }

            private bool IsAssignmentAhead()
            {
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                return j < s.Length && s[j] == '=' && (j + 1 >= s.Length || s[j + 1] != '=');
            }

            private static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_'; }

            private string ParseIdent()
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                if (i == start) throw new FormatException("identifier expected at " + i);
                return s.Substring(start, i - start);
            }

            private void Expect(char c)
            {
                if (i >= s.Length || s[i] != c) throw new FormatException("'" + c + "' expected at " + i);
                i++;
            }

            private double ParseNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                {
                    i += 2;
                    int hs = i;
                    while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
                    double hv = (double)long.Parse(s.Substring(hs, i - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return s[start] == '-' ? -hv : hv;
                }
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                       || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')))) i++;
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private string ParseString()
            {
                char q = s[i++];
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != q)
                {
                    char c = s[i++];
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        default:
                            if (char.IsDigit(e))
                            {
                                int n = e - '0', k = 1;
                                while (k < 3 && i < s.Length && char.IsDigit(s[i])) { n = n * 10 + (s[i++] - '0'); k++; }
                                sb.Append((char)n);
                            }
                            else sb.Append(e);
                            break;
                    }
                }
                i++; // closing quote
                return sb.ToString();
            }
        }
    }
}
