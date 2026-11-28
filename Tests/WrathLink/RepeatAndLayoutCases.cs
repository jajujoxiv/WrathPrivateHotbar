using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class RepeatAndLayoutCases
{
    private const string Horizontal = "横の枠間隔（48px基準）", Vertical = "縦の枠間隔（48px基準）";
    private static string Settings(Simulation game) => Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json");
    private static bool Close(float a, float b) => Math.Abs(a - b) < .003f;

    internal static void SpacingAndText(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Settings(game), """{"KeyAlign":1,"Schema":2,"Bar":10,"Size":56,"Columns":6,"Gap":4,"Locked":true,"Pulse":false,"Sound":false,"X":31,"Y":29}""");
        using var plugin = load(game);
        int[] keys = [1, 2, 3, 4, 8, 0];
        for (int i = 0; i < keys.Length; i++) game.SetHint(10, i, keys[i].ToString());
        ActionManager.Timers[300] = new() { CooldownActive = true, PrimaryTotalTime = 120, PrimaryElapsedTime = 23 };
        game.Tick(); game.RenderGui();
        var old = Preferences.Load(Settings(game));
        check(old.Schema == 3 && old.ColumnGap == 4 && old.RowGap == 4 && !old.Pulse && !old.Sound && old.Locked && old.Bar == 10,
            "upgrading schema 2 reset saved controls or one axis of spacing");
        game.Command("config"); ImGui.OpenHeader = "配置・連打";
        Vector2 At(int i) => ImGui.Targets[i.ToString()].At;
        var first = At(0); float beforeX = At(5).X, beforeY = At(6).Y;
        ImGui.FloatLabel = Horizontal; ImGui.FloatIncrement = true; game.RenderGui();
        check(Close(At(5).X - beforeX, 5 * .1f * 56 / 48) && Close(At(6).Y, beforeY) && At(0) == first,
            "0.1px horizontal click lost precision, moved a row, or changed origin");
        ImGui.FloatLabel = Vertical; ImGui.FloatNext = 2.3f; game.RenderGui();
        check(Close(At(6).Y - At(0).Y - 56, 2.3f * 56 / 48) &&
            Close(At(1).X - At(0).X - 56, 4.1f * 56 / 48), "independent row and column pitches failed");
        var stored = Preferences.Load(Settings(game));
        check(stored.ColumnGap == 4.1f && stored.RowGap == 2.3f && !File.ReadAllText(Settings(game)).Contains("\"Gap\":"),
            "fractional gaps didn't round-trip, or obsolete integer layout was re-saved");

        TextRun SlotText(int i, string text)
        {
            var center = At(i) + new Vector2(28);
            return ImGui.Bar.Runs.Single(t => t.Text == text && t.Color == 0xFFFFFFFF &&
                t.ClipMin.X < center.X && t.ClipMax.X > center.X && t.ClipMin.Y < center.Y && t.ClipMax.Y > center.Y);
        }
        static float Height(TextRun run) => run.Max.Y - run.Min.Y;
        float timerDefault = Height(SlotText(0, "97"));
        float[] keyDefault = keys.Select((key, i) => Height(SlotText(i, key.ToString()))).ToArray();
        ImGui.SliderLabel = "キー文字サイズ (%)"; ImGui.SliderNext = 85; game.RenderGui();
        for (int i = 0; i < keys.Length; i++)
        {
            var key = SlotText(i, keys[i].ToString());
            check(Close(Height(key) / keyDefault[i], .85f) && Math.Abs((key.Min.X + key.Max.X) / 2 - At(i).X - 28) < .7,
                $"key {keys[i]} lost independent scale or center");
        }
        check(Close(Height(SlotText(0, "97")), timerDefault), "key slider altered timer");
        ImGui.SliderLabel = "リキャスト文字サイズ (%)"; ImGui.SliderNext = 80; game.RenderGui();
        check(Close(Height(SlotText(0, "97")) / timerDefault, .8f) &&
            Close(Height(SlotText(2, "3")) / keyDefault[2], .85f), "independent timer slider failed");
        stored = Preferences.Load(Settings(game));
        check(stored.KeyTextPercent == 85 && stored.TimerTextPercent == 80 && stored.ColumnGap == 4.1f && stored.RowGap == 2.3f,
            "font adjustment was lost or overwrote a gap");
        ImGui.SliderLabel = "リキャスト文字サイズ (%)"; ImGui.SliderNext = 140; game.RenderGui();
        ImGui.SliderLabel = "キー文字サイズ (%)"; ImGui.SliderNext = 140; game.RenderGui();
        var largeKey = SlotText(4, "8");
        check(Close(Height(SlotText(0, "97")) / timerDefault, 1.4f) &&
            Close(Height(largeKey) / keyDefault[4], 1.4f) && largeKey.ClipMin.Y <= largeKey.Min.Y,
            "maximum font setting plateaued early or cropped the shortcut");
        ImGui.OpenHeader = null;
    }

    internal static void NextSkill(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation(); using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Settings(game), """{"Schema":3,"Bar":10,"PulseMin":60,"PulseMax":61,"Turbo":false}""");
        game.Bind(10, 0, 0x41);
        // The same chord also exists on the ordinary A slot from before the press.
        game.Bind(6, 2, 0x41);
        game.Place(6, 2, 300); game.Place(9, 4, 400); game.Place(2, 8, 500);
        using var plugin = load(game);
        game.Tick(); AddonActionBarBase.Pulses.Clear();
        var pulses = AddonActionBarBase.Pulses; var manager = ActionManager.Instance();
        void Beat() { Thread.Sleep(76); game.Tick(); game.RenderGui(); }
        UIInputData.Down.Add(0x41); game.PressSlot(10, 0); game.Tick();
        manager->AnimationLock = .5f; ActionManager.Status[300] = 579;
        int uses = game.Dispatches;
        Beat(); check(pulses.SequenceEqual([(6, 2)]), "A repeated while its animation and recast were locked");
        TestWrath.Next = 400; Beat();
        check(pulses.SequenceEqual([(6, 2), (9, 4)]) && ImGui.Bar.Icons.Any(i => i.Icon == 900), "A did not switch to B on the next beat");
        TestWrath.Next = 500; Beat();
        check(pulses[^1] == (2, 8) && pulses.Count == 3 && ImGui.Bar.Icons.Any(i => i.Icon == 1000),
            "new suggestion C required an intervening execution of B");
        TestWrath.Next = 888; Beat(); check(pulses.Count == 3, "missing C replacement reused the previous normal slot");
        TestWrath.Next = 0; Beat(); check(pulses.Count == 3, "unknown suggestion illuminated a registered opener");
        TestWrath.Next = 300; manager->AnimationLock = 0; Beat();
        check(pulses.Count == 3, "same skill reapplied before its native status became ready");
        ActionManager.Status[300] = 0; Beat();
        check(pulses.Count == 4 && pulses[^1] == (6, 2), "legitimate consecutive A was suppressed after it recovered");
        TestWrath.Next = 400; UIInputData.Down.Clear(); game.Tick(); Beat();
        check(pulses.Count == 4 && pulses[^1] == (6, 2), "release triggered the next candidate");
        Thread.Sleep(260); game.Tick(); Beat();
        check(pulses.Count == 4 && game.Dispatches == uses && UIGlobals.Sounds == AddonActionBarBase.Pulses.Count,
            "preview executed a skill, emitted sound, or continued after release");
    }

    internal static void TurboAndExternal(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        foreach (bool external in new[] {false, true})
        {
            using var game = new Simulation(); using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
            game.ReAction.IsLoaded = external;
            File.WriteAllText(Settings(game), """{"Schema":3,"Bar":4,"Turbo":true,"RepeatOutOfCombat":true,"RepeatMs":60,"PulseMin":60,"PulseMax":61}""");
            game.Bind(4, 1, 0x42); game.Place(7, 2, 300); game.Place(1, 8, 400);
            using var plugin = load(game);
            game.Tick(); AddonActionBarBase.Pulses.Clear(); UIInputData.Down.Add(0x42);
            if (external)
            {
                // Source-less external dispatch. Its slot changes to B before the receipt is drained.
                game.Tick(); game.DirectUse(300);
                TestWrath.Next = 400; Thread.Sleep(75); game.Tick();
                check(AddonActionBarBase.Pulses.SequenceEqual([(7, 2), (1, 8)]) && UIGlobals.Sounds == AddonActionBarBase.Pulses.Count,
                    "late external acceptance replayed A instead of moving the still-held key to B");
                Thread.Sleep(76); game.Tick();
                check(AddonActionBarBase.Pulses[^1] == (1, 8) && UIGlobals.Sounds == AddonActionBarBase.Pulses.Count && game.Numbered.Count == 0,
                    "external repeat preview dispatched an extra input or reused A");
            }
            else
            {
                game.PressSlot(4, 1); game.Tick(); TestWrath.Next = 400;
                Thread.Sleep(76); game.Tick();
                check(game.Numbered.SequenceEqual([(4, 1), (4, 1)]) && game.Dispatches == 2 &&
                    game.LastUsed == 400 && UIGlobals.Sounds == 2 && AddonActionBarBase.Pulses.SequenceEqual([(7, 2), (1, 8)]),
                    "built-in repeat dispatched to an ordinary slot or used the old combo action");
            }
        }
    }

    internal static void PressSources(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation(); using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Settings(game), """{"Schema":3,"Bar":8,"Pulse":false,"Sound":true,"Size":56}""");
        game.Bind(8, 0, 0x41); game.Bind(8, 1, 0x42);
        game.SetHint(8, 0, "1"); game.SetHint(8, 1, "2"); game.SetHint(8, 2, "3");
        game.SetHint(8, 3, "4");
        using var plugin = load(game);
        game.Tick(); game.RenderGui(); AddonActionBarBase.Pulses.Clear();
        bool Lit(int i)
        {
            var rect = ImGui.Targets[i.ToString()]; var center = rect.At + rect.Size / 2;
            var rim = ImGui.Bar.Rims.SingleOrDefault(r => (r.Color & 0xFFFFFF) == 0xD5E9F2 &&
                Vector2.Distance((r.Min + r.Max) / 2, center) < .01f);
            return rim.Thickness >= 1 && rim.Rounding > 1 && rim.Color >> 24 >= 48 &&
                ImGui.Bar.Runs.Any(t => t.Text == (i + 1).ToString() && t.Color == 0xFFE9FAFF &&
                t.ClipMin.X < center.X && t.ClipMax.X > center.X);
        }
        game.Huds[4].CurrentPulsingSlots = 4; game.Tick(); game.RenderGui();
        check(!Lit(2), "ordinary HUD pulses leaked onto the private bar");
        game.Huds[7].CurrentPulsingSlots = 4; game.Tick(); game.RenderGui();
        check(Lit(2) && !Lit(0), "native selected press on hidden source missed its private rim or key text");
        game.Huds[7].CurrentPulsingSlots = 0;
        UIInputData.Down.Add(0x41); UIInputData.Down.Add(0x42); game.Tick(); game.RenderGui();
        check(Lit(0) && Lit(1) && !Lit(3), "overlapping real keys lost a private press or lit a neighbor");
        Thread.Sleep(225); game.RenderGui();
        check(Lit(0) && Lit(1) && !Lit(2), "holding real keys did not keep both rims visible");
        UIInputData.Down.Clear(); game.Tick();
        game.RejectNext = true; game.PressPointer(8, 3); game.Tick(); game.RenderGui();
        check(Lit(3) && AddonActionBarBase.Pulses.Count == 0 && UIGlobals.Sounds == 0 && game.Dispatches == 1,
            "rejected native dispatch without a key sample failed private feedback or emitted an external effect");
        Thread.Sleep(225); game.RenderGui();
        check(!Lit(3), "released private button stayed lit");
    }
}
