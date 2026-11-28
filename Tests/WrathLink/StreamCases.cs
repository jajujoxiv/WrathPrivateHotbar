using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class StreamCases
{
    internal static void AudibleFrames(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation { AutoDraw = false };
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            """{"Schema":3,"Bar":10,"Sound":true,"PulseMin":80,"PulseMax":81}""");
        game.Place(4, 2, 300); game.Bind(10, 0, 0x41);
        using var plugin = load(game); game.Tick();
        var hud = game.Huds + 3;
        var owner = hud->ActionBarSlotVector.AsSpan()[2].ComponentDragDrop->OwnerNode;
        var sounds = () => UIGlobals.Sounds;
        void Press(int ms = 220)
        {
            clock.Advance(ms);
            game.PressSlot(10, 0);
            game.Tick();
        }
        AddonActionBarBase.AnimateRequests = false;
        Press(); game.DrawNative(4); game.DrawNative(4);
        check(sounds() == 0, "requested pulse without a native animation was audible");
        AddonActionBarBase.AnimateRequests = true;
        AddonActionBarBase.PulseDurationMs = 300;
        Press(); game.DrawNative(5);
        check(sounds() == 0, "a different HUD acknowledged someone else's click");
        game.DrawNative(4); game.DrawNative(4);
        check(sounds() == 1, "same native pulse drew twice and did not produce exactly one click");
        Press(90); game.DrawNative(4);
        check(sounds() == 2, "a distinct observed press inside the existing native animation lacked its own drawn onset and click");
        Press(360); owner->AtkResNode.Visible = false; game.DrawNative(4);
        check(sounds() == 2, "invisible button made a sound");
        owner->AtkResNode.Visible = true;
        Press(360); game.DrawNative(4, true); game.DrawNative(4);
        check(sounds() == 2, "prevented draw retained a pending orphan click");
        Press(360); hud->Alpha = 0; game.DrawNative(4); hud->Alpha = 255;
        Press(360); hud->RaptureHotbarId = 6; game.DrawNative(4); hud->RaptureHotbarId = 3;
        Press(360); game.Place(4, 2, 777); game.DrawNative(4); game.Place(4, 2, 300);
        Press(360); owner->AtkResNode.Left = 2300; game.DrawNative(4); owner->AtkResNode.Left = 140;
        check(sounds() == 2, "fade, page change, reassignment or offscreen node produced a click");
        Press(360); game.DrawNative(4); check(sounds() == 3, "new, visible native pulse failed to click");
        // A natural pulse already presented before WPH matched this slot is the same visible press.
        clock.Advance(400); game.Tick(); game.DrawNative(4);
        hud->CurrentPulsingSlots |= 1 << 2; game.DrawNative(4);
        int requests = AddonActionBarBase.Pulses.Count;
        game.PressSlot(10,0); game.Tick(); game.DrawNative(4);
        check(sounds() == 3 && AddonActionBarBase.Pulses.Count == requests, "already presented manual press gained another click");
        hud->CurrentPulsingSlots = 0; game.DrawNative(4);
        game.Command("config");
        ImGui.ToggleLabel = "対応する通常HBの枠を発光させる"; game.RenderGui();
        Press(); game.DrawNative(4); check(sounds() == 3, "accepted use played a sound with visuals off");
        UIInputData.Down.Add(0x41); game.Tick(); game.RenderGui();
        var box = ImGui.Targets["0"];
        check(ImGui.Bar.Rims.Count(r => (r.Color >> 24) > 0 && r.Rounding > 0 &&
            System.Numerics.Vector2.Distance((r.Min + r.Max)/2, box.At + box.Size/2) < .1f) >= 2 &&
            AddonActionBarBase.HiddenPulses.Count == 0 && sounds() == 3,
            "private press lacked its own visible layers or sent a hidden-native pulse/sound");
        Console.WriteLine("No-draw / rejected native animation / hidden / duplicate / offscreen: zero extra sounds; three distinct presented presses: three sounds");
    }

    internal static void ConsumedCast(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            """{"Schema":3,"Bar":8,"Sound":true,"PulseMin":80,"PulseMax":81,"EarlyPreview":true}""");
        game.Place(4, 0, 200); game.Place(6, 1, 300); game.Place(7, 2, 400); game.Place(9, 3, 500);
        game.Bind(8, 0, 0x41);
        game.ReAction.IsLoaded = true; game.Target = 0x12345;
        using var plugin = load(game); game.Tick();
        void Use(uint id) { clock.Advance(140); TestWrath.Next = id; game.PressSlot(8, 0); game.Tick(); }
        for (int n = 0; n < 3; n++) { Use(200); Use(300); Use(400); }
        Use(200);
        var am = ActionManager.Instance(); var pulses = AddonActionBarBase.Pulses;
        int sounds = UIGlobals.Sounds;
        pulses.Clear();
        DesktopInput.DesktopKeys = [0x41]; // ReAction consumed game key, Windows still sees the physical hold
        TestWrath.Next = 300; clock.Advance(150);
        game.DeferNextCast = true; game.PressSlot(8, 0);
        am->CastActionId = am->CastSpellId = 300; am->CastTimeTotal = 2; am->CastTargetId = game.Target;
        game.Tick();
        int realUses = game.Dispatches;
        void Frame(int ms) { clock.Advance(ms); am->CastTimeElapsed += ms / 1000f; game.Tick(); }
        Frame(100);
        check(pulses.SequenceEqual([(6,1),(7,2)]), "consumed selected input lost the learned successor at 5% of the cast");
        TestWrath.Next = 500; Frame(100);
        check(pulses[^1] == (9,3), "live successor did not override the 5% estimate at 10%");
        TestWrath.Next = 400; game.QueueNext = true; game.PressSlot(8,0); TestWrath.Next = 500;
        Frame(100);
        check(pulses[^1] == (7,2), "queue did not override a live candidate at 15%");
        Frame(650);
        check(pulses[^1] == (7,2) && pulses.Count == 5 && UIGlobals.Sounds - sounds == 5 && game.Dispatches == realUses + 1,
            "keyless preview stopped halfway or emitted a mismatched SE/dispatch");
        Console.WriteLine($"ReAction consumed game input, physical key held: 5% learned → 10% live → 15% queued; {pulses.Count} ordinary pulses / {UIGlobals.Sounds - sounds} SE");
    }
}
