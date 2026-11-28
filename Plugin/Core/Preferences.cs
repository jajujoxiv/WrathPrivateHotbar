using System.Text.Json;

namespace WrathPrivateHotbar;

internal enum NumberFont { Automatic, Axis, Miedinger, TrumpGothic }
internal enum KeyAlignment { Left, Center, Right }

internal sealed class NumberStyle
{
    public int Percent { get; set; } = 100;
    public NumberFont Font { get; set; }
    internal void Normalize()
    {
        Percent = Math.Clamp(Percent, 50, 180);
        if (!Enum.IsDefined(Font)) Font = NumberFont.Automatic;
    }
}

internal sealed class Preferences
{
    internal bool Migrated { get; private set; }
    public int Schema { get; set; } = 3;
    public int Bar { get; set; } = 1;
    public int PreferredBars
    {
        get; set;
    }
    public bool Show { get; set; } = true;
    public bool MuteSourceClick { get; set; } = true;
    public bool HideOriginal { get; set; } = true;
    public bool Locked
    {
        get; set;
    }
    public int Columns { get; set; } = 12;
    public int Size { get; set; } = 48;
    public float ColumnGap { get; set; } = 6;
    public float RowGap { get; set; } = 6;
    public int TimerTextPercent { get; set; } = 100;
    public int KeyTextPercent { get; set; } = 100;
    public KeyAlignment KeyAlign { get; set; } = KeyAlignment.Left;
    public float KeyOffsetX { get; set; }
    public float KeyOffsetY { get; set; }
    public NumberStyle ResourceText { get; set; } = new();
    public NumberStyle StockText { get; set; } = new();
    public NumberStyle ChargeText { get; set; } = new();
    public float X { get; set; } = -1;
    public float Y { get; set; } = -1;
    public bool Pulse { get; set; } = true;
    public int PulseMin { get; set; } = 137;
    public int PulseMax { get; set; } = 209;
    public bool EarlyPreview { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool Turbo
    {
        get; set;
    }
    public int RepeatMs { get; set; } = 120;
    public bool RepeatOutOfCombat
    {
        get; set;
    }
    public bool ShowCooldown { get; set; } = true;
    public void Normalize()
    {
        Bar = Math.Clamp(Bar, 1, 10);
        PreferredBars &= 1023;
        Columns = Math.Clamp(Columns, 1, 12);
        Size = Math.Clamp(Size, 24, 120);
        ColumnGap = NormalizeGap(ColumnGap);
        RowGap = NormalizeGap(RowGap);
        TimerTextPercent = Math.Clamp(TimerTextPercent, 65, 140);
        KeyTextPercent = Math.Clamp(KeyTextPercent, 65, 140);
        if (!Enum.IsDefined(KeyAlign)) KeyAlign = KeyAlignment.Left;
        KeyOffsetX = NormalizeOffset(KeyOffsetX);
        KeyOffsetY = NormalizeOffset(KeyOffsetY);
        (ResourceText ??= new()).Normalize();
        (StockText ??= new()).Normalize();
        (ChargeText ??= new()).Normalize();
        RepeatMs = Math.Clamp(RepeatMs, 60, 500);
        PulseMin = Math.Clamp(PulseMin, 60, 999);
        PulseMax = Math.Clamp(PulseMax, PulseMin + 1, 1000);
        if (!float.IsFinite(X))
            X = -1;
        if (!float.IsFinite(Y))
            Y = -1;
    }

    private static float NormalizeOffset(float value) => float.IsFinite(value)
        ? MathF.Round(Math.Clamp(value, -24f, 24f) * 10f) / 10f : 0f;

    private static float NormalizeGap(float value) => float.IsFinite(value)
        ? MathF.Round(Math.Clamp(value, 0, 24) * 10f) / 10f : 6f;

    public static Preferences Load(string path)
    {
        if (!File.Exists(path))
            return new();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var p = document.RootElement.Deserialize<Preferences>() ?? new();
        int version = document.RootElement.TryGetProperty(nameof(Schema), out var schema) && schema.TryGetInt32(out var found) ? found : 0;
        if (version < 2)
        {
            p.Show = p.HideOriginal = p.Sound = p.Pulse = true;
            if (p.PulseMin >= p.PulseMax) { p.PulseMin = 137; p.PulseMax = 209; }
        }
        if (version < 3)
        {
            if (document.RootElement.TryGetProperty("Gap", out var old) && old.ValueKind == JsonValueKind.Number && old.TryGetSingle(out float gap))
                p.ColumnGap = p.RowGap = NormalizeGap(gap);
            p.Migrated = true;
            p.Schema = 3;
        }
        p.Normalize();
        return p;
    }
    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
