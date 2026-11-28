using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class InteractionCases
{
    internal static void Feedback(Func<Simulation, Plugin> load, Action<bool, string> check, Action<Simulation, int> click)
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        using var plugin = load(game);
        game.Tick();
        game.Command("config");
        ImGui.Selection = "HB3";
        game.RenderGui();
        game.Place(6, 3, 300);
        game.Place(9, 2, 400);
        game.Bind(3, 0, 0x41);
        game.Tick();
        var pulses = AddonActionBarBase.Pulses;
        pulses.Clear();

        UIInputData.Down.Add(0x41);
        game.RejectNext = true;
        game.PressSlot(3, 0);
        game.Tick();
        check(pulses.SequenceEqual([(6, 3)]) && UIGlobals.Sounds == 1, "first visible native press was not paired");

        game.PressSlot(3, 0);                        // accepted 300
        TestWrath.Next = 400;                        // next suggestion has already changed
        game.Tick();
        game.RenderGui();
        check(pulses.SequenceEqual([(6, 3)]) && UIGlobals.Sounds == 1 &&
            ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0, "the accepted skill was not visible and audible");
        int uses = game.Dispatches;
        Thread.Sleep(225);
        game.Tick();
        check(pulses.SequenceEqual([(6, 3), (9, 2)]) && game.Dispatches == uses && UIGlobals.Sounds == 2,
            "holding the key failed to preview the next skill exactly once or injected use/sound");
        UIInputData.Down.Clear();
        game.Tick();
        Thread.Sleep(305);
        game.Tick();
        check(pulses.Count == 2, "key release did not end repeat feedback");

        game.QueueNext = true;
        UIInputData.Down.Add(0x41); game.PressPointer(3, 0); // a new real tap queues 400
        UIInputData.Down.Clear(); // released before the next sampled frame
        game.Tick();
        check(pulses.Count == 3 && pulses[^1] == (9, 2) && UIGlobals.Sounds == 3, "queued press did not pair its visible pulse with a sound");
        TestWrath.Next = 300;
        game.FireQueued();
        game.Tick();
        check(pulses.Count == 3 && pulses[^1] == (9, 2) && UIGlobals.Sounds == 3,
            "released, queued skill did not retain its source and actual result");

        TestWrath.Next = 777;                        // this skill is absent on every ordinary bar
        game.PressPointer(3, 0);
        game.Tick();
        check(pulses.Count == 3 && UIGlobals.Sounds == 3, "a missing normal target created a false pulse or an orphan SE");
        ImGui.ToggleLabel = "対応する通常HBの枠を発光させる";
        game.RenderGui();
        TestWrath.Next = 400;
        game.PressSlot(3, 0);
        game.Tick();
        check(pulses.Count == 3 && UIGlobals.Sounds == 3, "visuals disabled but emitted an orphan sound");
        game.PressPointer(9, 2);
        game.Tick();
        check(UIGlobals.Sounds == 3, "ordinary skill played a click with visuals off");
        ImGui.ToggleLabel = "純正HBの発光1回につきSEを1回鳴らす";
        game.RenderGui();
        game.PressSlot(3, 0);
        game.Tick();
        check(UIGlobals.Sounds == 3, "disabling SE did not silence it");

        ImGui.ToggleLabel = "対応する通常HBの枠を発光させる";
        game.RenderGui();
        game.Place(3, 5, 4);
        game.Module->GetSlotById(2, 5)->CommandType = RaptureHotbarModule.HotbarSlotType.GeneralAction;
        game.Place(2, 7, 3);
        game.PressPointer(3, 5);
        game.Tick();
        check(pulses.Count == 4 && pulses[^1] == (2, 7), "general Sprint did not match its ordinary action slot");

        // A real dispatch can span timer ticks inside a framework update. Its receipt
        // must be compared to the time it is drained, after returning from the game.
        ImGui.ToggleLabel = "純正HBの発光1回につきSEを1回鳴らす";
        game.RenderGui();
        game.BeforeNativePointer = () => Thread.Sleep(30);
        TestWrath.Next = 300;
        click(game, 0);
        game.BeforeNativePointer = null;
        check(game.LastUsed == 300 && pulses.Count == 5 && pulses[^1] == (6, 3) && UIGlobals.Sounds == 4,
            "a virtual click completing after frame start lost its pulse or SE");
    }

    internal static void JobChanges(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation { RenderCached = true };
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        using var plugin = load(game);
        game.Tick();
        game.Command("config");
        ImGui.Selection = "HB4";
        game.RenderGui();
        // One registered action from each ClassJob row 1..42 in the test data,
        // and one unknown job after them. Native and combo results are simulated.
        uint[] roots = [9, 53, 31, 75, 97, 119, 142, 4631, 4632, 4633, 4634, 4635, 4636, 4637, 4638,
            227, 210, 288, 29, 70, 49, 92, 114, 136, 158, 163, 25805, 17869, 2240, 2259, 2866, 3617,
            3596, 7477, 37004, 11383, 16137, 15989, 24373, 24283, 34606, 34650, 48000];
        for (int i = 0; i < roots.Length; i++)
        {
            game.JobId = i == roots.Length - 1 ? 200u : (uint)i + 1;
            TestWrath.Root = roots[i];
            TestWrath.Next = 51000u + (uint)i;
            game.Place(4, 1, roots[i]);
            game.Place(7, 1, roots[i]);
            game.Tick();
            foreach (uint native in new[] { roots[i], 49000u + (uint)i, roots[i] })
            {
                TestWrath.NativeRoot = native;
                var slot = game.Module->GetSlotById(6, 1);
                slot->ApparentActionId = TestWrath.Next;
                game.Widget(7, 1)->IconId = TestWrath.Next + 500;
                var display = game.RenderSlot(7, 1);
                game.DrawPrevented(7);
                game.PressPointer(7, 1);
                check(display.ActionId == native && game.Widget(7, 1)->IconId == native + 500 &&
                    game.LastUsed == native, $"native state lost on job {game.JobId}");
                game.PressSlot(4, 1);
                check(game.RenderSlot(4, 1).ActionId == TestWrath.Next && game.LastUsed == TestWrath.Next,
                    $"selected combo lost on job {game.JobId}");
            }
        }
        using var reloaded = new ActionReplacer();
        Service.ActionReplacer = reloaded;
        game.Notify();
        game.PressPointer(7, 1); // intentionally before the next framework update
        check(game.LastUsed == TestWrath.NativeRoot && !reloaded.ActionReplacingEnabled,
            "provider reload leaked a combo between framework updates");
        game.BeforeNativePointer = reloaded.EnableActionReplacingIfRequired;
        game.PressSlot(7, 1);
        check(game.LastUsed == TestWrath.NativeRoot, "re-arm inside the native by-ID dispatch leaked a combo");
        game.BeforeNativePointer = null;
        // The client owns PvP chains and non-action appearances such as items.
        var special = game.Module->GetSlotById(6, 1);
        special->ApparentActionMode = HotbarSlotApparentActionMode.PvPCombo;
        special->ApparentActionId = 12345;
        game.Widget(7, 1)->IconId = 12845;
        game.DrawPrevented(7);
        check(game.AppearanceOf(7, 1) == 12345 && game.RenderSlot(7, 1).ActionId == 12345 &&
            game.Widget(7, 1)->IconId == 12845, "native PvP combo appearance was overwritten");
    }

    internal static void Upgrade(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        string file = Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json");
        File.WriteAllText(file, """{"Bar":5,"Show":false,"HideOriginal":false,"Sound":false,"Pulse":false,"PulseMin":160,"PulseMax":160,"Columns":6,"X":50,"Y":80}""");
        using (var plugin = load(game))
        {
            game.Tick();
            game.RenderGui();
            check(ImGui.BarOpened && game.DrawPrevented(5) && !game.Huds[4].RootNode->Visible &&
                !game.DrawPrevented(1), "upgrade failed to replace the previously selected source on first frame");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var v = doc.RootElement;
            check(v.GetProperty("Schema").GetInt32() == 3 && v.GetProperty("Sound").GetBoolean() &&
                v.GetProperty("Pulse").GetBoolean() && v.GetProperty("PulseMin").GetInt32() < v.GetProperty("PulseMax").GetInt32() &&
                v.GetProperty("Columns").GetInt32() == 6 && v.GetProperty("X").GetInt32() == 50,
                "upgrade lost layout or failed to persist new feedback defaults");
            game.Command("config");
            ImGui.ToggleLabel = "疑似ホットバーを表示";
            game.RenderGui();
            game.Tick();
            check(game.Huds[4].RootNode->Visible && !game.DrawPrevented(5), "hiding virtual bar left original hidden");
        }
        using var reload = load(game);
        game.Tick();
        game.RenderGui();
        check(!ImGui.BarOpened && game.Huds[4].RootNode->Visible, "one-time migration overwrote a newer preference");
        game.Command("show");
        game.Tick();
        game.RenderGui();
        check(ImGui.BarOpened && !game.Huds[4].RootNode->Visible, "show did not restore the selected virtual bar");
        game.IsLoggedIn = false;
        game.Tick();
        check(game.Huds[4].RootNode->Visible && provider.ActionReplacingEnabled, "logout did not release the HUD and provider");
    }
}
