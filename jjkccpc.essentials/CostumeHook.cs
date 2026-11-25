using System.Diagnostics;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using UE.Toolkit.Core.Types.Unreal.UE5_4_4;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using Color = System.Drawing.Color;

namespace jjkccpc.essentials;

// the game only offers a costume if its item is in the save so make it yes lmao
internal sealed unsafe class CostumeHook(IReadOnlyCollection<string> items, IReadOnlyDictionary<string, (int Group, int Colour)> numbers, Action<string, Color> log)
{
    // bool IsCostumeFree(void* self, FGameDataTableRow_CharacterVariation* row)
    private const string Pattern = "40 53 48 83 EC 20 48 85 D2 74 ?? 48 83 7A 40 00 48 8D 5A 40 74 ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 74 ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 0F 95 C0";
    private const int ItemOffset = 0x40;     // Id_Item
    private const int ReleasedCall = 0x19;   // call <released?>
    private const int InSaveCall = 0x25;     // call <in save?>

    [Function(CallingConventions.Microsoft)]
    public delegate byte IsCostumeFree(nint self, nint variationRow);

    [Function(CallingConventions.Microsoft)]
    public delegate byte ItemCheck(nint itemName);

    private const string NumbersPattern = "48 8D 58 10 48 8B CB E8 ?? ?? ?? ?? 48 8B CB 41 89 06 E8 ?? ?? ?? ?? 89 06 B0 01";
    private const int GroupCall = 0x07;
    private const int ColourCall = 0x12;

    [Function(CallingConventions.Microsoft)]
    public delegate int ReadNumber(nint blueprintName);

    private IHook<IsCostumeFree>? _hook;
    private IHook<ReadNumber>? _group, _colour;
    private ItemCheck? _released, _inSave;
    private readonly Lazy<HashSet<ulong>> _names = new(() => items.Select(RawName).ToHashSet());
    private readonly Lazy<Dictionary<ulong, (int Group, int Colour)>> _numbers = new(() => numbers.ToDictionary(p => RawName(p.Key), p => p.Value));

    private static ulong RawName(string id)
    {
        var name = new FName(id);
        return *(ulong*)&name;
    }

    public void Install(IStartupScanner scanner, IReloadedHooks hooks)
    {
        scanner.AddMainModuleScan(Pattern, result =>
        {
            if (items.Count == 0) return;
            try
            {
                if (!result.Found) throw new Exception();
                var address = Process.GetCurrentProcess().MainModule!.BaseAddress + result.Offset;
                // the original is never called, only its two checks
                _released = hooks.CreateWrapper<ItemCheck>(CallTarget(address + ReleasedCall), out _);
                _inSave = hooks.CreateWrapper<ItemCheck>(CallTarget(address + InSaveCall), out _);
                _hook = hooks.CreateHook<IsCostumeFree>(Impl, address).Activate();
            }
            catch (Exception)
            {
                log("Costumes aren't gonna show up because game version isn't correct", Color.Red);
            }
        });

        scanner.AddMainModuleScan(NumbersPattern, result =>
        {
            if (numbers.Count == 0) return;
            try
            {
                if (!result.Found) throw new Exception();
                var address = Process.GetCurrentProcess().MainModule!.BaseAddress + result.Offset;
                _group = hooks.CreateHook<ReadNumber>(GroupImpl, CallTarget(address + GroupCall)).Activate();
                _colour = hooks.CreateHook<ReadNumber>(ColourImpl, CallTarget(address + ColourCall)).Activate();
            }
            catch (Exception)
            {
                log("Costume slots past 10 aren't gonna work because game version isn't correct", Color.Red);
            }
        });
    }

    private int GroupImpl(nint name) => _numbers.Value.TryGetValue(*(ulong*)name, out var n) ? n.Group : _group!.OriginalFunction(name);

    private int ColourImpl(nint name) => _numbers.Value.TryGetValue(*(ulong*)name, out var n) ? n.Colour : _colour!.OriginalFunction(name);

    private static long CallTarget(nint call)
    {
        if (*(byte*)call != 0xE8) throw new Exception();
        return call + 5 + *(int*)(call + 1);
    }

    private byte Impl(nint self, nint row)
    {
        if (row == 0) return 0;
        var itemName = row + ItemOffset;
        var item = *(ulong*)itemName;
        if (item == 0 || _names.Value.Contains(item)) return 1;
        return (byte)(_released!(itemName) != 0 && _inSave!(itemName) != 0 ? 1 : 0);
    }
}
