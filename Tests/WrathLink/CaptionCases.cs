using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;
using Slot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;

internal static unsafe class CaptionCases
{
    private static void Configure(Simulation game, int size) =>
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json"),
            JsonSerializer.Serialize(new { Schema = 2, Bar = 10, Size = size, Columns = 6, X = 51, Y = 37,
                KeyAlign = (int)KeyAlignment.Center, Locked = true, PulseMin = 90, PulseMax = 91 }));

    private static void Cooldown(uint id, int seconds) => ActionManager.Timers[id] = new()
        { CooldownActive = true, PrimaryTotalTime = 120, PrimaryElapsedTime = 120 - seconds + .25f };

    private static TextRun Fill(int slot, string value, uint color)
    {
        var cell = ImGui.Targets[slot.ToString()];
        float middle = cell.At.Y + cell.Size.Y * .5f;
        return ImGui.Bar.Runs.Single(t => t.Text == value && t.Color == color &&
            t.ClipMin.Y < middle && t.ClipMax.Y > middle &&
            t.Min.X >= cell.At.X - 1 && t.Max.X <= cell.At.X + cell.Size.X + 1 &&
            t.Min.Y >= cell.At.Y - cell.Size.Y * .28f - 1 && t.Max.Y <= cell.At.Y + cell.Size.Y * 1.08f + 1);
    }

    internal static void Layout(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        int[] seconds = [6, 18, 19, 50, 76, 77, 108];
        string[] labels = ["S+2", "F11", "F12", "8", "0", "A+9", "C+S+F12"];
        foreach (int size in new[] { 24, 48, 56, 96, 120 })
        {
            using var game = new Simulation();
            Service.Configuration.ActionChanging = true;
            using var provider = new ActionReplacer();
            Service.ActionReplacer = provider;
            Configure(game, size);
            Slot.Costs[100] = (999, SlotCostType.Health);
            Slot.MemberFunctionPointers.CostLookup = false;
            Lumina.Excel.Sheets.Action.CostTypes[888] = 4;
            game.Place(10, 11, 888); Slot.Costs[888] = (0, SlotCostType.Magic); Slot.NativeText[888] = "ALL";
            for (int i = 0; i < seconds.Length; i++)
            {
                uint id = i == 0 ? 300u : (uint)(800 + i);
                game.Place(10, i, i == 0 ? 100 : id);
                game.SetHint(10, i, labels[i]); Cooldown(id, seconds[i]);
                Lumina.Excel.Sheets.Action.CostTypes[id] = 3;
                Slot.Costs[id] = (i == 2 ? 2400u : 300u, SlotCostType.Magic);
            }
            // Source has stale HP from the combo root; its own cost style must not color the MP replacement.
            var original = game.Module->GetSlotById(9, 0);
            original->CostType = (byte)SlotCostType.Health; original->CostDisplayMode = 1; original->CostValue = 999;
            game.SetCostNode(10, 0, game.TextNode("999", 12, FontType.Axis, 0xFF00AA00, 0xFF004400));
            // The selected HUD is hidden. Its local text nodes still have a valid theme.
            var themed = game.Module->GetSlotById(9, 2);
            themed->CostType = (byte)SlotCostType.Magic; themed->CostValue = 2400; themed->CostDisplayMode = 1;
            game.SetCostNode(10, 2, game.TextNode("2400", 14, FontType.Axis, 0xFFF3C8EF, 0xFF503A55));
            game.SetTimerNode(10, 2, game.TextNode("19", 28, FontType.TrumpGothic, 0xFFFAF8F5, 0xFF413C36));
            ActionManager.Charges[802] = 2;
            // A timer copied from a different action must not supply its font or color.
            game.SetTimerNode(10, 0, game.TextNode("83", 96, FontType.MiedingerMed, 0xFF00AA00, 0xFF004400));
            using var plugin = load(game);
            game.Tick(); game.RenderGui();
            check(!game.Huds[9].RootNode->Visible, "native typography was tested only with visible source HUD");
            for (int i = 0; i < seconds.Length; i++)
            {
                var cell = ImGui.Targets[i.ToString()];
                var center = cell.At + cell.Size / 2;
                uint color = i == 2 ? 0xFFFAF8F5 : 0xFFFFFFFF;
                var timer = Fill(i, seconds[i].ToString(), color);
                var wh = timer.Max - timer.Min;
                string which = $"slot {i}, {seconds[i]} seconds, {size}px";
                check(timer.Explicit && timer.Font == 3, "timer inherited the shortcut font: " + which);
                check(Vector2.Distance((timer.Min + timer.Max) / 2, center) < .85f, "timer ink not centered: " + which);
                check(wh.Y >= .54f * size && wh.Y < .74f * size && wh.X <= .85f * size, "timer escaped native proportions: " + which);
                var key = Fill(i, labels[i], 0xFFFFFFFF);
                check(MathF.Abs((key.Min.X + key.Max.X) / 2 - center.X) <= .6f && key.Min.Y < cell.At.Y &&
                    key.Max.Y < cell.At.Y + .22f * size + 1 && key.Explicit, "hotkey not centered on its own slot: " + which);
                var mp = Fill(i, i == 2 ? "2400" : "300", i == 2 ? 0xFFF3C8EF : 0xFFF5D4FA);
                check(mp.Font == 2 && mp.Min.X <= cell.At.X + Math.Max(2, .04f * size) &&
                    MathF.Abs(mp.Max.Y - (cell.At.Y + size)) <= .07f * size + 1, "MP color, face or bottom-left anchor: " + which);
                if (i == 2)
                {
                    var charge = Fill(2, "2", 0xFFFFFFFF);
                    check(charge.Min.X > mp.Max.X && charge.Max.X > center.X && charge.Max.X <= cell.At.X + size,
                        "charged spell overlapped or suppressed its MP");
                    check(ImGui.Bar.Runs.Any(t => t.Text == "2400" && t.Color == 0xFF503A55),
                        "actual native cost text outline ignored");
                }
            }
            check(Fill(11, "ALL", 0xFFF5D4FA).Explicit, "all-MP spell lost localized game cost text");
            check(ImGui.ActiveFont == 0 && game.Errors.Count == 0, "font leaked or hidden native reads failed");
        }
    }

    internal static void MissingFont(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation(); Configure(game, 24);
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        game.Place(10, 0, 867); game.SetHint(10, 0, "Ctrl+Shift+F12"); Cooldown(867, 108);
        FontAtlas.Unavailable = true;
        try
        {
            using var plugin = load(game);
            game.Tick(); game.RenderGui();
            var timer = Fill(0, "108", 0xFFFFFFFF);
            var key = Fill(0, "Ctrl+Shift+F12", 0xFFFFFFFF);
            var cell = ImGui.Targets["0"]; var center = cell.At + cell.Size / 2;
            check(timer.Explicit && timer.Font == 0 && timer.Max.X - timer.Min.X <= .85f * cell.Size.X &&
                Math.Abs((timer.Max.Y + timer.Min.Y) / 2 - center.Y) <= .6f, "missing game font made 108 overflow");
            check(key.Explicit && key.Max.X - key.Min.X <= .95f * cell.Size.X &&
                Math.Abs((key.Min.X + key.Max.X) / 2 - center.X) <= .6f, "fallback binding escaped adjacent slot");
        }
        finally { FontAtlas.Unavailable = false; }
    }

    internal static void AdjustableNumbers(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation();
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        string file = Path.Combine(game.GetPluginConfigDirectory(), "preferences-2.0.0.json");
        File.WriteAllText(file, """{"Schema":3,"Bar":6,"Locked":true,"X":84,"Y":73,"Size":56}""");
        game.Place(6, 0, 911); Slot.Costs[911] = (300, SlotCostType.Magic);
        Lumina.Excel.Sheets.Action.CostTypes[911] = 3;
        game.SetHint(6, 0, "1"); Cooldown(911,76);
        game.Place(6, 9, 912); Slot.Costs[912] = (0, SlotCostType.Default); ActionManager.Charges[912] = 2;
        game.Place(6, 10, 1000123); game.Module->GetSlotById(5,10)->CommandType = RaptureHotbarModule.HotbarSlotType.Item;
        InventoryManager.Stock[(123,true)] = 300; game.SetHint(6,10,"F11");
        using var plugin = load(game);
        game.Tick(); game.Command("config"); ImGui.OpenHeader = "配置・連打"; game.RenderGui();
        var box = ImGui.Targets["0"];
        TextRun Key() => Fill(0, "1", 0xffffffff);
        TextRun Cost() => Fill(0, "300", 0xfff5d4fa);
        TextRun Stock() => Fill(10, "×300", 0xffffffff);
        TextRun Charge() => Fill(9, "2", 0xffffffff);
        static float Height(TextRun text) => text.Max.Y - text.Min.Y;
        static bool Near(float a, float b) => Math.Abs(a-b) < .025f;
        void Slider(string label, int percent)
        {
            ImGui.SliderLabel = label + "の文字サイズ (%)"; ImGui.SliderNext = percent; game.RenderGui();
        }
        void Pick(string choice) { ImGui.Selection = choice; game.RenderGui(); }
        void Offset(string axis, float value)
        {
            ImGui.FloatLabel = $"キー文字の{axis}位置（48px基準）"; ImGui.FloatNext = value; game.RenderGui();
        }
        float mp = Height(Cost()), stock = Height(Stock()), charge = Height(Charge()), timer = Height(Fill(0,"76",0xffffffff));
        check(Key().Min.X - box.At.X < 2 && Fill(10,"F11",0xffffffff).Min.X - ImGui.Targets["10"].At.X < 2,
            "default numeric and function key labels did not share a left anchor");
        var left = Key();
        Offset("横",-4.2f);
        var shifted = ImGui.Bar.Runs.Single(r => r.Text == "1" && r.Color == 0xffffffff && Math.Abs(r.Min.X - (left.Min.X-4.2f*56/48)) < .03f);
        check(shifted.ClipMin.X < shifted.Min.X && shifted.Min.X < box.At.X && ImGui.Targets["0"] == box,
            "left-aligned key could not move past the original left anchor");
        Offset("横",0); Pick("中央上"); var centered = Key();
        check(Near((centered.Min.X+centered.Max.X)/2, box.At.X+28), "wrong center anchor");
        Offset("横",.1f); Offset("縦",-.1f);
        check(Near(Key().Min.X-centered.Min.X, .1f*56/48) && Near(Key().Min.Y-centered.Min.Y,-.1f*56/48) &&
            ImGui.Targets["0"] == box, "subpixel key offsets were rounded away or moved the hotbar");
        Pick("右上"); var right = Key();
        check(right.Max.X <= box.At.X+box.Size.X && right.Min.X > centered.Min.X, "wrong right anchor");
        Offset("横",5);
        var outside = ImGui.Bar.Runs.Single(r => r.Text == "1" && r.Color == 0xffffffff && Math.Abs(r.Min.X - (right.Min.X+4.9f*56/48)) < .03f);
        check(outside.ClipMax.X > outside.Max.X && outside.Max.X > box.At.X + box.Size.X && ImGui.Targets["0"] == box,
            "right-aligned key clipped when shifted beyond its original anchor");
        Offset("横",-4); Offset("縦",3);
        Pick("Trump Gothic##アイテム所持数"); Pick("Miedinger##チャージ数"); Pick("AXIS##消費MPなど");
        check(Stock().Font == 3 && Charge().Font == 1 && Cost().Font == 2, "font controls affected the wrong number");
        // Stock can hit its width limit at large sizes. Use a scale that fits for exact ratios.
        stock = Height(Stock()); charge = Height(Charge()); mp = Height(Cost());
        Slider("アイテム所持数", 70);
        check(Near(Height(Stock())/stock,.7f) && Near(Height(Charge()),charge) && Near(Height(Cost()),mp),
            "stock sizing affected MP or charges");
        Slider("消費MPなど", 80); Slider("チャージ数", 155);
        check(Near(Height(Cost())/mp,.8f) && Near(Height(Charge())/charge,1.55f) && Near(Height(Fill(0,"76",0xffffffff)),timer),
            $"independent sizes: MP {Height(Cost())/mp}, charge {Height(Charge())/charge}, recast {Height(Fill(0,"76",0xffffffff))}/{timer}");
        var saved = Preferences.Load(file);
        check(saved.KeyAlign == KeyAlignment.Right && saved.KeyOffsetX == -4 && saved.KeyOffsetY == 3 &&
            saved.StockText.Font == NumberFont.TrumpGothic && saved.StockText.Percent == 70 && saved.ChargeText.Percent == 155 &&
            saved.ResourceText.Percent == 80, "font choices, independent sizes or key anchor did not survive saving");
        game.Command("show"); game.Command("unlock"); game.RenderGui();
        check(ImGui.Targets["0"] == box && Preferences.Load(file).X == 84, "show/unlock silently recentered the bar");
        ImGui.ButtonLabel = "疑似ホットバーを画面中央に表示"; game.RenderGui();
        check(ImGui.ButtonLabel != null && ImGui.Targets["0"] == box, "obsolete recenter button remained active");
        ImGui.ButtonLabel = null; ImGui.OpenHeader = null;
    }

    internal static void Press(Func<Simulation, Plugin> load, Action<bool, string> check)
    {
        using var game = new Simulation(); Configure(game, 56);
        Service.Configuration.ActionChanging = true;
        using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
        game.Bind(10, 0, 0x41); game.Bind(10, 1, 0x42);
        game.Place(5, 4, 300); Cooldown(300, 108);
        using var plugin = load(game);
        game.Tick(); game.RenderGui();
        var cell = ImGui.Targets["0"]; var center = cell.At + cell.Size / 2;
        var pulses = AddonActionBarBase.Pulses; pulses.Clear();
        static bool Bright(Vector2 at) => ImGui.Bar.Rims.Any(g => Vector2.DistanceSquared((g.Min + g.Max) / 2, at) < .001f && (g.Color & 0xffffff) == 0xd5e9f2 && (g.Color >> 24) >= 20);
        check(!Bright(center), "idle slot glowed");
        var idleIcon = ImGui.Bar.Icons[0].At;
        UIInputData.Down.Add(0x41); game.RejectNext = true; game.PressSlot(10, 0); game.Tick(); game.RenderGui();
        check(Bright(center) && ImGui.Bar.Icons[0].At == idleIcon && pulses.SequenceEqual([(5, 4)]) && UIGlobals.Sounds == pulses.Count,
            $"rejected key: glow={Bright(center)}, inset={ImGui.Bar.Icons[0].At.X-idleIcon.X}, pulses={pulses.Count}, sounds={UIGlobals.Sounds}");
        var timer = Fill(0, "108", 0xFFFFFFFF);
        int lastVeil = ImGui.Bar.Veils.Where(t => Vector2.DistanceSquared(t.At, center) < .001f).Max(t => t.Order);
        check(ImGui.Bar.Rims.Any(g => (g.Color & 0xffffff) == 0xd5e9f2 && Vector2.DistanceSquared((g.Min + g.Max) / 2, center) < .001f && lastVeil < g.Order && g.Order < timer.Order),
            "cooldown covered press or glow covered the timer");
        UIInputData.Down.Clear(); game.Tick(); Thread.Sleep(215); game.RenderGui();
        check(!Bright(center) && ImGui.Bar.Icons[0].At == idleIcon, "source did not return to idle");
        int before = game.Dispatches;
        ImGui.ClickSlot = 0; ImGui.Press = true; game.RenderGui();
        check(Bright(center) && game.Dispatches == before && pulses.SequenceEqual([(5, 4)]) && UIGlobals.Sounds == pulses.Count,
            "mouse-down waited for release, dispatch or acceptance");
        ImGui.Press = false;
        Thread.Sleep(205); game.RenderGui();
        check(ImGui.Bar.Icons[0].At == idleIcon && Bright(center), "held mouse lost depressed state");
        ImGui.Release = true; game.RenderGui(); ImGui.Release = false; ImGui.ClickSlot = -1;
        game.Tick(); game.RenderGui();
        check(pulses.SequenceEqual([(5, 4), (5, 4)]) && UIGlobals.Sounds == pulses.Count && game.Dispatches == before + 1,
            "mouse release did not produce exactly one accepted use, normal pulse and sound");
        UIInputData.Down.Add(0x42); Texture.PendingIcon = 800;
        try
        {
            game.Tick(); game.RenderGui();
            var second = ImGui.Targets["1"];
            check(Bright(second.At + second.Size / 2) && ImGui.Front.Fills.Count == 0 && ImGui.Front.Borders.Count == 0 &&
                pulses.SequenceEqual([(5, 4), (5, 4)]) && UIGlobals.Sounds == pulses.Count, "loading icon blocked a new physical press or sent false feedback");
        }
        finally { Texture.PendingIcon = 0; UIInputData.Down.Clear(); }
    }
}
