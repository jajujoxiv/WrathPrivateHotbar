using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class Program
{
    private static int checks;
    private static void Check(bool pass, string why)
    {
        checks++;
        if (!pass)
            throw new InvalidOperationException(why);
    }
    private static Plugin Load(Simulation s) => new(s, s, s, s, s, s, s, s, s, s, s, s, s);
    private static void ClickSlot(Simulation s, int slot)
    {
        ImGui.ClickSlot = slot;
        ImGui.Press = true;
        s.RenderGui();
        ImGui.Press = false;
        ImGui.Release = true;
        s.RenderGui();
        ImGui.Release = false;
        ImGui.ClickSlot = -1;
        s.Tick();
    }
    private static void SetOption(Simulation s, string name)
    {
        ImGui.ToggleLabel = name;
        s.RenderGui();
        s.Tick();
    }
    private static void Main()
    {
        var observed = ReportedSymptoms.Observe(Load);
        Console.WriteLine(observed);
        Check(observed == new ReportedSymptoms(true, true, true, true), "reported configuration failed");
        var isolation = IsolationCases.Observe(Load);
        Console.WriteLine(isolation);
        Check(isolation.Passed, "ordinary HUD isolation failed under cached or re-enabled rendering");
        CheckActiveSession();
        CheckUnavailableHooks();
        CheckProviderLifetime();
        InteractionCases.Feedback(Load, Check, ClickSlot);
        InteractionCases.JobChanges(Load, Check);
        InteractionCases.Upgrade(Load, Check);
        NativeHudCases.Layout(Load, Check);
        NativeHudCases.Repress(Load, Check);
        CaptionCases.Layout(Load, Check);
        CaptionCases.MissingFont(Load, Check);
        CaptionCases.Press(Load, Check);
        CaptionCases.AdjustableNumbers(Load, Check);
        RepeatAndLayoutCases.SpacingAndText(Load, Check);
        RepeatAndLayoutCases.NextSkill(Load, Check);
        RepeatAndLayoutCases.PressSources(Load, Check);
        RepeatAndLayoutCases.TurboAndExternal(Load, Check);
        EarlyPressCases.SequenceBoundaries(Check);
        EarlyPressCases.Caster(Load, Check, true, false);
        EarlyPressCases.Caster(Load, Check, false, false);
        EarlyPressCases.Caster(Load, Check, true, true);
        EarlyPressCases.Melee(Load, Check);
        EarlyPressCases.NativeVariant(Load, Check);
        EarlyPressCases.CancelAndQueue(Load, Check);
        EarlyPressCases.PrivateArt(Load, Check);
        EarlyPressCases.HiddenNative(Load, Check);
        EarlyPressCases.NativeLayers(Load, Check);
        SwiftcastCases.Run(Load, Check, ClickSlot);
        ReleaseAndSoundCases.KeyUp(Load, Check);
        ReleaseAndSoundCases.SourceAudio(Load, Check);
        ReleaseAndSoundCases.Surface(Load, Check);
        ReleaseAndSoundCases.NativeMaterial(Load, Check);
        StreamCases.AudibleFrames(Load, Check);
        StreamCases.ConsumedCast(Load, Check);
        foreach (bool mouse in new[]{false,true})
            foreach (bool casting in new[]{false,true}) RepeatedPressCases.BeforeUse(Load,Check,mouse,casting);
        RepeatedPressCases.ReleaseBetweenUpdateAndDraw(Load,Check);
        RepeatedPressCases.ExternalAttempts(Load,Check);
        RepeatedPressCases.SourceRhythm(Load,Check);
        RepeatedPressCases.SourceMaterial(Load,Check);
        PrivatePressPixelCases.Run(Load,Check);
        RepeatedPressCases.LateCompletion(Load,Check);
#if UPSTREAM
        Console.WriteLine("Provider implementation: supplied WrathCombo ActionReplacer method bodies");
#else
        Console.WriteLine("Provider implementation: deterministic fixture");
#endif
        Console.WriteLine($"PASS {checks} integration checks through framework, UI, input and addon events");
    }
    private static void CheckActiveSession()
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var upstream = new ActionReplacer();
        Service.ActionReplacer = upstream;
        Check(game.RenderSlot(6, 0).ActionId == 300, "test never reproduced global replacement before loading WPH");
        var plugin = Load(game);
        try
        {
            game.Tick();
            game.RenderGui();
            Check(ImGui.BarOpened && ImGui.Bar.Icons.Any(x => x.Icon == 800), "first displayed frame did not contain its selected Wrath skill");
            Check(!upstream.ActionReplacingEnabled && Service.Configuration.ActionChanging, "global was not paused or user's saved preference was overwritten");
            game.RenderGui();
            Check(ImGui.Bar.Icons.Any(x => x.Icon == 800), "private bar didn't draw Wrath result");
            game.Command("config");
            for (int selected = 1; selected <= 10; selected++)
            {
                ImGui.Selection = $"HB{selected}";
                game.RenderGui();
                game.Tick();
                foreach (uint stage in new uint[] { 100, 300, 400, 100 })
                {
                    TestWrath.Next = stage;
                    for (int b = 1; b <= 10; b++)
                    for (int i = 0; i < 12; i++)
                    {
                        uint expected = b == selected ? stage : 100;
                        var ui = game.RenderSlot(b, i);
                        Check(ui.IconId == expected + 500 && ui.ActionId == expected, "real render path leaked into another hotbar");
                        int n = game.Dispatches;
                        game.PressSlot(b, i);
                        Check(game.LastUsed == expected && game.Dispatches == n + 1, "wrong action or double use");
                    }
                }
                game.Tick();
            }
            TestWrath.Next = 300;
            ImGui.Selection = "HB8";
            game.RenderGui();
            game.Tick();
            for (int b = 1; b <= 10; b++)
                Check(game.Huds[b - 1].RootNode->Visible == (b != 8) && game.DrawPrevented(b) == (b == 8), "hidden source or another HUD incorrectly handled");
            game.RenderGui();
            Check(ImGui.BarOpened, "hiding original also hid virtual bar");
            game.Huds[7].RootNode->Visible = true;
            Check(game.DrawPrevented(8), "native redraw escaped hide setting");
            ImGui.Selection = "HB3";
            game.RenderGui();
            game.Tick();
            Check(game.Huds[7].RootNode->Visible && !game.DrawPrevented(8) && game.DrawPrevented(3), "changing source didn't restore old and hide new");
            SetOption(game, "元の選択HBを隠す");
            Check(game.Huds[2].RootNode->Visible && !game.DrawPrevented(3), "unchecking didn't restore original");
            Thread.Sleep(65); game.Tick(); // let the preceding 480 slot dispatches finish animating
            game.Place(6, 3, 300);
            game.Bind(3, 0, 0x41);
            game.Bind(5, 2, 0x42);
            game.Place(5, 2, 750);
            game.Tick();
            AddonActionBarBase.Pulses.Clear();
            UIInputData.Down.Add(0x41);
            game.PressSlot(3, 0);
            game.Tick();
            game.RenderGui();
            Check(AddonActionBarBase.Pulses.Contains((6, 3)) && ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0, "selected key didn't draw visible matching regular slot");
            int beats = AddonActionBarBase.Pulses.Count, used = game.Dispatches;
            Thread.Sleep(220);
            game.Tick();
            Check(AddonActionBarBase.Pulses.Count == beats, "an accepted skill was pressed again with no successor");
            game.Place(9, 2, 400); TestWrath.Next = 400;
            Thread.Sleep(220); game.Tick();
            Check(AddonActionBarBase.Pulses.Count == beats + 1 && AddonActionBarBase.Pulses[^1] == (9,2) &&
                game.Dispatches == used, "hold failed to move to the next skill, or lighting injected an action");
            UIInputData.Down.Clear();
            game.Tick();
            beats = AddonActionBarBase.Pulses.Count;
            Thread.Sleep(305);
            game.Tick();
            Check(AddonActionBarBase.Pulses.Count == beats, "release continued pulsing");
            UIInputData.Down.Add(0x42);
            game.Tick();
            game.RenderGui();
            Check(AddonActionBarBase.Pulses.Last() == (5, 2) && ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0, "unrelated ordinary skill had no pulse");
            UIInputData.Down.Clear();
            game.Tick();
            int before = game.Dispatches;
            TestWrath.Next = 300;
            ClickSlot(game, 0);
            Check(game.LastUsed == 300 && game.Dispatches == before + 1, "virtual mouse click didn't execute the displayed action once");
            upstream.EnableActionReplacingIfRequired();
            game.Tick();
            Check(!upstream.ActionReplacingEnabled && game.RenderSlot(1, 0).ActionId == 100, "provider re-enable leaked after next framework update");
            SetOption(game, "疑似ホットバーを表示");
            game.RenderGui();
            Check(!ImGui.BarOpened, "explicitly hidden virtual bar redrawn");
            game.Command("show");
            game.RenderGui();
            Check(ImGui.BarOpened, "show did not enable the bar");
            for (int i = 0; i < 12; i++)
                game.Place(3, i, 0);
            Thread.Sleep(72);
            game.Tick();
            game.RenderGui();
            Check(ImGui.BarOpened && ImGui.Bar.Labels.Any(l => l.Text.Contains("未登録")) && !ImGui.Bar.Labels.Any(l => l.Text == "12"), "all-empty selected bar lost its empty status or displayed numbered placeholders");
            game.Flags.Add(ConditionFlag.WatchingCutscene);
            game.Tick();
            game.RenderGui();
            Check(!ImGui.BarOpened, "cutscene suppression lost");
            game.Flags.Clear();
            game.Tick();
            game.RenderGui();
            Check(ImGui.BarOpened, "failed to return from cutscene");
            Check(game.Errors.Count == 0, "unexpected integration error: " + string.Join(", ", game.Errors));
        }
        finally { plugin.Dispose(); }
        Check(upstream.ActionReplacingEnabled && game.RenderSlot(2, 0).ActionId == 300, "unloading WPH didn't restore previously-enabled Wrath");
    }
    private static void CheckUnavailableHooks()
    {
        using var game = new Simulation { FailRouter = true, FailObserver = true };
        Service.Configuration.ActionChanging = true;
        using var upstream = new ActionReplacer();
        Service.ActionReplacer = upstream;
        game.Wrath.IsLoaded = false;
        using var plugin = Load(game);
        game.Tick();
        game.RenderGui();
        Check(ImGui.BarOpened && ImGui.Bar.Icons.Count == 12, "offline provider hid regular skills");
        Dalamud.Interface.Textures.Texture.PendingIcon = ImGui.Bar.Icons[0].Icon;
        game.RenderGui();
        Check(ImGui.BarOpened && ImGui.Bar.Labels.Any(l => l.Text == "1"), "waiting for an icon texture left the registered slot invisible");
        Dalamud.Interface.Textures.Texture.PendingIcon = 0;
        game.RenderGui();
        Check(ImGui.Bar.Icons.Count == 12, "loaded textures were not drawn on the next frame");
        game.Command("config");
        Check(game.DrawPrevented(1) && !game.Huds[0].RootNode->Visible, "offline provider blocked hide-original");
        game.Bind(7, 1, 0x44);
        game.Tick();
        AddonActionBarBase.Pulses.Clear();
        UIInputData.Down.Add(0x44);
        game.Tick();
        game.RenderGui();
        Check(AddonActionBarBase.Pulses.Contains((7, 1)) && ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0 && ImGui.BarOpened, "missing hooks blocked ordinary visuals");
        UIInputData.Down.Clear();
        game.Tick();
        UIInputData.Current->NumKeybinds = 1;
        int n = game.Dispatches;
        ClickSlot(game, 0);
        Check(game.Dispatches == n + 1, "unavailable key table blocked valid virtual mouse click");
        game.Module->ModuleReady = false;
        game.Tick();
        game.RenderGui();
        Check(ImGui.BarOpened && ImGui.Bar.Labels.Any(l => l.Text.Contains("読み込み中")), "loading bar missing");
        game.Module->ModuleReady = true;
        game.Wrath.IsLoaded = true;
        game.Notify();
        game.Tick();
        Check(!upstream.ActionReplacingEnabled, "global wasn't stopped when Wrath reloaded");
    }
    private static void CheckProviderLifetime()
    {
        using var game = new Simulation { ByContext = true };
        var link = new WrathLink(game);
        Service.Configuration.ActionChanging = false;
        using var a = new ActionReplacer();
        Service.ActionReplacer = a;
        link.Refresh();
        Check(link.Ready && link.Lookup!(100) == 300, "previously-off provider or context lookup failed");
        link.Release();
        Check(!a.ActionReplacingEnabled, "enabled a provider the user had already turned off");
        Service.Configuration.ActionChanging = true;
        a.EnableActionReplacingIfRequired();
        game.ByRegistry = true;
        link.Refresh();
        Check(link.Ready && !a.ActionReplacingEnabled, "installed-plugin lookup failed");
        Service.Configuration.ActionChanging = false;
        link.Release();
        Check(!a.ActionReplacingEnabled, "restored against a newer user decision");
        Service.Configuration.ActionChanging = true;
        a.EnableActionReplacingIfRequired();
        link.Refresh();
        game.Wrath.IsLoaded = false;
        link.Invalidate();
        Check(!link.Ready, "unload event left old endpoint armed");
        link.Refresh();
        Check(!link.Ready && link.Lookup == null && !a.ActionReplacingEnabled, "dead provider retained or called");
    }
}
