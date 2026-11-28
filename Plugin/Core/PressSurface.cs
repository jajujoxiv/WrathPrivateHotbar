using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace WrathPrivateHotbar;

// Soft inner bevel for frames where the game has no usable press sprite (e.g. a hidden HUD).
// Straight-alpha RGBA: the center stays transparent and the effect follows rounded corners.
internal sealed class PressSurface(ITextureProvider textures) : IDisposable
{
    private IDalamudTextureWrap? texture;
    private long retry;
    internal const int Side = 96;
    internal static byte[] Pixels()
    {
        var bytes = new byte[Side * Side * 4];
        for (int y = 0; y < Side; y++)
        for (int x = 0; x < Side; x++)
        {
            float u = (x + .5f) / Side, v = (y + .5f) / Side;
            var q = Vector2.Abs(new Vector2(u - .5f, v - .5f)) - new Vector2(.405f);
            float d = .085f - (Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0));
            if (d < 0) continue;
            float light = Math.Clamp(1 - d / .30f, 0, 1);
            float lip = MathF.Exp(-MathF.Pow((d - .038f) / .034f, 2));
            float shadow = Math.Clamp(1 - d / .022f, 0, 1);
            float alpha = Math.Clamp(.56f * light * light + .63f * lip + .60f * shadow, 0, .97f);
            int i = (y * Side + x) * 4;
            bytes[i] = (byte)(255 - 200 * shadow);
            bytes[i+1] = (byte)(204 + 41 * lip - 157 * shadow);
            bytes[i+2] = (byte)(112 + 98 * lip - 71 * shadow);
            bytes[i+3] = (byte)MathF.Round(255 * alpha);
        }
        return bytes;
    }

    internal void Draw(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, float contact, long now,
        int firstIconVertex, int iconVertexCount)
    {
        // Change the exact icon already on screen, even when both press textures
        // are unavailable. Leave UVs, antialias alpha, labels and cooldown geometry alone.
        var center = (min + max) * .5f;
        float side = max.X - min.X;
        var offset = new Vector2(0, side * .0125f * contact);
        uint shade = (uint)MathF.Round(255 * (1 - .22f * contact)) * 0x010101u;
        foreach (ref var vertex in dl.VtxBuffer.AsSpan().Slice(firstIconVertex, iconVertexCount))
        {
            vertex.Pos = center + (vertex.Pos - center) * (1 - .065f * contact) + offset;
            vertex.Col = (vertex.Col & 0xff000000u) | shade;
        }
        float unit = MathF.Max(1, side / 48f);
        var edge = new Vector2(unit * 1.7f);
        dl.AddRect(min + edge, max - edge, 0x00222430u | (uint)MathF.Round(208 * contact) << 24,
            MathF.Max(unit, radius - unit), ImDrawFlags.None, unit * 3.4f);
        if (texture == null && now >= retry)
        {
            retry = now + 2000;
            try { texture = textures.CreateFromRaw(RawImageSpecification.Rgba32(Side, Side), Pixels(), "WPH pressed slot"); }
            catch (Exception) { /* The vector bevel remains visible if the texture backend is unavailable. */ }
        }
        if (texture != null && contact > 0)
        {
            uint tint = 0xffffffu | (uint)MathF.Round(255 * contact) << 24;
            dl.AddImageRounded(texture.Handle, min, max, Vector2.Zero, Vector2.One, tint, radius);
        }
        int bands = texture == null ? 8 : 2;
        for (int band = bands; band > 0; band--)
        {
            var inset = new Vector2(unit * (band - .35f));
            uint rgb = band == 1 ? 0xd5e9f2u : 0x78c5ffu;
            float alpha = contact * (band == 1 ? .94f : .52f * (1f - (float)band / (bands + 1)));
            dl.AddRect(min + inset, max - inset, rgb | (uint)MathF.Round(255 * alpha) << 24,
                MathF.Max(unit, radius - inset.X * .6f), ImDrawFlags.None, unit * 1.8f);
        }
    }

    public void Dispose() { texture?.Dispose(); texture = null; }
}
