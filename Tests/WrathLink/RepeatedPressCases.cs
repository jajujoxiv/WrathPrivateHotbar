using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class RepeatedPressCases
{
    private static void Configure(Simulation game)
    {
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            """{"Schema":3,"Bar":8,"PulseMin":159,"PulseMax":160,"Sound":true,"Turbo":false}""");
        game.ReAction.IsLoaded = true;
        game.Target = 55;
        game.Bind(8,0,0x41);
        game.Place(3,0,300); game.Place(5,1,400); game.Place(7,2,500);
        AddonActionBarBase.PulseDurationMs = 1600;
    }

    // Record native render-time colors, private draw-list commands and actual sound
    // calls independently, not the number of pulse requests sent to the game.
    internal static void BeforeUse(Func<Simulation,Plugin> load, Action<bool,string> check, bool mouse, bool casting)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation { AutoDraw = false };
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        using var plugin = load(game);
        game.Tick(); game.RenderGui();
        if (mouse) { ImGui.ClickSlot = 0; ImGui.Press = true; game.RenderGui(); ImGui.Press = false; }
        else DesktopInput.DesktopKeys = [0x41];
        var am = ActionManager.Instance();
        TestWrath.Next = 300;
        if (casting)
        {
            game.DeferNextCast = true; game.PressSlot(8,0);
            am->CastActionId = am->CastSpellId = 300; am->CastTimeTotal = 1.2f; am->CastTargetId = game.Target;
        }
        else
        {
            game.PressSlot(8,0);
            *am->Gcd = new() { IsActive = true, Total = 1.2f, ActionId = 300 };
        }

        int at = 0, lastLevel = 0, previousPrivate = 0;
        var rises = new List<int>(); var clicks = new List<int>(); var privatePeaks = new List<int>();
        int cClicks = 0, afterRelease = 0;
        game.AtNativeDraw = bar => {
            if (bar != 5) return;
            var node = &game.Widget(5,1)->IconImage->AtkResNode;
            int value = node->AddRed_2 + node->AddGreen_2;
            if (value >= lastLevel + 18) rises.Add(at);
            lastLevel = value;
        };
        for (; at <= 2100; at += 20)
        {
            if (at != 0) clock.Advance(20);
            if (at < 1200)
            {
                if (casting) am->CastTimeElapsed = at / 1000f;
                else am->Gcd->Elapsed = at / 1000f;
            }
            if (at == 160) TestWrath.Next = 400;
            if (at == 480) { game.RejectNext = true; game.PressSlot(8,0); }
            if (at == 1200)
            {
                if (casting)
                {
                    am->CastActionId = am->CastSpellId = 0; am->CastTimeElapsed = am->CastTimeTotal;
                    game.DirectUse(300);
                }
                game.PressSlot(8,0);
                *am->Gcd = new() { IsActive = true, Elapsed = .01f, Total = 2.5f, ActionId = 400 };
            }
            if (at == 1400) TestWrath.Next = 500;
            if (at == 1740)
            {
                if (mouse) { ImGui.Release = true; game.RenderGui(); ImGui.Release = false; ImGui.ClickSlot = -1; }
                else DesktopInput.DesktopKeys!.Clear();
            }
            game.Tick();
            foreach (int bar in new[]{3,5,7})
            {
                int before = UIGlobals.Sounds;
                game.DrawNative(bar);
                int delta = UIGlobals.Sounds - before;
                if (delta != 0 && bar == 5) clicks.Add(at);
                if (at >= 1200 && at < 1740 && bar == 7) cClicks += delta;
                if (at >= 1740) afterRelease += delta;
                check(delta <= 1, "more than one click on one native onset");
            }
            game.RenderGui();
            var box = ImGui.Targets["0"];
            var rim = ImGui.Bar.Rims.Where(r => (r.Color & 0xffffff) == 0xd5e9f2 &&
                Math.Abs((r.Min.X+r.Max.X)/2 - (box.At.X+box.Size.X/2)) < .1)
                .Select(r => (int)(r.Color >> 24)).DefaultIfEmpty().Max();
            if (rim > previousPrivate + 60 && at >= 160 && at < 1200) privatePeaks.Add(at);
            previousPrivate = rim;
            var b = &game.Widget(5,1)->IconImage->AtkResNode;
            check(b->AddGreen == 0 && b->AddGreen_2 == 0, "draw-time accent leaked into the game's next update");
        }
        int[] expected = [160,320,480,640,800,960,1120];
        check(rises.SequenceEqual(expected) && clicks.SequenceEqual(expected) && privatePeaks.SequenceEqual(expected),
            $"B native=[{string.Join(',',rises)}] SE=[{string.Join(',',clicks)}] private=[{string.Join(',',privatePeaks)}]");
        check(cClicks >= 2 && afterRelease == 0 && game.Dispatches == (mouse ? 4 : 3) && game.Errors.Count == 0,
            $"after acceptance: C clicks={cClicks}, after release={afterRelease}, slot uses={game.Dispatches}");
        Console.WriteLine($"{(mouse?"mouse":"key")} / {(casting?"cast":"GCD")}: 7 B onsets, 7 matching private peaks and 7 SE before acceptance; zero B afterwards");
    }

    internal static void ReleaseBetweenUpdateAndDraw(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation { AutoDraw = false };
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        using var plugin = load(game); game.Tick();
        DesktopInput.DesktopKeys = [0x41];
        UIInputData.Down.Add(0x41);
        TestWrath.Next = 400;
        game.Tick(); game.DrawNative(5);
        int uses = game.Dispatches;
        clock.Advance(160); game.Tick(); // another B is ready, but the addon has not drawn it yet
        DesktopInput.DesktopKeys.Clear();
        game.DrawNative(5); game.DrawNative(5);
        check(AddonActionBarBase.Pulses.SequenceEqual([(5,1)]) && UIGlobals.Sounds == 1,
            "a physical release between Framework.Update and PreDraw allowed a second B");
        TestWrath.Next = 500;
        for (int i = 0; i < 8; i++) { clock.Advance(40); game.Tick(); game.DrawNative(7); }
        check(UIGlobals.Sounds == 1 && game.Dispatches == uses, "stale FF14 key state started C");
    }

    internal static void LateCompletion(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        using var plugin = load(game); game.Tick();
        DesktopInput.DesktopKeys = [0x41];
        var am = ActionManager.Instance();
        TestWrath.Next = 300; game.DeferNextCast = true; game.PressSlot(8,0);
        am->CastActionId = am->CastSpellId = 300; am->CastTimeTotal = .6f; am->CastTargetId = game.Target;
        game.Tick();
        for (int n = 1; n <= 45; n++)
        {
            clock.Advance(20);
            if (n < 30) am->CastTimeElapsed = n*.02f;
            if (n == 7) TestWrath.Next = 400;
            if (n == 20) { game.QueueNext = true; game.PressSlot(8,0); }
            if (n == 30)
            {
                // The game leaves the earlier B queue visible on the frame B begins.
                game.DeferNextCast = true; game.PressSlot(8,0);
                am->CastActionId = am->CastSpellId = 400; am->CastTimeElapsed = 0; am->CastTimeTotal = 1;
            }
            if (n > 30) am->CastTimeElapsed = (n-30)*.02f;
            if (n == 32) game.DirectUse(300); // A arrives late, while B casts
            if (n == 38) TestWrath.Next = 500;
            int count = AddonActionBarBase.Pulses.Count;
            game.Tick();
            if (n >= 30)
                check(AddonActionBarBase.Pulses.Skip(count).All(x => x == (7,2)),
                    "a consumed queue or delayed A replayed a previous skill after B started");
        }
        check(AddonActionBarBase.Pulses[^1] == (7,2) && game.Dispatches == 3,
            "late receipt disrupted the next C or changed actual execution");
    }

    internal static void SourceMaterial(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        var frame = game.InstallPressFrame(8,0);
        using var plugin = load(game); game.Tick(); game.RenderGui();
        DesktopInput.DesktopKeys = [0x41];
        game.Huds[7].PulseActionBarSlot(0); frame->AtkResNode.Visible = true;
        foreach (uint alpha in new uint[] {255,184,103,185})
        {
            clock.Advance(20); frame->AtkResNode.Color.RGBA = 0xffffffu | alpha << 24;
            game.Tick(); game.RenderGui();
            var art = ImGui.Bar.Images.Single(i => i.Texture == 900001);
            check(art.Color >> 24 == alpha && ImGui.Bar.Images.All(i => i.Texture != Simulation.PressTexture),
                "source HUD animation acquired a different alpha curve or an extra overlay");
        }
    }

    internal static void SourceRhythm(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        var sourceFrame = game.InstallPressFrame(8,0);
        var unrelated = game.InstallPressFrame(5,1);
        unrelated->PartsList->Parts->U = 0;
        using var plugin = load(game); game.Tick(); game.RenderGui();
        DesktopInput.DesktopKeys = [0x41];
        AddonActionBarBase.NativeSound = true;
        int last = 0;
        var peaks = new List<int>();
        var sourceTimes = new List<int>();
        int ordinarySounds = 0;
        for (int at = 0; at <= 960; at += 10)
        {
            if (at != 0) clock.Advance(10);
            TestWrath.Next = at < 310 ? 400u : 888u; // unassigned from here on; the selected key still repeats
            if (at <= 660 && at % 60 == 0)
            {
                sourceTimes.Add(at);
                game.Huds[7].PulseActionBarSlot(0);
                sourceFrame->AtkResNode.Visible = true;
                game.RejectNext = true; game.PressSlot(8,0);
            }
            if (at == 680) DesktopInput.DesktopKeys.Clear();
            unrelated->AtkResNode.Visible = at >= 160;
            game.Tick(); game.RenderGui();
            var box = ImGui.Targets["0"];
            var source = ImGui.Bar.Images.Where(i => i.Texture == 900001 && i.Min == box.At && i.Uv0.X == .25f)
                .Select(i => (int)(i.Color >> 24)).DefaultIfEmpty().Max();
            if (source > last + 32) peaks.Add(at);
            last = source;
            check(!ImGui.Bar.Images.Any(i => i.Texture == 900001 && (i.Min != box.At || i.Uv0.X != .25f)),
                "another slot or the mirrored ordinary HUD supplied the private press material");
            if (at == 300) ordinarySounds = UIGlobals.Sounds;
            if (at >= 320) check(UIGlobals.Sounds == ordinarySounds, "a source-only repeat played an ordinary click");
        }
        check(peaks.SequenceEqual(sourceTimes) && ordinarySounds > 0 && game.Dispatches == sourceTimes.Count,
            $"selected-HB presses=[{string.Join(',',sourceTimes)}], private rises=[{string.Join(',',peaks)}], ordinary SE={ordinarySounds}");
        Console.WriteLine($"one selected key: {peaks.Count} native source pulses at 60ms -> {peaks.Count} matching private peaks at identical frames; no clicks from private-only repeats");
    }

    internal static void ExternalAttempts(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        Configure(game);
        using var plugin = load(game); game.Tick();
        DesktopInput.DesktopKeys = [];
        var am = ActionManager.Instance();
        TestWrath.Next = 300; game.DeferNextCast = true; game.PressSlot(8,0);
        am->CastActionId = am->CastSpellId = 300; am->CastTimeTotal = 2; am->CastTargetId = game.Target;
        game.Tick(); int before = UIGlobals.Sounds;
        TestWrath.Next = 400;
        for (int n = 1; n <= 32; n++)
        {
            clock.Advance(20); am->CastTimeElapsed = n * .02f;
            if (n is 4 or 12 or 20) { game.RejectNext = true; game.PressSlot(8,0); }
            if (n == 24) TestWrath.Next = 500;
            game.Tick();
        }
        check(UIGlobals.Sounds == before + 3 && AddonActionBarBase.Pulses.Count(x => x == (5,1)) == 3 &&
            !AddonActionBarBase.Pulses.Contains((7,2)) && game.Dispatches == 4,
            "three rejected external B inputs were not three presses, or idle time created another");
    }
}
