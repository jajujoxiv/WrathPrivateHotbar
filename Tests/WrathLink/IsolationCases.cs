using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal readonly record struct IsolationCases(int OrdinaryCases, int WrongAppearance, int WrongIntermediate,
    int WrongWidget, int WrongExecution, int OrdinaryComboQueries, int PrivateErrors, int RegistrationErrors,
    bool IdleDisplayAndUseAgree)
{
    internal bool Passed => OrdinaryCases > 0 && WrongAppearance == 0 && WrongIntermediate == 0 &&
        WrongWidget == 0 && WrongExecution == 0 && OrdinaryComboQueries == 0 && PrivateErrors == 0 && RegistrationErrors == 0 && IdleDisplayAndUseAgree;

    // Mirrors the reported idle flicker: the engine can reuse its previous apparent ID
    // and the HUD can reuse its own icon separately. Neither one needs another key press.
    internal static unsafe IsolationCases Observe(Func<Simulation, Plugin> load)
    {
        using var game = new Simulation { RenderCached = true, JobId = 35 };
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        const uint jolt = 37004;
        TestWrath.Root = TestWrath.NativeRoot = jolt;
        for (int b = 1; b <= 10; b++)
            for (int i = 0; i < 12; i++) game.Place(b, i, jolt);

        using var plugin = load(game);
        game.Tick();
        // Allow even versions with delayed hook setup to finish before comparison.
        Thread.Sleep(1560);
        game.Tick();
        game.Command("config");
        // First reproduce the report in isolation: even with global action replacement
        // paused, a native widget left showing a combo candidate can disagree with use.
        bool idleAgrees = true;
        foreach (uint next in new uint[] { 7505, 7507, 7505 })
        {
            TestWrath.Next = next;
            game.Module->GetSlotById(2, 0)->ApparentActionId = next;
            game.Widget(3, 0)->IconId = next + 500;
            game.RenderSlot(3, 0);
            game.DrawPrevented(3);
            idleAgrees &= game.Widget(3, 0)->IconId == jolt + 500;
        }
        game.PressSlot(3, 0);
        idleAgrees &= game.LastUsed == jolt;
        int cases = 0, appearance = 0, intermediate = 0, widget = 0, execution = 0, queries = 0, privateErrors = 0, registration = 0;
        for (int selected = 1; selected <= 10; selected++)
        {
            ImGui.Selection = $"HB{selected}";
            game.RenderGui();
            game.Tick();
            foreach (uint next in new uint[] { 7505, 7507, 7505 })
            {
                TestWrath.Next = next;
                for (int b = 1; b <= 10; b++)
                {
                    if (b == selected) continue;
                    // Wrath can re-arm after the previous framework update.
                    provider.EnableActionReplacingIfRequired();
                    int previousQueries = TestWrath.Queries;
                    for (int i = 0; i < 12; i++)
                    {
                        cases++;
                        var slot = game.Module->GetSlotById((uint)(b - 1), (uint)i);
                        slot->ApparentActionId = next;
                        slot->IconId = next + 500;
                        game.Widget(b, i)->IconId = next + 500;
                        if (game.AppearanceOf(b, i) != jolt) appearance++;
                        var prepared = game.RenderSlot(b, i);
                        if (prepared.ActionId != jolt || prepared.IconId != jolt + 500 ||
                            slot->ApparentActionId != jolt || slot->IconId != jolt + 500) intermediate++;
                    }
                    game.DrawPrevented(b);
                    for (int i = 0; i < 12; i++)
                    {
                        if (game.Widget(b, i)->IconId != jolt + 500) widget++;
                        provider.EnableActionReplacingIfRequired();
                        if (i % 2 == 0) game.PressPointer(b, i); else game.PressSlot(b, i);
                        if (game.LastUsed != jolt) execution++;
                        if (game.Module->GetSlotById((uint)(b - 1), (uint)i)->CommandId != jolt) registration++;
                    }
                    queries += TestWrath.Queries - previousQueries;
                }
                // Selected checks run separately from counting ordinary provider requests.
                game.Tick();
                for (int i = 0; i < 12; i++)
                {
                    var drawn = game.RenderSlot(selected, i);
                    game.PressPointer(selected, i);
                    if (drawn.ActionId != next || drawn.IconId != next + 500 || game.LastUsed != next) privateErrors++;
                    if (game.Module->GetSlotById((uint)(selected - 1), (uint)i)->CommandId != jolt) registration++;
                }
            }
        }
        return new(cases, appearance, intermediate, widget, execution, queries, privateErrors, registration, idleAgrees);
    }
}
