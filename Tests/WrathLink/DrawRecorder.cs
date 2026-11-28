using System.Numerics;
using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

public readonly record struct ImTextureID(uint Value)
{
    public static implicit operator ImTextureID(uint value) => new(value);
}

[Flags]
public enum ImGuiWindowFlags
{
    NoDecoration = 1, NoSavedSettings = 2, NoBackground = 4, NoMove = 8, NoFocusOnAppearing = 16, NoNav = 32
}
public enum ImGuiCond
{
    Always, FirstUseEver
}
public enum ImDrawFlags
{
    None
}
public enum ImGuiMouseButton
{
    Left, Right
}
public sealed class Viewport
{
    public Vector2 Pos, WorkPos = new(40, 20), WorkSize = new(1280, 720); public Vector2 Size => WorkSize;
}
public sealed class Io
{
    public bool WantTextInput; public Vector2 MouseDelta;
}
public sealed class Style
{
    public Vector2 WindowPadding = new(8);
}
public struct ImFontGlyph { public float X0, X1, Y0, Y1, AdvanceX; }
public unsafe sealed class ImFontPtr
{
    // Unequal bearings, line heights and widths ensure layout tests measure visible ink.
    private readonly ImFontGlyph* glyphs;
    public int Face { get; }
    public float FontSize { get; }
    internal ImFontPtr(int face, float size, float width, float top, float height)
    {
        Face = face; FontSize = size;
        glyphs = (ImFontGlyph*)NativeMemory.AllocZeroed(128, (nuint)sizeof(ImFontGlyph));
        for (int i = 0; i < 128; i++)
        {
            float advance = size * (i == '1' ? width * .85f : width);
            glyphs[i] = new() { X0 = size * .035f, X1 = advance - size * .03f,
                Y0 = top * size, Y1 = (top + height) * size, AdvanceX = advance };
        }
    }
    public ImFontGlyph* FindGlyph(ushort c) => glyphs + (c < 128 ? c : 127);
    public (Vector2 Min, Vector2 Max) Measure(string text, float pixels)
    {
        float x = 0;
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (char c in text)
        {
            var g = FindGlyph(c);
            min = Vector2.Min(min, new(x + g->X0, g->Y0)); max = Vector2.Max(max, new(x + g->X1, g->Y1));
            x += g->AdvanceX;
        }
        return (min * (pixels / FontSize), max * (pixels / FontSize));
    }
}
public readonly record struct TextRun(string Text, Vector2 Min, Vector2 Max, uint Color, float Size, int Font, bool Explicit, int Order, Vector2 ClipMin, Vector2 ClipMax);
public struct DrawVertex { public Vector2 Pos; public uint Col; }
public sealed class Vertices
{
    public readonly List<DrawVertex> Items = [];
    public int Size => Items.Count;
    public Span<DrawVertex> AsSpan() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Items);
}
public sealed class ImDrawListPtr
{
    public List<(string Text, Vector2 At)> Labels = []; public List<(uint Icon, Vector2 At)> Icons = [];
    public List<(Vector2 Min, Vector2 Max)> Fills = [], Borders = [];
    public void Reset()
    {
        Labels.Clear();
        Icons.Clear();
        Fills.Clear(); Faces.Clear(); Images.Clear(); ImageVertices.Clear();
        Borders.Clear();
        TextSizes.Clear();
        Circles.Clear(); textRuns.Clear(); VtxBuffer.Items.Clear(); Glows.Clear(); Veils.Clear(); Rims.Clear(); Rings.Clear(); order = 0;
        if (clips.Count != 0) throw new InvalidOperationException("unbalanced clip stack");
    }
    public List<(Vector2 At, float Radius)> Circles = [];
    public List<(string Text, float Size)> TextSizes = [];
    private int order;
    private readonly Stack<(Vector2 Min, Vector2 Max)> clips = [];
    private readonly List<(TextRun Run, int Vertex)> textRuns = [];
    public readonly Vertices VtxBuffer = new();
    public IEnumerable<TextRun> Runs => textRuns.Select(t => t.Run with { Min = VtxBuffer.Items[t.Vertex].Pos, Max = VtxBuffer.Items[t.Vertex + 1].Pos });
    public readonly List<(Vector2 At, float Radius, int Alpha, int Order)> Glows = [];
    public readonly List<(Vector2 At, int Order)> Veils = [];
    public void PushClipRect(Vector2 min, Vector2 max, bool intersect) => clips.Push((min, max));
    public void PopClipRect() => clips.Pop();
    public readonly List<(Vector2 Min, Vector2 Max, uint Color, float Thickness, float Rounding, int Order)> Rims = [];
    public readonly List<(Vector2 Center, float Radius, uint Color, int Order)> Rings = [];
    public void AddCircle(Vector2 center, float radius, uint color, int segments, float width) => Rings.Add((center, radius, color, ++order));
    public void PathLineTo(Vector2 p) { }
    public void PathArcTo(Vector2 center, float radius, float lo, float hi, int segments) { }
    public void AddTriangleFilled(Vector2 a, Vector2 b, Vector2 c, uint color) => Veils.Add((a, ++order));
    public void PathStroke(uint color, ImDrawFlags flags, float thickness) { }
    public void AddCircleFilled(Vector2 center, float r, uint color, int segments)
    {
        Circles.Add((center, r)); Glows.Add((center, r, (int)(color >> 24), ++order));
    }
    private void Record(ImFontPtr font, float size, Vector2 at, uint color, string text, bool bounded)
    {
        var ink = font.Measure(text, size);
        var clip = clips.Count == 0 ? (Min: new Vector2(float.MinValue), Max: new Vector2(float.MaxValue)) : clips.Peek();
        textRuns.Add((new(text, at + ink.Min, at + ink.Max, color, size, font.Face, bounded, ++order, clip.Min, clip.Max), VtxBuffer.Size));
        VtxBuffer.Items.Add(new() { Pos = at + ink.Min });
        VtxBuffer.Items.Add(new() { Pos = at + ink.Max });
        TextSizes.Add((text, size)); Labels.Add((text, at));
    }
    public void AddText(ImFontPtr font, float size, Vector2 at, uint color, string text) => Record(font, size, at, color, text, true);
    public void AddText(Vector2 at, uint color, string text) => Record(ImGui.GetFont(), ImGui.GetFontSize(), at, color, text, false);
    public readonly List<(uint Texture, Vector2 Min, Vector2 Max, Vector2 Uv0, Vector2 Uv1, uint Color)> Images = [];
    public readonly List<(uint Texture, int First)> ImageVertices = [];
    public readonly List<(Vector2 Min, Vector2 Max, uint Color, float Radius)> Faces = [];
    public void AddImageRounded(uint texture, Vector2 a, Vector2 b, Vector2 u, Vector2 v, uint c, float r)
    {
        if (texture < 900000) Icons.Add((texture, a)); Images.Add((texture, a, b, u, v, c));
        ImageVertices.Add((texture,VtxBuffer.Size));
        VtxBuffer.Items.Add(new() { Pos = a, Col = c });
        VtxBuffer.Items.Add(new() { Pos = b, Col = c });
    }
    public void AddRectFilled(Vector2 a, Vector2 b, uint c, float f)
    {
        Fills.Add((a,b)); Faces.Add((a,b,c,f));
    }
    public void AddRect(Vector2 a, Vector2 b, uint c, float f, ImDrawFlags flags, float width)
    {
        Borders.Add((a,b)); Rims.Add((a,b,c,width,f,++order));
    }
}
public static class ImGui
{
    public static readonly ImDrawListPtr Bar = new(), Front = new();
    public static readonly Viewport View = new();
    private static readonly Io Io = new();
    private static readonly Style Style = new();
    public static string? ToggleLabel, ButtonLabel, Selection;
    public static int ClickSlot = -1;
    public static bool Drag;
    public static readonly Dictionary<string, (Vector2 At, Vector2 Size)> Targets = [];
    private static int item = -1;
    public static bool Press, Release, BarOpened;
    public static Vector2 WindowPosition, WindowSize;
    private static Vector2 next, cursor;
    public static void Frame()
    {
        Messages.Clear();
        Bar.Reset();
        Front.Reset();
        BarOpened = false;
        Targets.Clear();
        item = -1;
    }
    public static bool Begin(string name, ImGuiWindowFlags flags)
    {
        BarOpened = true;
        WindowPosition = next;
        cursor = next + Style.WindowPadding;
        return true;
    }
    public static bool Begin(string name, ref bool open) => true;
    public static void End()
    {
    }
    public static void SetNextWindowPos(Vector2 p, ImGuiCond c) => next = p;
    public static void SetNextWindowSize(Vector2 s, ImGuiCond c) => WindowSize = s;
    public static void SetNextWindowFocus()
    {
    }
    public static void SetCursorScreenPos(Vector2 p) => cursor = p;
    public static Vector2 GetCursorScreenPos() => cursor;
    public static ImDrawListPtr GetWindowDrawList() => Bar;
    public static ImDrawListPtr GetForegroundDrawList() => Front;
    public static Viewport GetMainViewport() => View;
    public static Io GetIO() => Io; public static Style GetStyle() => Style;
    public static float GetFrameHeight() => 20;
    internal static int ActiveFont;
    private static readonly ImFontPtr[] Fonts = [new(0, 16, .52f, .16f, .70f), new(1, 48, .86f, .23f, .68f),
        new(2, 48, .49f, .15f, .70f), new(3, 45, .35f, .11f, .82f)];
    public static ImFontPtr GetFont() => Fonts[ActiveFont];
    public static float GetFontSize() => GetFont().FontSize;
    public static void SetTooltip(string value) {}
    public static Vector2 CalcTextSize(string s)
    {
        var font = GetFont();
        return new(font.Measure(s, font.FontSize).Max.X, font.FontSize);
    }
    public static void PushID(int id) => item = id;
    public static void PopID() => item = -1;
    public static void InvisibleButton(string label, Vector2 size)
    {
        if (label == "##WPHLock") item = 50;
        Targets[item.ToString()] = (cursor, size);
    }
    public static bool IsItemClicked(ImGuiMouseButton b) => Press && item >= 0 && item == ClickSlot;
    public static bool IsItemDeactivated() => Release && item >= 0 && item == ClickSlot;
    public static bool IsItemActive() => Drag && item >= 0 && item == ClickSlot;
    public static bool IsItemHovered() => item >= 0 && item == ClickSlot;
    public static bool IsMouseClicked(ImGuiMouseButton b) => false;
    public static bool IsMouseDragging(ImGuiMouseButton b) => Drag && b == ImGuiMouseButton.Left;
    public static bool IsMouseDown(ImGuiMouseButton b) => Drag && b == ImGuiMouseButton.Left;
    public static bool Checkbox(string name, ref bool value)
    {
        if (name != ToggleLabel)
            return false;
        ToggleLabel = null;
        value = !value;
        return true;
    }
    public static bool Button(string name)
    {
        if (name != ButtonLabel)
            return false;
        ButtonLabel = null;
        return true;
    }
    public static bool BeginCombo(string name, string selected) => Selection != null;
    public static bool Selectable(string option, bool selected)
    {
        if (option != Selection)
            return false;
        Selection = null;
        return true;
    }
    public static void EndCombo()
    {
    }
    public static void BeginDisabled(bool b)
    {
    }
    public static void EndDisabled()
    {
    }
    public static string? FloatLabel;
    public static float FloatNext; public static bool FloatIncrement;
    public static bool InputFloat(string label, ref float value, float step, float fast, string format)
    {
        if (FloatLabel != label) return false;
        FloatLabel = null;
        value = FloatIncrement ? value + step : FloatNext;
        FloatIncrement = false;
        return true;
    }
    public static string? SliderLabel; public static int SliderNext;
    public static bool SliderInt(string s, ref int value, int lo, int hi)
    {
        if (s != SliderLabel) return false;
        SliderLabel = null; value = Math.Clamp(SliderNext, lo, hi); return true;
    }
    public static string? OpenHeader;
    public static bool CollapsingHeader(string name) => name == OpenHeader;
    public static void Image(uint tex, Vector2 size)
    {
    }
    public static void Text(string text)
    {
    }
    public static readonly List<string> Messages = [];
    public static void TextWrapped(string text) => Messages.Add(text);
    public static void SameLine()
    {
    }
    public static void Separator()
    {
    }
}
