using System.Runtime.InteropServices;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using jjkccpc.essentials.Template;
using jjkccpc.essentials.Configuration;
using UE.Toolkit.Core.Types;
using UE.Toolkit.Core.Types.Interfaces;
using UE.Toolkit.Core.Types.Unreal.Factories;
using UE.Toolkit.Core.Types.Unreal.UE5_4_4;
using UE.Toolkit.Interfaces;
using UnrealEssentials.Interfaces;
using IUnrealMemory = UE.Toolkit.Interfaces.IUnrealMemory;
using Color = System.Drawing.Color;
#if DEBUG
using System.Diagnostics;
#endif

namespace jjkccpc.essentials;

public class Mod : ModBase // <= Do not Remove.
{
    private const string Folder = "jjkcc-essentials";
    private const string IniFile = "characters.ini";

    // ue4ss header dump shit
    private const string PreviewObj = "Default__GameWidgetSceneCaptureParameter_BP_C";
    private const int PreviewMap = 0x78;   // UGameWidgetSceneCaptureParameter::CharacterParameterMap

    private const string SeqObj = "Default__GameSequencerLoadingParameter_BP_C";
    private const int SeqList = 0x28;    // UGameSequencerLoadingParameter::FileParameters
    private const int SeqSize = 0xF8;    // FGameSequencerLoadingFileParameter
    private const int SeqFile = 0x08;
    private const int SeqWeapons = 0x68;
    private const int MapSize = 0x50;
    private const int SeqAnims = 0xB8;   // TArray<FGameSequencerAnimationReferenceParameter>
    private const int AnimSize = 0x40;
    private const int AnimPath = 0x30;
    private static readonly int[] SeqPtrs = { 0x18, 0x38, 0x58, 0x68, 0x88, 0xA8, 0xB8, 0xC8, 0xD8, 0xE8 };
    private static readonly string[] SeqKinds = { "SQ_Appearance_", "SQ_SuperCursedEnergy_", "SQ_TagCombo_", "SQ_Result_" };

    private const string OffsetObj = "Default__GameSequencerOffsetParameter_BP_C";
    private const int OffsetMap = 0x28;   // UGameSequencerOffsetParameter::SettingsMap

    private const string ViewerObj = "Default__GameWidgetCharacterParameter_BP_C";
    private const int ViewerMap = 0x78;    // UGameWidgetCharacterParameter::CharaModelViewerParameterMap
    private const int ViewerSize = 0xD0;   // FGameWidgetCharaModelViewerParameter
    private const int ViewerElem = 0xE0;
    private static readonly int[] ViewerPoses = { 0x18, 0x80 };
    private static readonly int[] MapPtrs = { 0x00, 0x20, 0x40 };

    private const string ImageObj = "Default__GameWidgetExchangeImageParameter_BP_C";
    private const int ImageMap = 0x28;   // UGameWidgetExchangeImageParameter::ImageOffsetParameterMap
    private const int ImageElem = 0x20;
    private const int ImageArray = 0x08;
    private const int ImageRow = 0x18;   // FGameWidgetExchangeImageOffsetParameter
    private const int ImageTypes = 20;   // EGameWidgetExchangeImageType::Max

    private const int ObjFlags = 0x08;   // UObjectBase::ObjectFlags
    private const int RF_NeedLoad = 0x400;

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly List<(string NewId, string SourceId, string FromMod)> _chars = new();
    private readonly HashSet<string> _noWeapon = new();
    private readonly HashSet<string> _cineOnly = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly AssetLists _assets;
    private readonly Costumes _costumes;
    private readonly CostumeHook _costumeHook;
    private readonly SoundStuff _sound;
    private readonly FreeBattle _freeBattle;
    private readonly IUnrealEssentials _essentials;
    private readonly IUnrealMemory _memory;
    private readonly IUnrealObjects _objects;

    private enum Result { Done, NotReady, Bad }

    private sealed class Target(string obj, Func<nint, Result> apply)
    {
        public readonly string Obj = obj;
        public readonly Func<nint, Result> Apply = apply;
        public bool Done;
        public int Tries;
    }

    private readonly List<Target> _targets = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private int _ticks;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

#if DEBUG
        Debugger.Launch();
#endif

        if (_hooks == null) throw Missing("Reloaded hooks");
        var toolkit = Controller<IToolkit>("UE Toolkit");
        var scanner = Controller<IStartupScanner>("R2 Sig Scan");
        _essentials = Controller<IUnrealEssentials>("Unreal Essentials");
        _objects = Controller<IUnrealObjects>("UE Toolkit");
        _memory = Controller<IUnrealMemory>("UE Toolkit");

        var ownDir = _modLoader.GetDirectoryForModId(_modConfig.ModId);
        _assets = new AssetLists(ownDir, Log);

        // has to be done before the game loads its tables
        _costumes = new Costumes(ownDir, dir => toolkit.AddObjectsPath(dir), Log);

        _costumeHook = new CostumeHook(_costumes.Owned, _costumes.Numbers, Log);
        _costumeHook.Install(scanner, _hooks);
        _sound = new SoundStuff(_costumes.Cues, Log);
        _sound.Install(scanner, _hooks);
        _freeBattle = new FreeBattle(Controller<IUnrealFactory>("UE Toolkit"), _objects, Log);
        _freeBattle.Install(scanner, _hooks);

        foreach (var mod in _modLoader.GetActiveMods())
            ReadMod(mod.Generic.ModId);
        _modLoader.ModLoaded += (_, config) => ReadMod(config.ModId);
        _modLoader.OnModLoaderInitialized += ModsLoaded;

        _targets.Add(new Target(PreviewObj, AddPreviews));
        _targets.Add(new Target(SeqObj, AddCinematics));
        _targets.Add(new Target(OffsetObj, AddOffsets));
        _targets.Add(new Target(ViewerObj, AddViewer));
        _targets.Add(new Target(ImageObj, AddImages));

        _timer = new System.Threading.Timer(_ => Poll(), null, 2000, 1000); // tekka certified
    }

    private Exception Missing(string what) => new($"[{_modConfig.ModId}] Couldn't get {what}, shit will break :(");

    private T Controller<T>(string name) where T : class
    {
        var controller = _modLoader.GetController<T>();
        return controller != null && controller.TryGetTarget(out var target) ? target : throw Missing(name);
    }

    private void ModsLoaded()
    {
        if (_assets.Build() is { } folder) _essentials.AddFromFolder(folder);
        _costumes.Finish();
    }

    private unsafe void Poll()
    {
        if (_chars.Count == 0 || !Monitor.TryEnter(_lock)) return;
        try
        {
            var n = ++_ticks;
            if (n > 120 && n % 5 != 0) return;

            foreach (var target in _targets.Where(t => !t.Done))
            {
                try
                {
                    var obj = _objects.FindObjectByName<UObjectBase>(target.Obj);
                    if (obj == null) continue;
                    var loading = (*(int*)((nint)obj.Self + ObjFlags) & RF_NeedLoad) != 0 && target.Tries < 15;
                    var result = loading ? Result.NotReady : target.Apply((nint)obj.Self);
                    if (result == Result.NotReady && ++target.Tries < 30) continue;
                    if (result != Result.Done) Log($"Broke ass {target.Obj} so new characters will be missing stuff", Color.Red);
                }
                catch (Exception ex)
                {
                    Log($"{target.Obj}: \"{ex.Message}\"", Color.Red);
                }
                target.Done = true;
            }

            if (_targets.All(t => t.Done))
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private void ReadMod(string modId)
    {
        try
        {
            var dir = _modLoader.GetDirectoryForModId(modId);
            if (dir == null || !_seen.Add(modId)) return;
            _assets.Add(modId, dir);
            _costumes.Add(modId, dir);
            var file = Path.Combine(dir, Folder, IniFile);
            if (!File.Exists(file)) return;

            foreach (var rawLine in File.ReadAllLines(file))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                // NEW=SOURCE[, option ...]
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                var right = parts.Length == 2 ? parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
                if (parts[0].Length == 0 || right.Length == 0)
                {
                    Log($"{modId} {IniFile} broky here \"{line}\"", Color.Red);
                    continue;
                }

                var newId = parts[0];
                foreach (var option in right.Skip(1).Select(o => o.ToLowerInvariant()))
                {
                    if (option == "no-weapon-binding") _noWeapon.Add(newId);
                    else if (option == "cinematics-only") _cineOnly.Add(newId);
                    else Log($"{modId} {IniFile} broky here \"{line}\"", Color.Red);
                }

                var entry = (newId, right[0], modId);
                if (!_chars.Contains(entry)) _chars.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log($"{IniFile} in {modId}: \"{ex.Message}\"", Color.Red);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TArray
    {
        public nint Data;
        public int Num;
        public int Max;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VictorDeadlock
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential, Size = ViewerSize)]
    private struct ModelViewerParams
    {
        public ulong FirstBytes;
    }

    private static unsafe ulong RawName(FName name) => *(ulong*)&name;

    private static unsafe string? GetName(ulong raw)
    {
        try
        {
            var text = (*(FName*)&raw).ToString();
            if (string.IsNullOrEmpty(text)) return null;
            if (RawName(new FName(text)) == raw) return text;
            var number = (uint)(raw >> 32);
            if (number != 0)
            {
                var numbered = $"{text}_{number - 1}";
                if (RawName(new FName(numbered)) == raw) return numbered;
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private unsafe bool CopyBlock(nint* slot)
    {
        var block = *slot;
        if (block == 0) return true;
        nint size = 0;
        try
        {
            if (!_memory!.GetAllocSize(block, ref size) || size <= 0) return false;
        }
        catch (Exception)
        {
            return false;
        }
        var copy = _memory.Malloc(size, MemoryConstants.DEFAULT_ALIGNMENT);
        if (copy == 0) return false;
        Buffer.MemoryCopy((void*)block, (void*)copy, (long)size, (long)size);
        *slot = copy;
        return true;
    }

    private unsafe byte* CopyRows(TArray* array, int rowSize, List<(int Source, ulong Name)> additions)
    {
        var total = array->Num + additions.Count;
        var data = (byte*)_memory!.Malloc((nint)total * rowSize, MemoryConstants.DEFAULT_ALIGNMENT);
        if (data == null) return null;
        Buffer.MemoryCopy((void*)array->Data, data, (long)total * rowSize, (long)array->Num * rowSize);
        for (var k = 0; k < additions.Count; k++)
        {
            var row = data + (long)(array->Num + k) * rowSize;
            Buffer.MemoryCopy(data + (long)additions[k].Source * rowSize, row, rowSize, rowSize);
            *(ulong*)row = additions[k].Name;
        }
        return data;
    }

    private static unsafe void Swap(TArray* array, byte* data, int count)
    {
        array->Data = (nint)data;
        array->Max = count;
        array->Num = count;
    }

    private unsafe List<nint>? ReadMap(nint map, int elementSize, int limit, Func<nint, uint> hashOfKey)
    {
        var data = *(nint*)map;
        var num = *(int*)(map + 0x08);
        var max = *(int*)(map + 0x0C);
        var heapBits = *(nint*)(map + 0x20);
        var numBits = *(int*)(map + 0x28);
        var hashSize = *(int*)(map + 0x48);
        if (data == 0 || num <= 0 || num > limit || max < num || numBits != num || hashSize <= 0 || (hashSize & (hashSize - 1)) != 0)
            return null;

        var bits = (uint*)(heapBits != 0 ? heapBits : map + 0x10);
        var elements = new List<nint>(num);
        for (var i = 0; i < num; i++)
        {
            if ((bits[i / 32] & (1u << (i % 32))) == 0) continue;
            var element = data + (nint)i * elementSize;
            var bucket = *(int*)(element + elementSize - 0x04);
            if (bucket != (int)(hashOfKey(element) & (uint)(hashSize - 1))) return null;
            elements.Add(element);
        }
        return elements;
    }

    private unsafe Result AddPreviews(nint obj)
    {
        using var map = new TMapDictionary<FName, nint>((TMap<FName, nint>*)(obj + PreviewMap), _memory!);
        if (map.Count == 0) return Result.NotReady;

        var added = new List<string>();
        foreach (var (newId, sourceId, fromMod) in _chars)
        {
            if (_cineOnly.Contains(newId)) continue;
            var dstKey = new FName(newId);
            if (map.ContainsKey(dstKey)) continue;
            if (!map.TryGetValue(new FName(sourceId), out var srcValue))
            {
                Log($"{fromMod} is trying to copy {sourceId} for {newId}, but that isn't a valid CP", Color.Red);
                continue;
            }

            var value = *srcValue.Value;
            map.Add(dstKey, new Ptr<nint>(&value));
            added.Add(newId);
        }
        if (added.Count > 0) Log($"Registered {string.Join(", ", added)}", Color.Green);
        return Result.Done;
    }

    // cinematics masterds
    private static string? SeqName(string name, string sourceId, string newId)
    {
        foreach (var prefix in SeqKinds)
        {
            var own = prefix + sourceId;
            if (name.StartsWith(own, StringComparison.Ordinal))
                return name.Length == own.Length ? prefix + newId : null;
        }
        return null;
    }

    private unsafe Result AddCinematics(nint obj)
    {
        var array = (TArray*)(obj + SeqList);
        var oldData = array->Data;
        var oldNum = array->Num;
        if (oldData == 0 || oldNum <= 0) return Result.NotReady;
        if (oldNum > 100_000 || array->Max < oldNum) return Result.Bad;

        var names = new string?[oldNum];
        var known = new HashSet<ulong>();
        for (var i = 0; i < oldNum; i++)
        {
            var raw = *(ulong*)(oldData + (nint)i * SeqSize);
            known.Add(raw);
            names[i] = GetName(raw);
        }

        var additions = new List<(int Source, ulong Name)>();
        var unbind = new HashSet<ulong>();
        foreach (var (newId, sourceId, _) in _chars)
        {
            for (var i = 0; i < oldNum; i++)
            {
                if (names[i] == null || SeqName(names[i]!, sourceId, newId) is not { } newName) continue;
                var raw = RawName(new FName(newName));
                if (!known.Add(raw)) continue;
                additions.Add((i, raw));
                if (_noWeapon.Contains(newId)) unbind.Add(raw);
            }
        }
        if (additions.Count == 0) return Result.Done;

        byte[]? emptyWeaponMap = null;
        for (var i = 0; i < oldNum && emptyWeaponMap == null; i++)
        {
            var map = oldData + (nint)i * SeqSize + SeqWeapons;
            if (*(nint*)map != 0 || *(int*)(map + 0x08) != 0 || *(nint*)(map + 0x20) != 0 || *(nint*)(map + 0x40) != 0) continue;
            emptyWeaponMap = new byte[MapSize];
            Marshal.Copy(map, emptyWeaponMap, 0, MapSize);
        }

        var newData = CopyRows(array, SeqSize, additions);
        if (newData == null) return Result.Bad;

        for (var k = 0; k < additions.Count; k++)
        {
            var entry = newData + (long)(oldNum + k) * SeqSize;
            *(ulong*)(entry + SeqFile) = additions[k].Name;
            if (emptyWeaponMap != null && unbind.Contains(additions[k].Name))
                Marshal.Copy(emptyWeaponMap, 0, (nint)(entry + SeqWeapons), MapSize);

            foreach (var offset in SeqPtrs)
                CopyBlock((nint*)(entry + offset));
            var animations = (TArray*)(entry + SeqAnims);
            for (var i = 0; animations->Data != 0 && i < animations->Num; i++)
                CopyBlock((nint*)(animations->Data + (nint)i * AnimSize + AnimPath));
        }

        Swap(array, newData, oldNum + additions.Count);
        return Result.Done;
    }

    private unsafe Result AddOffsets(nint obj)
    {
        using var map = new TMapDictionary<FName, VictorDeadlock>((TMap<FName, VictorDeadlock>*)(obj + OffsetMap), _memory!);
        if (map.Count == 0) return Result.NotReady;

        foreach (var (newId, sourceId, _) in _chars)
        {
            if (_cineOnly.Contains(newId)) continue;
            var dstKey = new FName(newId);
            if (map.ContainsKey(dstKey) || !map.TryGetValue(new FName(sourceId), out var srcValue)) continue;

            var value = *srcValue.Value;
            map.Add(dstKey, new Ptr<VictorDeadlock>(&value));
        }
        return Result.Done;
    }

    private unsafe Result AddViewer(nint obj)
    {
        if (sizeof(TMapElementHashable<FName, ModelViewerParams>) != ViewerElem) return Result.Bad;
        if (ReadMap(obj + ViewerMap, ViewerElem, 1000, e => (*(FName*)e).GetTypeHash()) == null)
            return Result.NotReady;

        using var map = new TMapDictionary<FName, ModelViewerParams>((TMap<FName, ModelViewerParams>*)(obj + ViewerMap), _memory!);
        foreach (var (newId, sourceId, _) in _chars)
        {
            if (_cineOnly.Contains(newId)) continue;
            var dstKey = new FName(newId);
            if (map.ContainsKey(dstKey) || !map.TryGetValue(new FName(sourceId), out var srcValue)) continue;

            var value = *srcValue.Value;
            var copied = true;
            foreach (var offset in ViewerPoses)
                copied = copied && ClonePose((nint)(&value) + offset, sourceId, newId);
            if (copied) map.Add(dstKey, new Ptr<ModelViewerParams>(&value));
        }
        return Result.Done;
    }

    private unsafe bool ClonePose(nint mapAddress, string sourceId, string newId)
    {
        foreach (var offset in MapPtrs)
            if (!CopyBlock((nint*)(mapAddress + offset))) return false;

        using var map = new TMapDictionary<FName, VictorDeadlock>((TMap<FName, VictorDeadlock>*)mapAddress, _memory!);
        foreach (var key in map.Keys)
        {
            var name = GetName(RawName(key));
            if (name == null || !name.StartsWith(sourceId + "_", StringComparison.Ordinal)) continue;
            var newKey = new FName(newId + name[sourceId.Length..]);
            if (map.ContainsKey(newKey) || !map.TryGetValue(key, out var srcValue)) continue;
            var value = *srcValue.Value;
            map.Add(newKey, new Ptr<VictorDeadlock>(&value));
        }
        return true;
    }

    private sealed class ImageList
    {
        public nint Array;
        public string?[] Ids = [];
        public readonly HashSet<ulong> Known = new();
        public readonly List<(int Source, ulong Name)> Additions = new();
    }

    // CP_060 or CP_060_00
    private static string? ImageId(string id, string sourceId, string newId)
    {
        if (!id.StartsWith(sourceId, StringComparison.Ordinal)) return null;
        if (id.Length == sourceId.Length) return newId;
        return id.Length == sourceId.Length + 3 && id[sourceId.Length] == '_' ? newId + id[sourceId.Length..] : null;
    }

    private unsafe Result AddImages(nint obj)
    {
        var elements = ReadMap(obj + ImageMap, ImageElem, ImageTypes, e => *(byte*)e);
        if (elements == null) return Result.NotReady;

        var lists = new List<ImageList>();
        foreach (var element in elements)
        {
            var type = *(byte*)element;
            var array = (TArray*)(element + ImageArray);
            var looksRight = type > 0 && type < ImageTypes && array->Num >= 0 && array->Num <= 500 && array->Max >= array->Num && (array->Num == 0 || array->Data != 0);
            for (var i = 0; looksRight && i < array->Num; i++)
            {
                var offset = (double*)(array->Data + (nint)i * ImageRow + 0x08);
                looksRight = Math.Abs(offset[0]) < 10_000 && Math.Abs(offset[1]) < 10_000;
            }
            if (!looksRight) return Result.NotReady;

            var list = new ImageList { Array = (nint)array, Ids = new string?[array->Num] };
            for (var i = 0; i < array->Num; i++)
            {
                var raw = *(ulong*)(array->Data + (nint)i * ImageRow);
                list.Known.Add(raw);
                list.Ids[i] = GetName(raw);
            }
            lists.Add(list);
        }

        foreach (var (newId, sourceId, _) in _chars)
        {
            if (_cineOnly.Contains(newId)) continue;
            foreach (var list in lists)
            {
                for (var i = 0; i < list.Ids.Length; i++)
                {
                    if (list.Ids[i] == null || ImageId(list.Ids[i]!, sourceId, newId) is not { } newName) continue;
                    var raw = RawName(new FName(newName));
                    if (list.Known.Add(raw)) list.Additions.Add((i, raw));
                }
            }
        }

        foreach (var list in lists.Where(l => l.Additions.Count > 0))
        {
            var array = (TArray*)list.Array;
            var data = CopyRows(array, ImageRow, list.Additions);
            if (data != null) Swap(array, data, array->Num + list.Additions.Count);
        }
        return Result.Done;
    }

    private void Log(string message, Color color) => _logger.WriteLine($"[{_modConfig.ModId}] {message}", color);

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        // Apply settings from configuration.
        // ... your code here.
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
