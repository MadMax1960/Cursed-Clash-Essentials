using System.Security;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;
using Color = System.Drawing.Color;

namespace jjkccpc.essentials;

internal sealed class Costumes(string root, Action<string> register, Action<string, Color> log)
{
    public const string FileName = "costumes.yaml";
    private const int PerGroup = 5;
    private const int MaxGroup = 35;

    private static readonly string[] Langs = { "De", "En", "Es", "Es_LA", "Fr", "It", "Ja", "Ko", "Pt", "Zh_Hans", "Zh_Hant" };
    private static readonly int[] ColourWords = { 2208, 2209, 2210, 2211 };

    private readonly string _out = Path.Combine(root, "generated", "costumes");
    private readonly JsonElement _data = Load(root, log);
    private readonly Dictionary<(string Character, int Group), string> _taken = new();
    private readonly Dictionary<string, List<string>> _more = new();
    private bool _finished;

    public readonly List<string> Owned = new();
    public readonly Dictionary<string, string> Cues = new();
    public readonly Dictionary<string, (int Group, int Colour)> Numbers = new();

    private static JsonElement Load(string modDir, Action<string, Color> log)
    {
        try
        {
            var stale = Path.Combine(modDir, "generated", "costumes");
            if (Directory.Exists(stale)) Directory.Delete(stale, true);
            return JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "Data", "costume-snapshot.json"))).RootElement.Clone();
        }
        catch (Exception)
        {
            log("Costume data broken", Color.Red);
            return default;
        }
    }

    private static FormatException Bad(string key) => new($"Bad '{key}'");

    private sealed class Spec
    {
        public string Character = "";
        public int Group = -1;
        public int Template;
        public string Name = "";
        public readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
        public string? Sound;
        public readonly List<string> Cues = new();
        public string? Weapon;
        public bool Owned = true;
        public bool ColourWords = true;
        public bool OwnImages;
        public string Blueprint = "{row}_BP";
    }

    public void Add(string modId, string modDir)
    {
        if (_data.ValueKind != JsonValueKind.Object) return;
        var file = Path.Combine(modDir, "jjkcc-essentials", FileName);
        if (!File.Exists(file)) return;

        try
        {
            var xml = new Xml();
            var count = 0;
            foreach (var spec in Parse(File.ReadAllText(file)))
            {
                try
                {
                    Build(spec, modId, xml);
                    count++;
                }
                catch (Exception ex)
                {
                    log($"Skipped the {spec.Character} costume in {modId}: \"{ex.Message}\"", Color.Red);
                }
            }
            if (count == 0) return;

            var dir = Path.Combine(_out, modId);
            xml.Write(dir);
            register(dir);
        }
        catch (Exception ex)
        {
            log($"Can't read {FileName} from {modId}: \"{ex.Message}\"", Color.Red);
        }
    }

    private static List<Spec> Parse(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count == 0) return new List<Spec>();
        YamlSequenceNode list = yaml.Documents[0].RootNode switch
        {
            YamlSequenceNode s => s,
            YamlMappingNode m when m.Children.TryGetValue(new YamlScalarNode("costumes"), out var c) && c is YamlSequenceNode s => s,
            _ => throw Bad("costumes"),
        };
        var specs = new List<Spec>();
        foreach (var node in list)
        {
            if (node is not YamlMappingNode m) throw Bad("costumes");
            var spec = new Spec();
            foreach (var (k, v) in m.Children)
            {
                var key = ((YamlScalarNode)k).Value ?? "";
                string Str() => (v as YamlScalarNode)?.Value?.Trim() ?? throw Bad(key);
                switch (key.ToLowerInvariant())
                {
                    case "character": spec.Character = Str(); break;
                    case "group": spec.Group = int.Parse(Str()); break;
                    case "template": spec.Template = int.Parse(Str()); break;
                    case "name": spec.Name = Str(); break;
                    case "names":
                        if (v is not YamlMappingNode names) throw Bad(key);
                        foreach (var (lk, lv) in names.Children)
                            spec.Names[((YamlScalarNode)lk).Value ?? ""] = ((YamlScalarNode)lv).Value ?? "";
                        break;
                    case "sound": spec.Sound = Str(); break;
                    case "sound_cues":
                        if (v is YamlSequenceNode cues)
                            foreach (var cue in cues) spec.Cues.Add(((YamlScalarNode)cue).Value?.Trim() ?? "");
                        else spec.Cues.Add(Str());
                        break;
                    case "weapon": spec.Weapon = Str(); break;
                    case "owned": spec.Owned = bool.Parse(Str()); break;
                    case "colours":
                    case "colors":
                        spec.ColourWords = Str().ToLowerInvariant() switch
                        {
                            "glossary" => true,
                            "none" => false,
                            _ => throw Bad(key),
                        };
                        break;
                    case "images":
                        spec.OwnImages = Str().ToLowerInvariant() switch
                        {
                            "template" => false,
                            "own" => true,
                            _ => throw Bad(key),
                        };
                        break;
                    case "blueprint": spec.Blueprint = Str(); break;
                    default: throw Bad(key);
                }
            }
            specs.Add(spec);
        }
        return specs;
    }

    private void Build(Spec s, string modId, Xml xml)
    {
        static void Need(bool ok, string key) { if (!ok) throw Bad(key); }
        Need(IsId(s.Character), "character");
        Need(s.Group >= 0 && s.Group <= MaxGroup, "group");
        Need(s.Name.Length > 0 || Langs.All(s.Names.ContainsKey), "name");
        Need(s.Sound == null ? s.Cues.Count == 0 : IsId(s.Sound), "sound");
        Need(s.Cues.All(IsId), "sound_cues");
        Need(s.Names.Keys.All(l => Langs.Contains(l, StringComparer.OrdinalIgnoreCase)), "names");
        Need(_data.GetProperty("characters").TryGetProperty(s.Character, out var character), "character");

        var slots = character.GetProperty("Variations").EnumerateArray().Select(e => e.GetString() ?? "None").ToArray();
        var claim = (s.Character, s.Group);
        var first = s.Group * PerGroup;
        Need(first < slots.Length || !_finished, "group");
        for (var c = 0; c < PerGroup; c++)
        {
            var i = first + c;
            Need((i >= slots.Length || slots[i] == "None") && !_taken.ContainsKey(claim), "group");
            var t = s.Template * PerGroup + c;
            Need(t < slots.Length && slots[t] != "None", "template");
        }
        _taken[claim] = modId;

        List<string>? more = null;
        if (first >= slots.Length)
        {
            if (!_more.TryGetValue(s.Character, out more)) _more[s.Character] = more = new();
            while (more.Count < first + PerGroup - slots.Length) more.Add("None");
        }

        var variations = _data.GetProperty("variations");
        var sortBase = _data.GetProperty("costumeItems").EnumerateObject()
            .Where(p => p.Name.StartsWith($"ITM_COSTUME_{s.Character}_", StringComparison.Ordinal))
            .Select(p => p.Value.GetProperty("SortingOrder").GetInt32()).DefaultIfEmpty(9000).Max();

        string? soundRow = null;
        for (var c = 0; c < PerGroup; c++)
        {
            var row = $"{s.Character}_{Digit(s.Group)}{c}";
            var blueprint = s.Blueprint.Replace("{row}", row);
            if (s.Group > 9) Numbers[blueprint] = (s.Group, c);
            var tmpl = variations.GetProperty(slots[s.Template * PerGroup + c]);
            string T(string field) => tmpl.GetProperty(field).GetString() ?? "None";
            var item = $"ITM_COSTUME_{row}";
            if (s.Sound != null && soundRow == null)
            {
                soundRow = $"{s.Character}_{Digit(s.Group)}0";
                SoundRow(xml, soundRow, T("Id_CharacterSound"), s.Sound);
            }

            xml.Row("CharacterVariationDataTable", row, new()
            {
                ["ID"] = row,
                ["FileName_BP"] = blueprint,
                ["Id_CharacterMaterial"] = T("Id_CharacterMaterial"),
                ["Id_CharacterWeapon"] = s.Weapon ?? T("Id_CharacterWeapon"),
                ["Id_CharacterSound"] = soundRow ?? T("Id_CharacterSound"),
                ["Id_CharacterImage"] = T("Id_CharacterImage"),
                ["Id_CharacterUniqueImage"] = T("Id_CharacterUniqueImage"),
                ["Id_Item"] = item,
                ["ImageFileName"] = s.OwnImages ? $"T_UI_Item_Costume_{row}" : T("ImageFileName"),
                ["ThumbnailImageFileName"] = s.OwnImages ? $"T_UI_ItemThumbnail_Costume_{row}" : T("ThumbnailImageFileName"),
                ["FreeBattleImageFileName"] = s.OwnImages ? $"T_UI_FreeBattleImage_{row}" : T("FreeBattleImageFileName"),
            });
            if (more == null) xml.ArrayItem("CharacterDataTable", s.Character, "Id_CharacterVariation", first + c + 1, row);
            else more[first + c - slots.Length] = row;
            if (s.Owned) Owned.Add(item);
            // the item row has to exist or the select screen crashes
            xml.Row("ItemDataTable", item, new()
            {
                ["ID"] = item,
                ["Id_ItemText"] = item,
                ["ItemType"] = "Costume",
                ["VariableId"] = "None",
                ["SortingOrder"] = (sortBase + 1 + c).ToString(),
                ["SellPrice"] = "0",
                ["PossessionMax"] = "1",
                ["InitialPossession"] = "1",
                ["UserProgressFlag"] = "-1",
            });
            foreach (var lang in Langs)
            {
                var name = s.Names.TryGetValue(lang, out var n) ? n : s.Name;
                xml.Row($"ItemTextDataTable_{lang}", item, new() { ["ID"] = item, ["Text"] = name + ColourSuffix(lang, c, s.ColourWords) });
            }
        }
        foreach (var cue in s.Cues) Cues[cue] = s.Sound!;
    }

    public void Finish()
    {
        _finished = true;
        if (_more.Count == 0) return;
        try
        {
            var xml = new Xml();
            foreach (var (character, rows) in _more)
                xml.Row("CharacterDataTable", character, new(), new() { ["Id_CharacterVariation"] = rows });
            var dir = Path.Combine(_out, "more-groups");
            xml.Write(dir);
            register(dir);
        }
        catch (Exception ex)
        {
            log($"Couldn't add costume groups over 6 \"{ex.Message}\"", Color.Red);
        }
    }

    private void SoundRow(Xml xml, string row, string templateSoundId, string extraSheet)
    {
        if (!_data.GetProperty("sounds").TryGetProperty(templateSoundId, out var t)) throw Bad("template");
        List<string> Names(string field) => t.GetProperty(field).EnumerateArray().Select(e => e.GetString() ?? "None").ToList();

        var sheets = Names("CueSheetName").Where(n => n != "None").ToList();
        sheets.Add(extraSheet);
        while (sheets.Count < 2) sheets.Add("None");
        xml.Row("CharacterSoundDataTable", row, new()
        {
            ["ID"] = row,
            ["FootstepType"] = t.GetProperty("FootstepType").GetString() ?? "None",
        }, new()
        {
            ["CueSheetName"] = sheets,
            ["Id_CharacterVoiceGroupSet"] = Names("Id_CharacterVoiceGroupSet"),
            ["Id_VoiceGroup"] = Names("Id_VoiceGroup"),
        });
    }

    private static string ColourSuffix(string lang, int colour, bool words)
    {
        if (colour == 0 || !words) return "";
        var tag = $"<GLO=GLS_GAME_WORDS_{ColourWords[colour - 1]}>";
        return lang switch
        {
            "Ja" or "Zh_Hans" or "Zh_Hant" => $"（{tag}）",
            "Ko" => $"({tag})",
            _ => $" ({tag})",
        };
    }

    // CP_010_60 is group 6, CP_010_A0 is group 10
    private static char Digit(int group) => (char)(group < 10 ? '0' + group : 'A' + group - 10);

    private static bool IsId(string s) => s.Length > 0 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private sealed class Xml
    {
        private readonly Dictionary<string, StringBuilder> _tables = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Table, string Row, string Array), SortedDictionary<int, string>> _arrays = new();

        private StringBuilder Table(string table)
        {
            if (!_tables.TryGetValue(table, out var sb)) _tables[table] = sb = new StringBuilder();
            return sb;
        }

        private static string Aaaaaaa(string v) => SecurityElement.Escape(v) ?? "";

        public void Row(string table, string row, Dictionary<string, string> fields, Dictionary<string, List<string>>? arrays = null)
        {
            var sb = Table(table);
            sb.Append($"  <Item id=\"{Aaaaaaa(row)}\">\n");
            foreach (var (k, v) in fields) sb.Append($"    <{k} value=\"{Aaaaaaa(v)}\" />\n");
            if (arrays != null)
                foreach (var (k, list) in arrays)
                {
                    sb.Append($"    <{k}>\n");
                    foreach (var v in list) sb.Append($"      <Item id=\"+\" value=\"{Aaaaaaa(v)}\" />\n");
                    sb.Append($"    </{k}>\n");
                }
            sb.Append("  </Item>\n");
        }

        public void ArrayItem(string table, string row, string array, int index1, string value)
        {
            var key = (table, row, array);
            if (!_arrays.TryGetValue(key, out var items)) _arrays[key] = items = new SortedDictionary<int, string>();
            items[index1] = value;
        }

        public void Write(string dir)
        {
            foreach (var ((table, row, array), items) in _arrays)
            {
                var sb = Table(table);
                sb.Append($"  <Item id=\"{Aaaaaaa(row)}\">\n    <{array}>\n");
                foreach (var (i, v) in items) sb.Append($"      <Item id=\"{i}\" value=\"{Aaaaaaa(v)}\" />\n");
                sb.Append($"    </{array}>\n  </Item>\n");
            }
            _arrays.Clear();
            Directory.CreateDirectory(dir);
            foreach (var (table, body) in _tables)
                File.WriteAllText(Path.Combine(dir, $"{table}.obj.xml"),
                    "<!-- generated from costumes.yaml -->\n<DataTable>\n" + body + "</DataTable>\n",
                    new UTF8Encoding(false));
        }
    }
}
