using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class SwiftcastCases
{
    internal static void Run(Func<Simulation, Plugin> load, Action<bool, string> check, Action<Simulation,int> click)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            """{"Schema":3,"Bar":7,"Turbo":true,"RepeatMs":60,"RepeatOutOfCombat":true}""");
        const int minus = 0xbd, shift = 0x10, slot = 10;
        game.Bind(7, slot, minus, 1, 1);
        game.Bind(7, 0, 0x41);
        game.Place(7, slot, 7561);
        DesktopInput.DesktopKeys = [];
        var os = DesktopInput.DesktopKeys;
        void Beat(int interval = 70) { clock.Advance(interval); game.Tick(); }
        string Trace()
        {
            ImGui.OpenHeader = "トラブル時の診断";
            try
            {
                game.RenderGui();
                return ImGui.Messages.Single(x => x.StartsWith("直近の迅速魔"));
            }
            finally { ImGui.OpenHeader = null; }
        }

        using var plugin = load(game);
        game.Tick(); game.Command("config");
        // The game sees only residual Shift, the OS sees only '-' for this entire stroke.
        UIInputData.Current->CurrentKeyModifier = 1;
        os.Add(minus); Beat(); Beat(); Beat();
        check(game.Numbered.Count == 0 && AddonActionBarBase.HiddenPulses.Count == 0 && Trace().EndsWith("未観測"), "two incomplete input sources fabricated Swiftcast");
        game.RejectNext = true; game.PressSlot(7, slot); Beat(); Beat();
        check(game.Numbered.Count == 1 && Trace().EndsWith("未観測"), "rejected hybrid dispatch armed a repeat");
        os.Add(shift); Beat(); Beat();
        check(game.Numbered.Count == 1, "adding Shift to an already held minus armed repeat");

        os.Clear(); Beat(); os.UnionWith([shift, minus]); Beat(); Beat();
        check(game.Numbered.Count == 1, "complete physical chord without an initial native slot dispatch armed repeat");
        game.PressSlot(7, slot); Beat(); Beat();
        check(game.Numbered.Count == 3 && Trace().Contains("WPH内蔵連打") && Trace().Contains("HB7-11") &&
            Trace().Contains("登録 7561 → 7561") && Trace().Contains("Shift:押 / -:押"), "legitimate second binding failed to repeat or report execution");
        os.Remove(shift); Beat(); os.Add(shift); Beat(); Beat();
        check(game.Numbered.Count == 3, "re-adding Shift after release resumed Swiftcast with '-' still held");
        game.PressSlot(7, slot); Beat(); Beat();
        check(game.Numbered.Count == 4, "a non-fresh modified stroke rearmed through another slot call");

        os.Remove(minus); Beat(); os.Add(minus);
        game.PressSlot(7, slot); Beat(); Beat();
        check(game.Numbered.Count == 6 && game.LastUsed == 7561, "new complete Shift+- stroke before the next framework sample failed");
        game.Place(7, slot, 999); Beat(); Beat();
        check(game.Numbered.Count == 6, "reassigned slot inherited the old repeat");
        os.Clear(); Beat(); game.Place(7, slot, 7561);

        // Another combo key may legitimately resolve to Swiftcast without Shift+-.
        TestWrath.Next = 7561;
        os.Add(0x41); Beat(); game.PressSlot(7, 0); Beat();
        string combo = Trace();
        check(combo.Contains("ゲーム／他プラグインの枠操作") && combo.Contains("登録 100 → 7561") &&
            combo.Contains("コンボ置換") && combo.Contains("Shift:離 / -:離"), "missing combo-source evidence");
        os.Clear(); Beat(); game.DirectUse(888);
        check(Trace() == combo, "an unrelated accepted skill overwrote the Swiftcast diagnostic");
        // A direct accepted use while a matching slot is held must not claim that slot as proof.
        os.Add(0x41); Beat(); game.DirectUse(7561);
        check(Trace().Contains("枠外・呼出元不明") && !Trace().Contains("HB7-"), "held visual heuristic leaked into direct-use diagnosis");
        os.Clear(); Beat();
        click(game, slot);
        check(Trace().Contains("疑似HBクリック") && Trace().Contains("HB7-11"), "private mouse click mislabeled");
        game.QueueNext = true; game.PressSlot(7, 0); game.FireQueued();
        check(Trace().Contains("先行入力") && Trace().Contains("HB7-1") && Trace().Contains("コンボ置換"), "queued Swiftcast lost source");
        game.PressSlot(2, 1);
        game.Place(2, 1, 7561); game.PressSlot(2, 1);
        check(Trace().Contains("HB2-2") && !Trace().Contains("コンボ置換"), "ordinary Swiftcast reported as combo replacement");
        game.RenderGui();
        string[] diagnosticLines = ["直近の発光先", "元HBで抑止", "連打の判定", "疑似HBの直近の入力",
            "直近の詠唱中の切り替え", "直近のクライアント実行", "直近の迅速魔"];
        check(ImGui.Messages.All(line => !diagnosticLines.Any(prefix => line.StartsWith(prefix))) &&
            ImGui.Messages.Count(line => line.Contains("選択HBのみ有効")) == 1,
            "closed diagnostics leaked counters or duplicated the live connection status");
        Trace();
        check(diagnosticLines.All(prefix => ImGui.Messages.Any(line => line.StartsWith(prefix))),
            "expanded diagnostics lost a counter or a recent execution");

        // Switching a HUD page or leaving the active game cannot carry a repeat into a later binding.
        os.UnionWith([shift, minus]); game.PressSlot(7, slot); Beat(); Beat();
        int beforePage = game.Numbered.Count;
        game.Huds[6].RaptureHotbarId = 8; Beat();
        game.Huds[6].RaptureHotbarId = 6; Beat(); Beat();
        check(game.Numbered.Count == beforePage, "paged-out stroke resumed after switching back to the same bar");
        os.Clear(); Beat();
        os.UnionWith([shift, minus]); game.PressSlot(7, slot); Beat();
        DesktopInput.Focused = false; Beat(); DesktopInput.Focused = true; Beat(); Beat();
        check(game.Numbered.Count == beforePage + 1, "refocusing rearmed a stroke held across focus loss");
        os.Clear(); Beat();
        game.ReAction.IsLoaded = true; Beat(900);
        os.UnionWith([shift, minus]); game.PressSlot(7, slot); Beat();
        int manual = game.Numbered.Count; Beat(); Beat();
        check(game.Numbered.Count == manual, "built-in repeat ran alongside ReAction");
    }
}
