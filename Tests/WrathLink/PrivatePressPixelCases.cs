using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class PrivatePressPixelCases
{
    private enum Material { Transparent, Missing, Clipped, Sliver, Pending }

    internal static void Run(Func<Simulation, Plugin> load, Action<bool,string> check)
    {
        WithoutPressTextures(load, check);
        foreach (var material in Enum.GetValues<Material>())
        {
            using var clock = new SimulationClock();
            using var game = new Simulation { AtlasAlpha = material == Material.Transparent ? (byte)0 : (byte)255 };
            using var provider = new ActionReplacer();
            Service.ActionReplacer = provider;
            File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),
                """{"Schema":3,"Bar":8,"Pulse":false,"Sound":true,"Size":48,"Turbo":false}""");
            game.Bind(8, 0, 0x31);
            var sprite = material is Material.Clipped or Material.Sliver
                ? game.InstallLayeredPressFrame(8, 0, out _) : game.InstallPressFrame(8, 0);
            if (material is Material.Clipped or Material.Sliver)
                sprite->AtkResNode.X = material == Material.Clipped ? (short)48 : (short)47;
            if (material == Material.Pending) game.DelayedRead = new();
            using var plugin = load(game);
            game.Tick(); game.RenderGui();
            DesktopInput.DesktopKeys = [0x31];
            int[] rises = [0,0], changedOnFirst = [0,0];
            float[] previous = [0,0];
            Vector3[] backgrounds = [new(27,37,87),new(216,223,218)];
            for (int frame = 0; frame < 72; frame++)
            {
                clock.Advance(16);
                if (frame < 56)
                {
                    game.Huds[7].PulseActionBarSlot(0);
                    sprite->AtkResNode.Visible = material != Material.Missing;
                    game.Widget(8,0)->IconImage->AtkResNode.AddRed = 0;
                }
                else
                {
                    DesktopInput.DesktopKeys.Clear();
                    sprite->AtkResNode.Visible = false;
                }
                game.Tick(); game.RenderGui();
                var box = ImGui.Targets["0"];
                var local = ImGui.Bar.Images.Where(i => i.Texture == Simulation.PressTexture).ToArray();
                check(local.All(i => i.Min == box.At && i.Max == box.At + box.Size), "press landed on another private slot");
                if (frame >= 56)
                    check(local.Length == 0, "switching away from an unusable sprite created a glow on key-up");
                uint tint = local.Length == 0 ? 0 : local.Single().Color;
                for (int k = 0; k < backgrounds.Length; k++)
                {
                    var (energy, changed) = Composite(game.Mask, tint, backgrounds[k], (int)box.Size.X);
                    if (frame == 0) changedOnFirst[k] = changed;
                    if (frame < 56 && energy > previous[k] + 4) rises[k]++;
                    previous[k] = energy;
                }
            }
            check(changedOnFirst.Min() >= 230 && rises.Min() > 5 && rises.Max() < 28,
                $"{material}: no distinct pressed pixels on both backgrounds, first={string.Join('/',changedOnFirst)}, rises={string.Join('/',rises)}");
            check(game.AtlasReads <= 1 && UIGlobals.Sounds == 0 && game.Dispatches == 0 && game.Errors.Count == 0,
                "press rendering reread a static atlas, sounded, or dispatched another action");
            Console.WriteLine($"private {material}: first changed pixels dark/light={string.Join('/',changedOnFirst)} of 2304; " +
                $"distinct rises={string.Join('/',rises)} in 56 frames; no new glow on release");
        }
    }

    private static void WithoutPressTextures(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock();
        using var game = new Simulation { FailMask = true };
        using var provider = new ActionReplacer();
        Service.ActionReplacer = provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),
            """{"Schema":3,"Bar":10,"Pulse":false,"Size":48}""");
        game.Bind(10,0,0x31);
        using var plugin = load(game);
        game.Tick(); game.RenderGui();
        var box = ImGui.Targets["0"];
        static (Vector2 Min, Vector2 Max, uint Tint) Face(int slot)
        {
            var rect = ImGui.Targets[slot.ToString()];
            int index = ImGui.Bar.Images.FindIndex(i => i.Texture < 900000 &&
                Vector2.Distance((i.Min+i.Max)/2,rect.At+rect.Size/2) < .01f);
            var vertices = ImGui.Bar.VtxBuffer.Items;
            int first = ImGui.Bar.ImageVertices[index].First;
            return (vertices[first].Pos,vertices[first+1].Pos,vertices[first].Col);
        }
        var idle = Face(0);
        var neighbor = Face(1);
        DesktopInput.DesktopKeys = [0x31];
        int downs = 0;
        uint previous = 255;
        for (int frame = 0; frame < 65; frame++)
        {
            clock.Advance(16);
            if (frame < 56) game.Huds[9].PulseActionBarSlot(0);
            game.Tick();
            if (frame == 56) DesktopInput.DesktopKeys.Clear(); // Between input sampling and ImGui drawing.
            game.RenderGui();
            var face = Face(0);
            uint brightness = face.Tint & 255;
            if (brightness + 12 < previous) downs++;
            previous = brightness;
            check(ImGui.Bar.Images.All(i => i.Texture < 900000) && Face(1) == neighbor,
                "private face depended on a glow texture or touched the neighboring icon");
            if (frame < 56)
            {
                check(face.Min.X > idle.Min.X && face.Max.X < idle.Max.X && brightness < 255 &&
                    ImGui.Bar.Rims.Any(r => (r.Color & 0xffffff) == 0xd5e9f2 &&
                    Vector2.Distance((r.Min + r.Max)/2, box.At + box.Size/2) < .01f),
                    "source input failed to press the actual displayed icon and light its inside edge");
            }
            else check(face == idle, "released icon was still displaced or tinted");
        }
        check(downs > 5 && downs < 28 && UIGlobals.Sounds == 0 && game.Dispatches == 0,
            $"no usable press texture: {downs} downstrokes on the actual icon");
        Console.WriteLine($"private without either texture: {downs} downstrokes on the drawn skill icon; neighbor stationary; restored on release");
    }

    // Sample the actual RGBA bevel applied by the plugin at 48px and composite it
    // over light and dark icons. Count only the textured surface, omitting the
    // additional vector highlights, so they cannot make this assertion pass.
    private static (float Energy, int Pixels) Composite(byte[] pixels, uint tint, Vector3 background, int size)
    {
        if (tint == 0 || pixels.Length == 0) return default;
        int side = (int)Math.Sqrt(pixels.Length / 4), changed = 0;
        float sum = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int sx = (int)((x + .5f) * side / size);
            int sy = (int)((y + .5f) * side / size);
            int i = (sy * side + sx) * 4;
            float alpha = (tint >> 24) * pixels[i+3] / (255f * 255f);
            var result = Vector3.Lerp(background, new(pixels[i],pixels[i+1],pixels[i+2]), alpha);
            float difference = Vector3.Distance(background, result);
            if (difference > 26) changed++;
            sum += difference;
        }
        return (sum / (size * size), changed);
    }
}
