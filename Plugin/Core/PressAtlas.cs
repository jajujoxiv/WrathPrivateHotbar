using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace WrathPrivateHotbar;

// Inspect only a small crop of the selected HUD's press atlas. While a readback
// is outstanding the caller draws the local bevel. The Draw callback never waits.
internal sealed class PressAtlas(ITextureReadbackProvider readback) : IDisposable
{
    private readonly record struct Crop(string Path, ImTextureID Handle, Vector2 Uv0, Vector2 Uv1);
    private readonly Dictionary<Crop, Task<Vector3>> cache = [];
    private readonly CancellationTokenSource lifetime = new();
    private const int Side = 32;

    internal float Coverage(string path, IDalamudTextureWrap wrap, Vector2 uv0, Vector2 uv1, uint tint)
    {
        var key = new Crop(path, wrap.Handle, uv0, uv1);
        if (!cache.TryGetValue(key, out var task))
        {
            if (cache.Values.Count(t => !t.IsCompleted) >= 4) return 0;
            if (cache.Count >= 64)
            {
                var completed = cache.First(p => p.Value.IsCompleted);
                cache.Remove(completed.Key);
            }
            task = Read(wrap, uv0, uv1, lifetime.Token);
            cache.Add(key, task);
        }
        if (!task.IsCompletedSuccessfully) return 0;
        var ink = task.Result * new Vector3(tint & 255, (tint >> 8) & 255, (tint >> 16) & 255) / 255f;
        return MathF.Max(ink.X, MathF.Max(ink.Y, ink.Z));
    }

    private async Task<Vector3> Read(IDalamudTextureWrap wrap, Vector2 uv0, Vector2 uv1, CancellationToken token)
    {
        try
        {
            using var lease = wrap.CreateWrapSharingLowLevelResource();
            var args = new TextureModificationArgs
            {
                Uv0 = uv0, Uv1 = uv1, NewWidth = Side, NewHeight = Side,
                DxgiFormat = RawImageSpecification.Rgba32(Side, Side).DxgiFormat
            };
            var (spec, bytes) = await readback.GetRawImageAsync(lease, args, true, token).ConfigureAwait(false);
            if (spec.Width != Side || spec.Height != Side || spec.DxgiFormat != args.DxgiFormat ||
                spec.Pitch < Side * 4 || bytes.Length < (long)(Side - 1) * spec.Pitch + Side * 4) return Vector3.Zero;
            var sum = Vector3.Zero;
            for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int p = y * spec.Pitch + x * 4;
                sum += new Vector3(bytes[p], bytes[p+1], bytes[p+2]) * (bytes[p+3] / 255f);
            }
            return sum / (Side * Side * 255f);
        }
        catch (Exception) { return Vector3.Zero; } // Use the local bevel if the backend rejects the crop.
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        cache.Clear();
    }
}
