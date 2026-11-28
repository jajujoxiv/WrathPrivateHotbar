using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class EarlyPressCases
{
    private static void Configure(Simulation game, int bar, bool early)
    {
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            $$"""{"Schema":3,"Bar":{{bar}},"EarlyPreview":{{early.ToString().ToLowerInvariant()}},"PulseMin":80,"PulseMax":81,"Turbo":false,"Sound":true}""");
        game.Target = 0x12345;
        game.Bind(bar, 0, 0x41);
        Service.Configuration.ActionChanging = true;
    }

    internal static void SequenceBoundaries(Action<bool, string> check)
    {
        var trail = new SequencePreview();
        var src = new SequencePreview.Source(new(7, 3), 100, 50);
        long now = 10;
        void Feed(params uint[] ids) { foreach (uint id in ids) trail.Accept(src, id, ++now); }
        for (int i = 0; i < 2; i++) Feed(200,300,400);
        Feed(200,300);
        check(trail.Predict(src, 300, true, now) == 0, "two observations were enough to guess");
        Feed(400,200,300);
        check(trail.Predict(src, 300, true, now) == 400, "three confirmations failed to establish a provisional successor");
        check(trail.Predict(src with { Target = 60 }, 300, true, now) == 0 &&
            trail.Predict(src with { Slot = new(1, 0) }, 300, true, now) == 0 &&
            trail.Predict(src with { Registered = 333 }, 300, true, now) == 0 &&
            trail.Predict(src, 300, true, now + 8001) == 0, "prediction crossed a target, bar, root or time boundary");
        Feed(500);
        for (int i = 0; i < 5; i++) Feed(200,300,400);
        Feed(200,300);
        check(trail.Predict(src, 300, true, now) == 0, "an observed branch was predicted as deterministic again");

        trail.Reset();
        for (int i = 0; i < 4; i++) Feed(200,300,300);
        Feed(200);
        check(trail.Predict(src, 300, false, now) == 0, "the currently casting spell was guessed again");
    }

    internal static void Caster(Func<Simulation, Plugin> load, Action<bool, string> check, bool early, bool branched)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        Configure(game, 10, early);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        game.Place(4, 0, 200); game.Place(6, 1, 300); game.Place(7, 2, 400); game.Place(9, 3, 500);
        using var plugin = load(game);
        game.Tick();
        var pulses = AddonActionBarBase.Pulses;
        var am = ActionManager.Instance();
        void Use(uint id) { clock.Advance(120); TestWrath.Next = id; game.PressSlot(10, 0); game.Tick(); }
        for (int n = 0; n < 3; n++) { Use(200); Use(300); Use(400); }
        if (branched) { Use(200); Use(300); Use(500); }
        Use(200); pulses.Clear();
        int sounds = UIGlobals.Sounds;
        UIInputData.Down.Add(0x41);
        clock.Advance(120);
        TestWrath.Next = 300;
        game.DeferNextCast = true;
        game.PressSlot(10, 0); // real selected dispatch, use receipt arrives at cast end
        am->CastActionType = ActionType.Action; am->CastActionId = early && !branched ? 301u : 300u; am->CastSpellId = 300;
        am->CastTimeTotal = 2; am->CastTimeElapsed = 0; am->CastTargetId = game.Target;
        long began = Environment.TickCount64;
        void Frame(int ms)
        {
            clock.Advance(ms);
            am->CastTimeElapsed = (Environment.TickCount64 - began) / 1000f;
            game.Tick(); game.RenderGui();
        }
        Frame(0);
        check(pulses.SequenceEqual([(6, 1)]) && UIGlobals.Sounds == sounds + pulses.Count, "confirmed cast start had no paired native pulse and confirmed pulses and sounds paired");
        int dispatches = game.Dispatches;
        Frame(100);
        bool predicted = early && !branched;
        check(predicted ? pulses.SequenceEqual([(6, 1), (7, 2)]) : pulses.SequenceEqual([(6, 1)]),
            "5% cast: wrong behavior for an established, disabled or branched prediction");
        check(ImGui.Bar.Icons.Any(x => x.Icon == 800) && game.Dispatches == dispatches && UIGlobals.Sounds == sounds + pulses.Count,
            "speculation changed the executable private icon, injected a use, or emitted an unpaired click");
        int remote = pulses.Count;
        game.Target = 0x777;
        Frame(100);
        check(pulses.Count == remote, "old target rotation was shown for a newly selected target");
        game.Target = am->CastTargetId;
        TestWrath.Next = 500;
        Frame(100);
        check(pulses[^1] == (9, 3) && am->CastTimeElapsed < am->CastTimeTotal * .2f,
            "Wrath's new live suggestion failed to supersede history before 20% of the cast");
        int afterLive = pulses.Count;
        TestWrath.Next = 300; Frame(100);
        check(pulses.Count == afterLive, "a withdrawn live successor was replaced with an older conflicting guess");
        TestWrath.Next = 500; Frame(640);
        check(pulses[^1] == (9, 3) && game.Dispatches == dispatches && UIGlobals.Sounds == sounds + pulses.Count,
            "normal bar stopped halfway through a cast with a known next skill");
        // Queue from the same selected slot beats a conflicting live suggestion.
        TestWrath.Next = 400; game.QueueNext = true; game.PressSlot(10, 0); TestWrath.Next = 500;
        Frame(600); check(pulses[^1] == (7, 2), "source-attributed queue lost to a conflicting live suggestion");
        // Another slot's queue is never imported as a prediction for this source.
        am->ActionQueued = false; Frame(100);
        game.Place(3, 8, 400); game.QueueNext = true; game.PressSlot(3, 8);
        Frame(100);
        check(pulses[^1] == (9, 3) && UIGlobals.Sounds == sounds + pulses.Count, "an unrelated queue produced a false target or an SE");
        am->ActionQueued = false;
        Frame(155); // 99.75%
        int beforeEnd = pulses.Count;
        am->CastActionId = am->CastSpellId = 0;
        game.DirectUse(300); // cast completion notification, private slot already shows 500
        Frame(10);
        check(pulses.Count == beforeEnd && UIGlobals.Sounds == sounds + pulses.Count,
            "late receipt jumped back to the previous spell or played a second click");
        check(game.Errors.Count == 0, "caster scenario recorded an exception");
        Console.WriteLine($"2s cast, early={early}, branched={branched}: 5% {(predicted ? "B" : "wait")}, 15% live C, 52% C, 82% queued B, 92% live C; confirmed pulses and sounds paired");
    }

    internal static void Melee(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        uint[] baseSkills = [7504, 7512, 7516], enchanted = [7527, 7528, 7529];
        for (int selected = 1; selected <= 10; selected++)
        {
            using var clock = new SimulationClock();
            using var game = new Simulation();
            Configure(game, selected, true);
            using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
            int Bar(int i) => (selected + i) % 10 + 1;
            for (int i = 0; i < 3; i++) game.Place(Bar(i), i + 2, baseSkills[i]);
            using var plugin = load(game);
            game.Tick(); AddonActionBarBase.Pulses.Clear(); UIInputData.Down.Add(0x41);
            var pulses = AddonActionBarBase.Pulses;
            var am = ActionManager.Instance();
            for (int i = 0; i < 3; i++)
            {
                // The game has already reverted its displayed adjusted ID to the normal button.
                TestWrath.Next = enchanted[i]; clock.Advance(100); game.PressSlot(selected, 0); game.Tick();
                check(game.LastUsed == enchanted[i] && UIGlobals.Sounds == pulses.Count && pulses[^1] == (Bar(i), i + 2),
                    $"enchanted melee {i+1} missed its own ordinary button on selected HB{selected}");
                *am->Gcd = new() { IsActive = true, Elapsed = .1f, Total = 1.5f, ActionId = enchanted[i] };
                int seen = pulses.Count;
                clock.Advance(100); game.Tick();
                check(pulses.Count == seen, "just-executed melee move repeated solely because Wrath still returned it");
                if (i == 2) continue;
                TestWrath.Next = enchanted[i+1]; clock.Advance(100); game.Tick();
                check(pulses.Count == seen + 1 && pulses[^1] == (Bar(i+1), i+3) && game.Dispatches == i+1 &&
                    UIGlobals.Sounds == pulses.Count && game.RenderSlot(Bar(i+1),i+3).ActionId == baseSkills[i+1],
                    "GCD display changed execution, native icon or SE, or the old opener was reused");
            }
            am->Gcd->Elapsed = am->Gcd->Total; clock.Advance(100); game.Tick();
            check(pulses[^1] == (Bar(2),4) && game.Dispatches == 3 && UIGlobals.Sounds == pulses.Count,
                "GCD end did not allow the same real candidate to be pressed again");
        }
        Console.WriteLine("RDM 7527 / 7528 / 7529 -> registered 7504 / 7512 / 7516, GCD and pulse checked for all ten selected bars");
    }

    internal static void NativeVariant(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        Configure(game, 4, true);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        for (int b = 1; b <= 10; b++)
            for (int i = 0; i < 12; i++) game.Place(b, i, 0);
        game.Place(4,0,100); game.Place(7,8,100);
        using var plugin = load(game); game.Tick(); AddonActionBarBase.Pulses.Clear();
        // Native form exists for just a single HUD draw between periodic assignment scans.
        TestWrath.NativeRoot = 963; game.DrawPrevented(7);
        TestWrath.NativeRoot = 100; TestWrath.Next = 963;
        clock.Advance(70); game.PressSlot(4,0); game.Tick();
        check(AddonActionBarBase.Pulses.SequenceEqual([(7,8)]) && UIGlobals.Sounds == 1 &&
            game.RenderSlot(7,8).ActionId == 100, "short-lived native upgrade was missed or changed its ordinary button");
        game.JobId++; clock.Advance(100); game.Tick();
        game.PressSlot(4,0); game.Tick();
        check(AddonActionBarBase.Pulses.Count == 1 && UIGlobals.Sounds == 1,
            "ordinary form learned on a previous job produced an unrelated flash or orphan sound");
    }

    internal static void CancelAndQueue(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        Configure(game, 10, true);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        game.Place(3,1,300); game.Place(7,5,400); game.Place(5,9,500);
        using var plugin = load(game); game.Tick();
        var am = ActionManager.Instance(); var pulses = AddonActionBarBase.Pulses; pulses.Clear();
        UIInputData.Down.Add(0x41);
        TestWrath.Next = 300; game.DeferNextCast = true; game.PressSlot(10,0);
        am->CastActionId = am->CastSpellId = 300; am->CastTimeTotal = 2; am->CastTargetId = game.Target;
        game.Tick(); clock.Advance(100); am->CastTimeElapsed = .1f; game.Tick();
        check(pulses.SequenceEqual([(3,1)]) && UIGlobals.Sounds == pulses.Count, "unknown opener repeated halfway into an active cast");
        am->CastActionId = am->CastSpellId = 0; am->CastTimeTotal = 0; TestWrath.Next = 400;
        clock.Advance(90); game.Tick();
        check(pulses[^1] == (7,5) && UIGlobals.Sounds == pulses.Count && game.Dispatches == 1,
            "cast interruption replayed a click or stopped the next live suggestion");
        // A second, genuinely started cast of the same spell must get its own single sound.
        TestWrath.Next = 300; clock.Advance(120); game.PressSlot(10,0);
        am->CastActionId = am->CastSpellId = 300; am->CastTimeElapsed = 0; am->CastTimeTotal = 2;
        game.Tick();
        check(pulses[^1] == (3,1) && UIGlobals.Sounds == pulses.Count, "start and use in the same frame duplicated or suppressed the new cast");
        // Prior knowledge of the queue is invalid after retargeting even if the queued action ID is identical.
        TestWrath.Next = 400; game.QueueNext = true; game.PressSlot(10,0);
        TestWrath.Next = 500; clock.Advance(100); am->CastTimeElapsed = .1f; game.Tick();
        check(pulses[^1] == (7,5), "actual queued skill was not shown");
        am->QueuedTargetId = 0x777; clock.Advance(100); am->CastTimeElapsed = .2f; game.Tick();
        check(pulses[^1] == (5,9) && UIGlobals.Sounds == pulses.Count && game.Dispatches == 3,
            "retargeted queue was treated as the original slot's next skill");
    }

    internal static void PrivateArt(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        Configure(game, 8, true); game.Place(2, 4, 300);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        var img = game.InstallPressFrame(8,0);
        using var plugin = load(game);
        game.Tick(); game.RenderGui(); AddonActionBarBase.Pulses.Clear();
        UIInputData.Down.Add(0x41); game.Tick();
        // The native HUD draws its frame after the dispatch. Simulate that live material change.
        game.Huds[7].CurrentPulsingSlots = 1; img->AtkResNode.Visible = true;
        game.RenderGui();
        var art = ImGui.Bar.Images.Single(x => x.Texture == 900001);
        var box = ImGui.Targets["0"];
        check(art.Min == box.At && art.Max == box.At + box.Size && art.Uv0 == new Vector2(.25f,.125f) &&
            art.Uv1 == new Vector2(.4375f,.3125f) && ImGui.Front.Images.Count == 0, "wrong native atlas crop, location or layer");
        // An unavailable atlas must not switch back to a white face/circle between repeat beats.
        Dalamud.Interface.Textures.Texture.PendingIcon = 900001;
        try
        {
            var rimLevels = new List<uint>();
            for (int j = 0; j < 30; j++)
            {
                clock.Advance(16); game.Tick(); game.RenderGui();
                check(!ImGui.Bar.Faces.Any(f => (f.Color & 0xffffff) == 0xe9f6ff) &&
                    !ImGui.Bar.Glows.Any(g => Vector2.Distance(g.At, box.At + box.Size / 2) < .1f) &&
                    ImGui.Bar.Rings.Count == 0, "flash/ripple reappeared while the native material was loading");
                if (j >= 13)
                    rimLevels.Add(ImGui.Bar.Rims.Where(r => (r.Color & 0xffffff) == 0xd5e9f2)
                        .Select(r => r.Color >> 24).DefaultIfEmpty().Max());
            }
            check(rimLevels.Min() >= 45 && rimLevels.Max() >= 200 && rimLevels.Max() - rimLevels.Min() > 90,
                "held private rim lost either contact or repeat contrast");
            UIInputData.Down.Clear(); game.Tick(); clock.Advance(300); game.Tick(); game.RenderGui();
            check(!ImGui.Bar.Rims.Any(r => (r.Color & 0xffffff) == 0xd5e9f2) && ImGui.Front.Fills.Count == 0 &&
                game.Dispatches == 0 && UIGlobals.Sounds == AddonActionBarBase.Pulses.Count, "private art stayed on, leaked, sounded or executed a skill");
        }
        finally { Dalamud.Interface.Textures.Texture.PendingIcon = 0; }
    }

    internal static void NativeLayers(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        Configure(game, 5, true);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        var press = game.InstallLayeredPressFrame(5, 0, out var excluded);
        using var plugin = load(game);
        game.Tick();
        UIInputData.Down.Add(0x41); game.Tick();
        game.Huds[4].CurrentPulsingSlots = 1; game.Tick();
        press->AtkResNode.Visible = true;
        ((FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode*)excluded)->AtkResNode.Visible = true;
        game.RenderGui();
        var box = ImGui.Targets["0"];
        var art = ImGui.Bar.Images.Single(i => i.Texture == 900001);
        var lo = box.At + box.Size * new Vector2(6f/48, 9f/48);
        var hi = lo + box.Size * new Vector2(24f/48, 18f/48);
        check(Vector2.Distance(art.Min, lo) < .001f && Vector2.Distance(art.Max, hi) < .001f &&
            art.Color == 0x40ffffff && ImGui.Front.Images.Count == 0, "wrong scaled bounds, accumulated opacity or native layers");
        press->AtkResNode.Color.RGBA = 0x02ffffff;
        game.RenderGui();
        check(ImGui.Bar.Images.Any(i => i.Texture == 900001 && i.Color >> 24 <= 2) &&
            ImGui.Bar.Rims.Any(r => (r.Color & 0xffffff) == 0xd5e9f2 && r.Color >> 24 >= 160 &&
                Vector2.Distance((r.Min + r.Max)/2, box.At + box.Size/2) < .01f),
            "almost transparent native art suppressed the pressed overlay");
        // A gap in the active bit must not restart the still-visible native timeline.
        for (int n = 0; n < 15; n++) { clock.Advance(16); game.Tick(); game.RenderGui(); }
        check(AddonActionBarBase.HiddenPulses.Count == 0, "held source repeatedly restarted a visible native timeline");
    }
    
    internal static void HiddenNative(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        for (int bar = 1; bar <= 10; bar++)
        {
            using var clock = new SimulationClock();
            using var game = new Simulation();
            File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
                $$"""{"Schema":3,"Bar":{{bar}},"Pulse":false,"Turbo":false,"HideOriginal":true}""");
            game.Bind(bar, 3, 0x42);
            using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
            var frame = game.InstallPressFrame(bar, 3);
            using var plugin = load(game);
            game.Tick(); game.RenderGui();
            var icon = ImGui.Bar.Icons[3];
            AddonActionBarBase.Pulses.Clear();
            UIInputData.Down.Add(0x42); game.Tick();
            check(AddonActionBarBase.HiddenPulses.Count == 0 && !game.Huds[bar - 1].RootNode->Visible,
                $"HB{bar} drove the hidden source instead of a private visual");
            game.Huds[bar - 1].CurrentPulsingSlots = 1 << 3;
            frame->AtkResNode.Visible = true;
            clock.Advance(172); game.Tick(); game.RenderGui();
            var box = ImGui.Targets["3"];
            check(ImGui.Bar.Images.Count(i => i.Texture == 900001 && i.Min == box.At && i.Max == box.At + box.Size) == 1 &&
                ImGui.Bar.Icons[3].At == icon.At && ImGui.Front.Images.Count == 0 &&
                AddonActionBarBase.HiddenPulses.Count == 0 && AddonActionBarBase.Pulses.Count == 0 &&
                UIGlobals.Sounds == 0 && game.Dispatches == 0,
                $"HB{bar} failed to mirror the ongoing native frame independent of any ordinary pulse");
            UIInputData.Down.Clear(); game.Tick();
            game.Huds[bar - 1].CurrentPulsingSlots = 0;
            frame->AtkResNode.Visible = false;
            clock.Advance(680); game.Tick(); game.RenderGui();
            check(!ImGui.Bar.Images.Any(i => i.Texture == 900001) && AddonActionBarBase.HiddenPulses.Count == 0,
                "native source echoed its own pressed bit after release");
        }
    }
}
