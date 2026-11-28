using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal readonly record struct ReportedSymptoms(bool VirtualIcons, bool OtherSkillPulse, bool SingleBarCombo, bool HideOriginal)
{
    // Measure through the UI, input and native-addon entry points with the exact
    // combination of switches in the report. Older and revised builds run the same steps.
    internal static unsafe ReportedSymptoms Observe(Func<Simulation, Plugin> load)
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var wrath = new ActionReplacer();
        Service.ActionReplacer = wrath;
        TestWrath.Next = 300;
        using var plugin = load(game);
        game.Tick();
        game.Command("config");
        ImGui.Selection = "HB8";
        game.RenderGui();
        game.Tick();
        game.Place(5, 2, 750);
        game.Bind(5, 2, 0x42);
        game.Tick();
        AddonActionBarBase.Pulses.Clear();
        UIInputData.Down.Add(0x42);
        game.Tick();
        game.RenderGui();
        bool icons = ImGui.BarOpened && ImGui.Bar.Icons.Any(x => x.Icon == 800);
        bool pulse = AddonActionBarBase.Pulses.Contains((5, 2)) && ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0;
        bool separate = true;
        for (int b = 1; b <= 10; b++)
            separate &= game.RenderSlot(b, 0).ActionId == (b == 8 ? 300 : 100);
        bool hidden = !game.Huds[7].RootNode->Visible || game.DrawPrevented(8);
        return new(icons, pulse, separate, hidden);
    }
}
