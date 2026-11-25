using System.Diagnostics;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using UE.Toolkit.Core.Types.Unreal.Factories;
using UE.Toolkit.Core.Types.Unreal.Factories.Interfaces;
using UE.Toolkit.Core.Types.Unreal.UE5_4_4;
using UE.Toolkit.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using Color = System.Drawing.Color;

namespace jjkccpc.essentials;

internal sealed unsafe class FreeBattle(IUnrealFactory factory, IUnrealObjects objects, Action<string, Color> log)
{
    // UGameFreeBattleTeamCharacterSelectWidget
    private const string Pattern = "48 8B C4 48 81 EC 98 00 00 00 48 89 58 F8 48 89 70 E8 4C 89 60 D8 4C 89 68 D0 4C 8B E9 4C 89 78 C0 E8 ?? ?? ?? ?? 49 8B 85 20 05 00 00 33 D2 48 8B 98 38 03 00 00 48 8B CB 48 8B 03 FF 90 C8 06 00 00";

    private const string Table = "CharacterSelectDataTable";
    private const string FirstIcon = "IconWidget_00";
    private const int RowMap = 0x30;    // UDataTable::RowMap
    private const int Class = 0x10;     // UObjectBase::ClassPrivate
    private const int Slot = 0x30;      // UWidget::Slot
    private const int Parent = 0x28;    // UPanelSlot::Parent
    private const int Content = 0x30;   // UPanelSlot::Content
    private const int Slots = 0x150;    // UPanelWidget::Slots

    private const double Icon = 88, Side = 10, GapX = -6, GapY = 40, Width = 1200, Height = 360;

    [Function(CallingConventions.Microsoft)]
    public delegate void Setup(nint self);

    private IHook<Setup>? _hook;

    public void Install(IStartupScanner scanner, IReloadedHooks hooks) => scanner.AddMainModuleScan(Pattern, result =>
    {
        try
        {
            if (!result.Found) throw new Exception();
            _hook = hooks.CreateHook<Setup>(Impl, Process.GetCurrentProcess().MainModule!.BaseAddress + result.Offset).Activate();
        }
        catch (Exception)
        {
            log("Free Battle is gonna hide fighters past 32 because game version isn't correct", Color.Red);
        }
    });

    private void Impl(nint self)
    {
        try
        {
            Grow(self);
        }
        catch (Exception ex)
        {
            log($"Can't add icons for free battle \"{ex.Message}\"", Color.Red);
        }
        _hook!.OriginalFunction(self);
    }

    private void Grow(nint self)
    {
        var table = objects.FindObjectByName<UObjectBase>(Table);
        if (table == null) return;
        var map = (nint)table.Self + RowMap;
        var need = *(int*)(map + 0x08) - *(int*)(map + 0x34);
        var first = factory.CreateUObject(self).ClassPrivate.PropertyLink.FirstOrDefault(p => p.NamePrivate == FirstIcon);
        if (first == null) return;
        var icon = *(nint*)(self + first.Offset_Internal);
        var box = *(nint*)(*(nint*)(icon + Slot) + Parent);
        var have = *(int*)(box + Slots + 8);
        if (need <= have) return;

        var library = (nint)objects.FindObjectByName<UObjectBase>("Default__WidgetBlueprintLibrary")!.Self;
        var args = stackalloc nint[4];
        for (var i = have; i < need; i++)
        {
            args[0] = self; args[1] = *(nint*)(icon + Class); args[2] = 0; args[3] = 0;
            Call(library, "Create", args, 0x20);
            if (args[3] == 0) throw new Exception("Create");
            args[0] = args[3]; args[1] = 0;
            Call(box, "AddChildToWrapBox", args, 0x10);
        }

        // one scale for everything
        var scale = 0.0;
        for (var rows = 1; rows <= need; rows++)
        {
            var perRow = (need + rows - 1) / rows;
            var wide = Width / (perRow * (Icon + 2 * Side + GapX) - GapX);
            var tall = Height / (rows * (Icon + GapY) - GapY);
            scale = Math.Max(scale, Math.Min(1, Math.Min(wide, tall) * 0.995));
        }

        var side = (float)((Icon / 2 + Side) * scale - Icon / 2);
        var top = (float)(Icon / 2 * (scale - 1));
        var margin = stackalloc float[4] { side, top, side, top };
        var pair = stackalloc double[2] { scale, scale };
        var slots = *(nint**)(box + Slots);
        for (var i = 0; i < need; i++)
        {
            Call(slots[i], "SetPadding", margin, 0x10);
            Call(*(nint*)(slots[i] + Content), "SetRenderScale", pair, 0x10);
        }
        pair[0] = GapX * scale;
        pair[1] = GapY * scale;
        Call(box, "SetInnerSlotPadding", pair, 0x10);
    }

    private void Call(nint obj, string name, void* args, int size)
    {
        IUFunction? function = null;
        for (var c = factory.CreateUObject(obj).ClassPrivate; function == null && c != null; c = c.GetSuperClass())
            function = c.GetFunction(name);
        if (function == null || function.GetTotalParameterSize() != size) throw new Exception(name);
        factory.ProcessEvent!(obj, function, (nint)args);
    }
}
