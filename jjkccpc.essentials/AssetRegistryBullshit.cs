using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Color = System.Drawing.Color;

namespace jjkccpc.essentials;

internal sealed class AssetLists(string dir, Action<string, Color> log)
{
    public const string FileName = "asset-registry.txt";
    private const string Project = "Jujutsu Kaisen CC";

    private readonly List<(string Mod, string[] Lines)> _lists = new();

    public void Add(string modId, string modDir)
    {
        var file = Path.Combine(modDir, "jjkcc-essentials", FileName);
        if (File.Exists(file)) _lists.Add((modId, File.ReadAllLines(file)));
    }

    public string? Build()
    {
        if (_lists.Count == 0) return null;
        try
        {
            var merger = new AssetRegistry(File.ReadAllBytes(Path.Combine(dir, "Data", "AssetRegistry.bin")));
            var total = 0;
            foreach (var (mod, lines) in _lists)
            {
                var bad = 0;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var outcome = merger.Add(line);
                    if (outcome == AssetRegistry.Outcome.Added) total++;
                    else if (outcome == AssetRegistry.Outcome.Failed) bad++;
                }
                if (bad > 0) log($"{mod} has {bad} bad lines in {FileName}", Color.Red);
            }

            var folder = Path.Combine(dir, "generated", "assetregistry");
            Directory.CreateDirectory(Path.Combine(folder, Project));
            File.WriteAllBytes(Path.Combine(folder, Project, "AssetRegistry.bin"), merger.Build());
            log($"Added {total} assets to the asset registry", Color.Green);
            return folder;
        }
        catch (Exception ex)
        {
            log($"Shit died building asset registry: \"{ex.Message}\"", Color.Red);
            return null;
        }
    }
}

internal sealed class AssetRegistry
{
    public enum Outcome { Added, Exists, Failed }

    private static readonly byte[] VersionGuid = { 0xE7, 0x9E, 0x7F, 0x71, 0x3A, 0x49, 0xB0, 0xE9, 0x32, 0x91, 0xB3, 0x88, 0x07, 0x81, 0x38, 0x1B };
    private const int Version = 16;              // FAssetRegistryVersion::AddedHeader
    private const int HeaderSize = 16 + 4 + 4;
    private const ulong HashAlgo = 0xC1640000;
    private const uint Numbered = 0x80000000;
    private const uint StoreBegin = 0x12345679, StoreEnd = 0x87654321;
    private const int MaxName = 1023;
    private const int AnsiString = 0, WideString = 1, NumberlessName = 2, Name = 3, NumberlessExportPath = 4, ExportPath = 5, LocalizedText = 6;

    private readonly record struct NameRef(int Index, uint Number);
    private readonly record struct AssetKey(NameRef Package, NameRef Asset);

    private sealed class Entry
    {
        public byte[] ClassPath = [];
        public ulong TagHandle;
        public List<(byte[] Key, uint Value)> Tags = new();
        public byte[] ChunksAndFlags = [];
        public byte[]? PackageData;
    }

    private sealed class Buffer
    {
        public readonly MemoryStream Stream = new();
        public int Count;
        public int Length => (int)Stream.Length;
        public void U32(uint v) => Stream.Write(BitConverter.GetBytes(v));
        public void U64(ulong v) => Stream.Write(BitConverter.GetBytes(v));
        public void Bytes(ReadOnlySpan<byte> b) => Stream.Write(b);
        public void CopyTo(Buffer o) => Stream.WriteTo(o.Stream);
    }

    private readonly byte[] _d;

    private readonly int _nameCount, _nameBytes, _hashPos, _headerPos, _stringPos, _namesEnd;
    private readonly string[] _names;
    private readonly int[] _canon;               // same name in another case -> first index
    private readonly Dictionary<string, int> _nameIndex;
    private readonly List<string> _newNames = new();

    private readonly int _cNlNames, _cNames, _cNlExports, _cExports, _cTexts, _cAnsiOffsets, _cWideOffsets, _cAnsi, _cWide, _cNlPairs, _cPairs;
    private readonly int _textBlockPos, _nlNamesPos, _namesArrayPos, _nlExportsPos, _exportsPos, _ansiOffsetsPos, _wideOffsetsPos, _ansiPos, _widePos, _nlPairsPos, _pairsPos, _storeEndPos;
    private readonly int[] _nameOff, _exportOff, _pairOff;
    private string[]? _texts;
    private readonly Buffer _newNlNames = new(), _newNamesArray = new(), _newNlExports = new(), _newExports = new(),
                         _newAnsiOffsets = new(), _newWideOffsets = new(), _newAnsi = new(), _newWide = new(),
                         _newNlPairs = new(), _newPairs = new();
    private readonly Dictionary<uint, string> _newValueText = new();
    private readonly Dictionary<(int Kind, string Text), uint> _newValueIds = new();

    private readonly int _assetsPos, _assetsEnd, _numAssets;
    private readonly Dictionary<AssetKey, int> _baseAssets;
    private readonly Dictionary<AssetKey, Entry> _added = new();
    private readonly Buffer _newAssets = new();

    private readonly int _packagesPos, _numPackages;
    private readonly Dictionary<NameRef, (int Body, int End)> _basePackages;
    private readonly Dictionary<NameRef, byte[]> _addedPackages = new();
    private readonly Buffer _newPackages = new();

    public int AssetCount => _numAssets;

    private uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(p, 4));
    private int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(p, 4));
    private ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(p, 8));
    private int NameSize(int p) => (U32(p) & Numbered) != 0 ? 8 : 4;
    private NameRef ReadName(int p)
    {
        var index = U32(p);
        return (index & Numbered) != 0 ? new NameRef((int)(index & ~Numbered), U32(p + 4)) : new NameRef((int)index, 0);
    }
    private NameRef Canon(NameRef n) => new(_canon[n.Index], n.Number);
    private static FormatException Bad() => new("AssetRegistry.bin isn't correct so shit won't work :(");

    public AssetRegistry(byte[] file)
    {
        _d = file;
        if (file.Length < HeaderSize + 16 || !file.AsSpan(0, 16).SequenceEqual(VersionGuid)) throw Bad();
        if (I32(16) != Version) throw Bad();

        // names
        var p = HeaderSize;
        _nameCount = I32(p);
        _nameBytes = I32(p + 4);
        if (_nameCount <= 0 || U64(p + 8) != HashAlgo) throw Bad();
        _hashPos = p + 16;
        _headerPos = _hashPos + 8 * _nameCount;
        _stringPos = _headerPos + 2 * _nameCount;
        _namesEnd = _stringPos + _nameBytes;
        _names = new string[_nameCount];
        _canon = new int[_nameCount];
        _nameIndex = new Dictionary<string, int>(_nameCount + 1024, StringComparer.OrdinalIgnoreCase);
        var s = _stringPos;
        for (var i = 0; i < _nameCount; i++)
        {
            int b0 = file[_headerPos + 2 * i], length = ((b0 & 0x7F) << 8) | file[_headerPos + 2 * i + 1];
            if ((b0 & 0x80) != 0) { _names[i] = Encoding.Unicode.GetString(file, s, length * 2); s += length * 2; }
            else { _names[i] = Encoding.Latin1.GetString(file, s, length); s += length; }
            _canon[i] = _nameIndex.TryAdd(_names[i], i) ? i : _nameIndex[_names[i]];
        }
        if (s != _namesEnd) throw Bad();
        CheckHashes();

        // tag store
        p = _namesEnd;
        if (U32(p) != StoreBegin) throw Bad();
        _cNlNames = I32(p + 4); _cNames = I32(p + 8); _cNlExports = I32(p + 12); _cExports = I32(p + 16); _cTexts = I32(p + 20);
        _cAnsiOffsets = I32(p + 24); _cWideOffsets = I32(p + 28); _cAnsi = I32(p + 32); _cWide = I32(p + 36);
        _cNlPairs = I32(p + 40); _cPairs = I32(p + 44);
        _textBlockPos = p + 48;
        p = _textBlockPos + 4 + I32(_textBlockPos);
        _nlNamesPos = p;
        for (var i = 0; i < _cNlNames; i++, p += 4) if ((U32(p) & Numbered) != 0) throw Bad();
        _namesArrayPos = p;
        _nameOff = new int[_cNames + 1];
        for (var i = 0; i < _cNames; i++) { _nameOff[i] = p; p += NameSize(p); }
        _nameOff[_cNames] = p;
        _nlExportsPos = p;
        for (var i = 0; i < _cNlExports * 4; i++, p += 4) if ((U32(p) & Numbered) != 0) throw Bad();
        _exportsPos = p;
        _exportOff = new int[_cExports + 1];
        for (var i = 0; i < _cExports; i++) { _exportOff[i] = p; for (var k = 0; k < 4; k++) p += NameSize(p); }
        _exportOff[_cExports] = p;
        _ansiOffsetsPos = p; p += 4 * _cAnsiOffsets;
        _wideOffsetsPos = p; p += 4 * _cWideOffsets;
        _ansiPos = p; p += _cAnsi;
        _widePos = p; p += 2 * _cWide;
        _nlPairsPos = p;
        for (var i = 0; i < _cNlPairs; i++, p += 8) if ((U32(p) & Numbered) != 0) throw Bad();
        _pairsPos = p;
        _pairOff = new int[_cPairs + 1];
        for (var i = 0; i < _cPairs; i++) { _pairOff[i] = p; p += NameSize(p) + 4; }
        _pairOff[_cPairs] = p;
        _storeEndPos = p;
        if (U32(p) != StoreEnd) throw Bad();
        p += 4;

        // assets
        _numAssets = I32(p);
        _assetsPos = p + 4;
        p = _assetsPos;
        _baseAssets = new Dictionary<AssetKey, int>(_numAssets);
        for (var i = 0; i < _numAssets; i++)
        {
            var start = p;
            p = SkipAsset(p, out var package, out var asset, out _, out _);
            _baseAssets.TryAdd(new AssetKey(Canon(package), Canon(asset)), start);
        }
        _assetsEnd = p;

        var dependencyBytes = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(p, 8));
        if (dependencyBytes < 0 || p + 8 + dependencyBytes > file.Length - 4) throw Bad();
        p += 8 + (int)dependencyBytes;

        _numPackages = I32(p);
        _packagesPos = p + 4;
        p = _packagesPos;
        _basePackages = new Dictionary<NameRef, (int, int)>(_numPackages);
        for (var i = 0; i < _numPackages; i++)
        {
            var name = Canon(ReadName(p));
            var body = p + NameSize(p);
            p = body + 8 + 16;                                   // disk size/gguid package
            p += I32(p) != 0 ? 4 + 16 : 4;                       // cooked hash
            p += 4 + 32 * I32(p);                                // chunk hashes
            p += 8 + 4 + 4;                                      // file version
            p += 4 + 20 * I32(p);                                // custom versions
            var imports = I32(p); p += 4;                        // imported classes
            for (var k = 0; k < imports; k++) p += NameSize(p);
            _basePackages.TryAdd(name, (body, p));
        }
        if (p != file.Length) throw Bad();
    }

    private void CheckHashes()
    {
        var p = _stringPos;
        for (int i = 0, tested = 0; i < _nameCount && tested < 64; i++)
        {
            int b0 = _d[_headerPos + 2 * i], length = ((b0 & 0x7F) << 8) | _d[_headerPos + 2 * i + 1];
            if ((b0 & 0x80) != 0) { p += length * 2; continue; }
            var text = _d.AsSpan(p, length);
            p += length;
            if (text.ContainsAnyExceptInRange((byte)0, (byte)127)) continue;
            if (NameHash(_names[i]) != U64(_hashPos + 8 * i)) throw Bad();
            tested++;
        }
    }

    // package path, class path, package name, asset name, tag handle, bundles, chunk ids, flags
    private int SkipAsset(int p, out NameRef package, out NameRef asset, out int classPos, out int chunksPos)
    {
        p += NameSize(p);
        classPos = p;
        p += NameSize(p); p += NameSize(p);
        package = ReadName(p); p += NameSize(p);
        asset = ReadName(p); p += NameSize(p);
        p += 8;
        var bundles = I32(p); p += 4;
        for (var b = 0; b < bundles; b++)
        {
            p += NameSize(p);
            var paths = I32(p); p += 4;
            for (var k = 0; k < paths; k++)
            {
                p += NameSize(p); p += NameSize(p);
                var length = I32(p); p += 4;
                p += length >= 0 ? length : -2 * length;
            }
        }
        chunksPos = p;
        p += 4 + 4 * I32(p);
        return p + 4;
    }

    private string NameText(NameRef n) => n.Number == 0 ? NameBase(n.Index) : $"{NameBase(n.Index)}_{n.Number - 1}";
    private string NameBase(int index) => index < _nameCount ? _names[index] : _newNames[index - _nameCount];
    private bool IsNone(NameRef n) => n.Number == 0 && NameBase(n.Index).Equals("None", StringComparison.OrdinalIgnoreCase);

    // Base 12
    private static (string Base, uint Number) SplitNumber(string text)
    {
        var digits = 0;
        for (var i = text.Length - 1; i >= 0 && text[i] is >= '0' and <= '9'; i--) digits++;
        var first = text.Length - digits;
        if (digits > 0 && digits < text.Length && text[first - 1] == '_' && digits <= 10 && (digits == 1 || text[first] != '0'))
        {
            var number = long.Parse(text.AsSpan(first), NumberStyles.None, CultureInfo.InvariantCulture);
            if (number < int.MaxValue) return (text[..(first - 1)], (uint)(number + 1));
        }
        return (text, 0);
    }

    private NameRef AddName(string text)
    {
        var (baseText, number) = SplitNumber(text);
        if (!_nameIndex.TryGetValue(baseText, out var index))
        {
            if (baseText.Length == 0 || baseText.Length > MaxName || baseText.Any(c => c > 127)) throw new FormatException();
            index = _nameCount + _newNames.Count;
            _newNames.Add(baseText);
            _nameIndex[baseText] = index;
        }
        return new NameRef(index, number);
    }

    private NameRef? FindName(string text)
    {
        var (baseText, number) = SplitNumber(text);
        return _nameIndex.TryGetValue(baseText, out var index) ? new NameRef(index, number) : null;
    }

    private static void WriteName(Buffer b, NameRef n)
    {
        if (n.Number == 0) b.U32((uint)n.Index);
        else { b.U32((uint)n.Index | Numbered); b.U32(n.Number); }
    }

    private string ValueText(uint value)
    {
        if (_newValueText.TryGetValue(value, out var added)) return added;
        int type = (int)(value & 7), index = (int)(value >> 3);
        switch (type)
        {
            case AnsiString:
                {
                    var start = _ansiPos + (int)U32(_ansiOffsetsPos + 4 * index);
                    var end = Array.IndexOf(_d, (byte)0, start);
                    return Encoding.Latin1.GetString(_d, start, end - start);
                }
            case WideString:
                {
                    var start = _widePos + 2 * (int)U32(_wideOffsetsPos + 4 * index);
                    var end = start;
                    while (_d[end] != 0 || _d[end + 1] != 0) end += 2;
                    return Encoding.Unicode.GetString(_d, start, end - start);
                }
            case NumberlessName: return NameBase((int)U32(_nlNamesPos + 4 * index));
            case Name: return NameText(ReadName(_nameOff[index]));
            case NumberlessExportPath:
                {
                    var p = _nlExportsPos + 16 * index;
                    return PathText(ReadName(p), ReadName(p + 4), ReadName(p + 8), ReadName(p + 12));
                }
            case ExportPath:
                {
                    var p = _exportOff[index];
                    var classPackage = ReadName(p); p += NameSize(p);
                    var classAsset = ReadName(p); p += NameSize(p);
                    var obj = ReadName(p); p += NameSize(p);
                    return PathText(classPackage, classAsset, obj, ReadName(p));
                }
            case LocalizedText:
                {
                    if (_texts == null)
                    {
                        _texts = new string[_cTexts];
                        var p = _textBlockPos + 4;
                        for (var i = 0; i < _cTexts; i++)
                        {
                            var length = I32(p); p += 4;
                            if (length >= 0) { _texts[i] = Encoding.Latin1.GetString(_d, p, Math.Max(0, length - 1)); p += length; }
                            else { _texts[i] = Encoding.Unicode.GetString(_d, p, -2 * length - 2); p += -2 * length; }
                        }
                    }
                    return _texts[index];
                }
            default: throw Bad();
        }
    }

    private string PathText(NameRef classPackage, NameRef classAsset, NameRef obj, NameRef package)
    {
        var path = IsNone(obj) ? NameText(package) : $"{NameText(package)}.{NameText(obj)}";
        if (IsNone(classPackage) && IsNone(classAsset)) return path;
        var cls = IsNone(classAsset) ? NameText(classPackage) : $"{NameText(classPackage)}.{NameText(classAsset)}";
        return $"{cls}'{path}'";
    }

    private uint AddValue(int sourceType, string text)
    {
        var kind = sourceType switch
        {
            AnsiString or WideString => 0,
            NumberlessName or Name => 1,
            NumberlessExportPath or ExportPath => 2,
            _ => throw new FormatException(),
        };
        if (text.Length == 0) throw new FormatException();
        if (_newValueIds.TryGetValue((kind, text), out var known)) return known;

        uint value;
        if (kind == 0)
        {
            if (text.All(c => c <= 127))
            {
                value = AnsiString | (uint)(_cAnsiOffsets + _newAnsiOffsets.Length / 4) << 3;
                _newAnsiOffsets.U32((uint)(_cAnsi + _newAnsi.Length));
                _newAnsi.Bytes(Encoding.ASCII.GetBytes(text + '\0'));
            }
            else
            {
                value = WideString | (uint)(_cWideOffsets + _newWideOffsets.Length / 4) << 3;
                _newWideOffsets.U32((uint)(_cWide + _newWide.Length / 2));
                _newWide.Bytes(Encoding.Unicode.GetBytes(text + '\0'));
            }
        }
        else if (kind == 1)
        {
            var name = AddName(text);
            if (name.Number == 0)
            {
                value = NumberlessName | (uint)(_cNlNames + _newNlNames.Length / 4) << 3;
                _newNlNames.U32((uint)name.Index);
            }
            else
            {
                value = Name | (uint)(_cNames + _newNamesArray.Count++) << 3;
                WriteName(_newNamesArray, name);
            }
        }
        else
        {
            string? cls = null;
            var path = text;
            var quote = text.IndexOf('\'');
            if (quote >= 0)
            {
                cls = text[..quote];
                path = text[(quote + 1)..];
                if (path.EndsWith('\'')) path = path[..^1];
            }
            var dot = path.IndexOf('.');
            var package = AddName(dot >= 0 ? path[..dot] : path);
            var obj = AddName(dot >= 0 ? path[(dot + 1)..] : "None");
            NameRef classPackage = AddName("None"), classAsset = classPackage;
            if (cls != null)
            {
                var classDot = cls.IndexOf('.');
                if (classDot <= 0) throw new FormatException();
                classPackage = AddName(cls[..classDot]);
                classAsset = AddName(cls[(classDot + 1)..]);
            }
            if (classPackage.Number == 0 && classAsset.Number == 0 && obj.Number == 0 && package.Number == 0)
            {
                value = NumberlessExportPath | (uint)(_cNlExports + _newNlExports.Length / 16) << 3;
                _newNlExports.U32((uint)classPackage.Index); _newNlExports.U32((uint)classAsset.Index);
                _newNlExports.U32((uint)obj.Index); _newNlExports.U32((uint)package.Index);
            }
            else
            {
                value = ExportPath | (uint)(_cExports + _newExports.Count++) << 3;
                WriteName(_newExports, classPackage); WriteName(_newExports, classAsset);
                WriteName(_newExports, obj); WriteName(_newExports, package);
            }
        }
        _newValueIds[(kind, text)] = value;
        _newValueText[value] = text;
        return value;
    }

    private static bool SplitPath(string objectPath, out string package, out string asset)
    {
        var dot = objectPath.IndexOf('.');
        package = dot > 0 ? objectPath[..dot] : "";
        asset = dot > 0 ? objectPath[(dot + 1)..] : "";
        return package.Length > 0 && asset.Length > 0;
    }

    private Entry? Find(string objectPath)
    {
        if (!SplitPath(objectPath, out var package, out var asset)) return null;
        if (FindName(package) is not { } packageName || FindName(asset) is not { } assetName) return null;
        var key = new AssetKey(packageName, assetName);
        if (_added.TryGetValue(key, out var added)) return added;
        if (!_baseAssets.TryGetValue(key, out var record)) return null;

        var end = SkipAsset(record, out var sourcePackage, out _, out var classPos, out var chunksPos);
        var classSize = NameSize(classPos) + NameSize(classPos + NameSize(classPos));
        var packageNamePos = classPos + classSize;
        var assetNamePos = packageNamePos + NameSize(packageNamePos);
        var handle = U64(assetNamePos + NameSize(assetNamePos));
        var entry = new Entry
        {
            ClassPath = _d.AsSpan(classPos, classSize).ToArray(),
            TagHandle = handle,
            ChunksAndFlags = _d.AsSpan(chunksPos, end - chunksPos).ToArray(),
        };
        // bit 63
        int count = (ushort)(handle >> 32), begin = (int)(uint)handle;
        for (var i = 0; i < count; i++)
        {
            if (handle >> 63 != 0)
            {
                var p = _nlPairsPos + 8 * (begin + i);
                entry.Tags.Add((_d.AsSpan(p, 4).ToArray(), U32(p + 4)));
            }
            else
            {
                var p = _pairOff[begin + i];
                var keySize = NameSize(p);
                entry.Tags.Add((_d.AsSpan(p, keySize).ToArray(), U32(p + keySize)));
            }
        }
        if (_basePackages.TryGetValue(Canon(sourcePackage), out var data))
            entry.PackageData = _d.AsSpan(data.Body, data.End - data.Body).ToArray();
        return entry;
    }

    // SourceObjectPath|NewObjectPath[|From>To,From>To]
    public Outcome Add(string line)
    {
        try
        {
            var fields = line.Split('|');
            if (fields.Length < 2) return Outcome.Failed;
            string sourcePath = fields[0].Trim(), newPath = fields[1].Trim();
            var source = Find(sourcePath);
            if (source == null) return Outcome.Failed;
            if (Find(newPath) != null) return Outcome.Exists;
            if (!SplitPath(newPath, out var newPackage, out var newAsset) || newPackage.LastIndexOf('/') <= 0) return Outcome.Failed;

            var replacements = new List<(string From, string To)>();
            if (fields.Length > 2)
                foreach (var replacement in fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var arrow = replacement.IndexOf('>');
                    if (arrow > 0) replacements.Add((replacement[..arrow], replacement[(arrow + 1)..]));
                }

            var entry = new Entry { ClassPath = source.ClassPath, ChunksAndFlags = source.ChunksAndFlags, TagHandle = source.TagHandle, PackageData = source.PackageData };
            var changed = false;
            foreach (var (key, value) in source.Tags)
            {
                var newValue = value;
                if (replacements.Count > 0)
                {
                    var text = ValueText(value);
                    var replaced = text;
                    foreach (var (from, to) in replacements) replaced = replaced.Replace(from, to, StringComparison.Ordinal);
                    if (replaced != text) { newValue = AddValue((int)(value & 7), replaced); changed = true; }
                }
                entry.Tags.Add((key, newValue));
            }

            var packageName = AddName(newPackage);
            var packagePath = AddName(newPackage[..newPackage.LastIndexOf('/')]);
            var assetName = AddName(newAsset);
            if (changed)
            {
                var numberless = source.TagHandle >> 63 != 0;
                var pairs = numberless ? _newNlPairs : _newPairs;
                var begin = numberless ? _cNlPairs + _newNlPairs.Length / 8 : _cPairs + _newPairs.Count;
                foreach (var (key, value) in entry.Tags) { pairs.Bytes(key); pairs.U32(value); }
                if (!numberless) _newPairs.Count += entry.Tags.Count;
                entry.TagHandle = (numberless ? 1UL << 63 : 0) | (ulong)entry.Tags.Count << 32 | (uint)begin;
            }

            WriteName(_newAssets, packagePath);
            _newAssets.Bytes(entry.ClassPath);
            WriteName(_newAssets, packageName);
            WriteName(_newAssets, assetName);
            _newAssets.U64(entry.TagHandle);
            _newAssets.U32(0);                                   // no bundles
            _newAssets.Bytes(entry.ChunksAndFlags);
            _newAssets.Count++;
            _added[new AssetKey(packageName, assetName)] = entry;

            if (entry.PackageData != null && !_basePackages.ContainsKey(packageName) && _addedPackages.TryAdd(packageName, entry.PackageData))
            {
                WriteName(_newPackages, packageName);
                _newPackages.Bytes(entry.PackageData);
                _newPackages.Count++;
            }
            return Outcome.Added;
        }
        catch (Exception)
        {
            return Outcome.Failed;
        }
    }

    public byte[] Build()
    {
        var o = new Buffer();
        void Copy(int from, int to) => o.Bytes(_d.AsSpan(from, to - from));
        void Count(int n) => o.U32((uint)n);

        Copy(0, HeaderSize);

        Count(_nameCount + _newNames.Count);
        Count(_nameBytes + _newNames.Sum(n => n.Length));
        Copy(HeaderSize + 8, _headerPos);
        foreach (var name in _newNames) o.U64(NameHash(name));
        Copy(_headerPos, _stringPos);
        foreach (var name in _newNames) o.Bytes([(byte)(name.Length >> 8), (byte)name.Length]);
        Copy(_stringPos, _namesEnd);
        foreach (var name in _newNames) o.Bytes(Encoding.ASCII.GetBytes(name));

        Copy(_namesEnd, _namesEnd + 4);
        Count(_cNlNames + _newNlNames.Length / 4);
        Count(_cNames + _newNamesArray.Count);
        Count(_cNlExports + _newNlExports.Length / 16);
        Count(_cExports + _newExports.Count);
        Count(_cTexts);
        Count(_cAnsiOffsets + _newAnsiOffsets.Length / 4);
        Count(_cWideOffsets + _newWideOffsets.Length / 4);
        Count(_cAnsi + _newAnsi.Length);
        Count(_cWide + _newWide.Length / 2);
        Count(_cNlPairs + _newNlPairs.Length / 8);
        Count(_cPairs + _newPairs.Count);
        Copy(_textBlockPos, _namesArrayPos); _newNlNames.CopyTo(o);
        Copy(_namesArrayPos, _nlExportsPos); _newNamesArray.CopyTo(o);
        Copy(_nlExportsPos, _exportsPos); _newNlExports.CopyTo(o);
        Copy(_exportsPos, _ansiOffsetsPos); _newExports.CopyTo(o);
        Copy(_ansiOffsetsPos, _wideOffsetsPos); _newAnsiOffsets.CopyTo(o);
        Copy(_wideOffsetsPos, _ansiPos); _newWideOffsets.CopyTo(o);
        Copy(_ansiPos, _widePos); _newAnsi.CopyTo(o);
        Copy(_widePos, _nlPairsPos); _newWide.CopyTo(o);
        Copy(_nlPairsPos, _pairsPos); _newNlPairs.CopyTo(o);
        Copy(_pairsPos, _storeEndPos); _newPairs.CopyTo(o);
        Copy(_storeEndPos, _storeEndPos + 4);

        Count(_numAssets + _newAssets.Count);
        Copy(_assetsPos, _assetsEnd); _newAssets.CopyTo(o);
        Copy(_assetsEnd, _packagesPos - 4);
        Count(_numPackages + _newPackages.Count);
        Copy(_packagesPos, _d.Length); _newPackages.CopyTo(o);
        return o.Stream.ToArray();
    }

    internal static ulong NameHash(string name)
    {
        Span<byte> lower = name.Length <= 256 ? stackalloc byte[name.Length] : new byte[name.Length];
        for (var i = 0; i < name.Length; i++) lower[i] = (byte)char.ToLowerInvariant(name[i]);
        return CityHash64(lower);
    }

    private const ulong K0 = 0xc3a5c85c97cb3127, K1 = 0xb492b66fbe98f273, K2 = 0x9ae16a3b2f90404f;
    private static ulong Fetch64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(i, 8));
    private static ulong Fetch32(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i, 4));
    private static ulong Rotate(ulong v, int shift) => BitOperations.RotateRight(v, shift);
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);
    private static ulong Swap(ulong v) => BinaryPrimitives.ReverseEndianness(v);

    private static ulong HashLen16(ulong u, ulong v, ulong mul = 0x9ddfea08eb382d69)
    {
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        return b * mul;
    }

    private static (ulong Lo, ulong Hi) WeakHash32(ReadOnlySpan<byte> s, int i, ulong a, ulong b)
    {
        ulong w = Fetch64(s, i), x = Fetch64(s, i + 8), y = Fetch64(s, i + 16), z = Fetch64(s, i + 24);
        a += w;
        b = Rotate(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }

    // CityHash64
    private static ulong CityHash64(ReadOnlySpan<byte> s)
    {
        unchecked
        {
            var len = (uint)s.Length;
            var n = s.Length;
            if (len <= 16)
            {
                if (len >= 8)
                {
                    var mul = K2 + len * 2;
                    ulong a = Fetch64(s, 0) + K2, b = Fetch64(s, n - 8);
                    return HashLen16(Rotate(b, 37) * mul + a, (Rotate(a, 25) + b) * mul, mul);
                }
                if (len >= 4) return HashLen16(len + (Fetch32(s, 0) << 3), Fetch32(s, n - 4), K2 + len * 2);
                if (len > 0)
                {
                    uint y = s[0] + ((uint)s[n >> 1] << 8), z = len + ((uint)s[n - 1] << 2);
                    return ShiftMix(y * K2 ^ z * K0) * K2;
                }
                return K2;
            }
            if (len <= 32)
            {
                var mul = K2 + len * 2;
                ulong a = Fetch64(s, 0) * K1, b = Fetch64(s, 8), c = Fetch64(s, n - 8) * mul, d = Fetch64(s, n - 16) * K2;
                return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d, a + Rotate(b + K2, 18) + c, mul);
            }
            if (len <= 64)
            {
                var mul = K2 + len * 2;
                ulong a = Fetch64(s, 0) * K2, b = Fetch64(s, 8), c = Fetch64(s, n - 24), d = Fetch64(s, n - 32);
                ulong e = Fetch64(s, 16) * K2, f = Fetch64(s, 24) * 9, g = Fetch64(s, n - 8), h = Fetch64(s, n - 16) * mul;
                var u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
                var v = ((a + g) ^ d) + f + 1;
                var w = Swap((u + v) * mul) + h;
                var x = Rotate(e + f, 42) + c;
                var y = (Swap((v + w) * mul) + g) * mul;
                var z = e + f + c;
                a = Swap((x + z) * mul + y) + b;
                b = ShiftMix((z + a) * mul + d + h) * mul;
                return b + x;
            }
            {
                var x = Fetch64(s, n - 40);
                var y = Fetch64(s, n - 16) + Fetch64(s, n - 56);
                var z = HashLen16(Fetch64(s, n - 48) + len, Fetch64(s, n - 24));
                var v = WeakHash32(s, n - 64, len, z);
                var w = WeakHash32(s, n - 32, y + K1, x);
                x = x * K1 + Fetch64(s, 0);
                var remaining = (len - 1) & ~63u;
                var i = 0;
                do
                {
                    x = Rotate(x + y + v.Lo + Fetch64(s, i + 8), 37) * K1;
                    y = Rotate(y + v.Hi + Fetch64(s, i + 48), 42) * K1;
                    x ^= w.Hi;
                    y += v.Lo + Fetch64(s, i + 40);
                    z = Rotate(z + w.Lo, 33) * K1;
                    v = WeakHash32(s, i, v.Hi * K1, x + w.Lo);
                    w = WeakHash32(s, i + 32, z + w.Hi, y + Fetch64(s, i + 16));
                    (z, x) = (x, z);
                    i += 64;
                    remaining -= 64;
                } while (remaining != 0);
                return HashLen16(HashLen16(v.Lo, w.Lo) + ShiftMix(y) * K1 + z, HashLen16(v.Hi, w.Hi) + x);
            }
        }
    }
}
