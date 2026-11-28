using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class NativeHudCases
{
    internal static void Layout(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        string config = Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json");
        File.WriteAllText(config, """{"Schema":2,"Bar":10,"Size":96,"Columns":6,"X":40,"Y":35}""");
        game.Bind(10, 10, 0x84);
        game.Bind(10, 11, 0x8C);
        game.SetHint(10, 10, "F11");
        game.SetTooltipHint(10, 11, " [F12]");
        game.SetHint(10, 0, "⇧2");
        game.SetHint(1, 10, "WRONG PAGE");
        using var plugin = load(game);
        game.Tick();
        game.RenderGui();
        var first = ImGui.Targets["0"];
        var second = ImGui.Targets["1"];
        var row2 = ImGui.Targets["6"];
        var last = ImGui.Targets["5"];
        var keylock = ImGui.Targets["50"];
        check(first.Size.X == 96 && second.At.X - first.At.X - first.Size.X == 12 &&
            row2.At.Y - first.At.Y - first.Size.Y == 12, "spacing did not scale for both grid axes");
        check(keylock.At.X > last.At.X + last.Size.X && keylock.At.Y >= first.At.Y &&
            keylock.At.Y + keylock.Size.Y <= first.At.Y + first.Size.Y, "lock is not to the right of the first row");
        check(ImGui.Bar.Labels.All(x => !x.Text.Contains("疑似HB") && x.Text != "F21" && x.Text != "140" && x.Text != "WRONG PAGE"),
            "heading, other page or raw virtual key displayed");
        check(ImGui.Bar.Labels.Any(x => x.Text == "F11" && x.At.Y < ImGui.Targets["10"].At.Y) &&
            ImGui.Bar.Labels.Any(x => x.Text == "F12") && ImGui.Bar.Labels.Any(x => x.Text == "⇧2") &&
            ImGui.Bar.TextSizes.Any(x => x.Text == "F11" && x.Size > 30), "native text, tooltip fallback, scaling or modifier lost");
        check(game.IconLookups.Count >= 12 && game.IconLookups.All(x => x.HiRes), "not all occupied slots preferred high resolution");
        Texture.PendingHighRes = 800;
        game.IconLookups.Clear();
        game.RenderGui();
        check(game.IconLookups.Count == 24 && game.IconLookups[0].HiRes && !game.IconLookups[1].HiRes &&
            ImGui.Bar.Icons.Count == 12, "pending HD texture left a blank slot");
        Texture.PendingHighRes = 0;
        game.IconLookups.Clear();
        game.RenderGui();
        check(game.IconLookups.Count == 12 && game.IconLookups.All(x => x.HiRes), "SD fallback stuck after HD became available");

        void ClickLock()
        {
            ImGui.ClickSlot = 50; ImGui.Press = true; game.RenderGui();
            ImGui.Press = false; ImGui.Release = true; game.RenderGui();
            ImGui.Release = false; ImGui.ClickSlot = -1;
        }
        ClickLock();
        game.RenderGui();
        check(Preferences.Load(config).Locked && ImGui.Targets["0"] == first && ImGui.Targets["50"] == keylock,
            "locking shifted the grid or failed to persist");
        ImGui.ClickSlot = 0; ImGui.Drag = true; ImGui.GetIO().MouseDelta = new Vector2(14, 8);
        game.RenderGui();
        ImGui.Drag = false; game.RenderGui();
        check(ImGui.Targets["0"] == first, "locked bar moved during drag");
        ImGui.ClickSlot = -1;
        ClickLock();
        check(!Preferences.Load(config).Locked, "lock could not be opened");
        ImGui.ClickSlot = 0; ImGui.Drag = true; game.RenderGui();
        ImGui.Drag = false; game.RenderGui();
        check(ImGui.Targets["0"].At == first.At + new Vector2(14, 8) && Preferences.Load(config).X == 54,
            "unlocked bar did not save the dragged position");
        ImGui.ClickSlot = -1; ImGui.GetIO().MouseDelta = default;
        game.Command("config");
        ImGui.OpenHeader = "配置・連打";
        ImGui.FloatLabel = "横の枠間隔（48px基準）"; ImGui.FloatNext = 3;
        game.RenderGui();
        check(Preferences.Load(config).ColumnGap == 3 &&
            ImGui.Targets["1"].At.X - ImGui.Targets["0"].At.X - ImGui.Targets["0"].Size.X == 6,
            "custom spacing did not persist or reach the drawn grid");
        ImGui.OpenHeader = null;
        game.Place(10, 4, 0); Thread.Sleep(75); game.Tick(); game.RenderGui();
        check(!ImGui.Targets.ContainsKey("4") && ImGui.Targets["5"].At.X > ImGui.Targets["3"].At.X + 2 * first.Size.X,
            "empty slot moved adjacent shortcuts or remained clickable");
    }

    internal static void Repress(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation();
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            """{"Schema":2,"Bar":10,"PulseMin":80,"PulseMax":81}""");
        game.Bind(10, 0, 0x41); game.Bind(10, 1, 0x42);
        game.Place(10, 1, 888); // a different registered skill with no matching ordinary slot
        game.Place(6, 2, 300); game.Place(9, 4, 400);
        using var plugin = load(game);
        game.Tick();
        var pulses = AddonActionBarBase.Pulses;
        pulses.Clear();
        UIInputData.Down.Add(0x41);
        game.PressSlot(10, 0); game.Tick();
        int uses = game.Dispatches;
        TestWrath.Next = 400;
        UIInputData.Down.Clear(); game.Tick();
        Thread.Sleep(112);
        game.Tick(); // the old release grace would have illuminated the new suggestion here
        check(pulses.SequenceEqual([(6, 2)]), "release illuminated a skill that was not pressed");
        Thread.Sleep(52);
        UIInputData.Down.Add(0x41); game.Tick(); game.RenderGui();
        check(pulses.Count == 2 && pulses[^1] == (9, 4) && game.Dispatches == uses && UIGlobals.Sounds == pulses.Count &&
            ImGui.Front.Borders.Count == 0 && ImGui.Front.Fills.Count == 0 && ImGui.Bar.Rims.Any(r => (r.Color & 0xffffff) == 0xd5e9f2),
            "same-key repress before the GCD required another use, played SE, or drew a foreground square");
        UIInputData.Down.Clear(); game.Tick();
        int seen = pulses.Count;
        Thread.Sleep(290); game.Tick(); Thread.Sleep(90); game.Tick();
        check(pulses.Count == seen && UIGlobals.Sounds == pulses.Count && game.Dispatches == uses,
            "full release kept emitting or executing actions");
        UIInputData.Down.Add(0x41); game.Tick();
        check(pulses.Count == seen + 1 && pulses[^1] == (9, 4) && UIGlobals.Sounds == pulses.Count,
            "same-slot repress after release waited for the next action");
        game.PressSlot(10, 0); game.Tick();
        check(pulses[^1] == (9, 4) && UIGlobals.Sounds == pulses.Count, "new accepted skill did not supersede earlier feedback");
        UIInputData.Down.Clear(); game.Tick();
        int beforeSwitch = pulses.Count;
        UIInputData.Down.Add(0x42); Thread.Sleep(95); game.Tick();
        check(pulses.Count == beforeSwitch && UIGlobals.Sounds == pulses.Count, "different key inherited a stale target");
        UIInputData.Down.Clear(); game.Tick();
        UIInputData.Down.Add(0x41); game.Place(10, 0, 777); Thread.Sleep(95); game.Tick();
        check(pulses.Count == beforeSwitch, "reassigned slot reused an old skill's glow");
    }
}
