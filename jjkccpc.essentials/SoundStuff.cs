using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using Color = System.Drawing.Color;

namespace jjkccpc.essentials;

internal sealed unsafe class SoundStuff(Dictionary<string, string> cues, Action<string, Color> log)
{
    private const string Pattern = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 49 8B F8 48 8B EA 48 8B D9 48 85 C9 74 ?? 4D 85 C0 74 ?? E8 ?? ?? ?? ?? 4C 8B C7 48 8B D5 48 8B CB 8B F0 E8";
    private const int ErrorLea = 0x59;
    private const string ErrorId = "E2010030901";

    [Function(CallingConventions.Microsoft)]
    public delegate void SetCueName(nint player, nint acb, nint cueName);

    private byte[] _first = [];
    private IHook<SetCueName>? _hook;

    public void Install(IStartupScanner scanner, IReloadedHooks hooks) => scanner.AddMainModuleScan(Pattern, result =>
    {
        if (cues.Count == 0) return;
        try
        {
            var address = Process.GetCurrentProcess().MainModule!.BaseAddress + result.Offset;
            if (!result.Found || !IsCri(address)) throw new Exception();
            _first = cues.Keys.Select(k => (byte)k[0]).Distinct().ToArray();
            _hook = hooks.CreateHook<SetCueName>(Impl, address).Activate();
        }
        catch (Exception)
        {
            log("Can't find Criware shit will die :(", Color.Red);
        }
    });

    private static bool IsCri(nint function)
    {
        var lea = (byte*)function + ErrorLea;
        if (lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x15) return false;
        return Marshal.PtrToStringAnsi((nint)(lea + 7 + *(int*)(lea + 3)), ErrorId.Length) == ErrorId;
    }

    private void Impl(nint player, nint acb, nint cueName)
    {
        if (acb != 0 && cueName != 0 && Array.IndexOf(_first, *(byte*)cueName) >= 0
            && Marshal.PtrToStringAnsi(cueName) is { } name && cues.ContainsKey(name))
            acb = 0;
        _hook!.OriginalFunction(player, acb, cueName);
    }
}
