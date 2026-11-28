using System.Buffers.Text;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace WrathPrivateHotbar;

public sealed unsafe partial class Plugin
{
    private enum Caption { Key, Timer, Cost, Count }
    private readonly record struct HudFont(float EmRatio, FontType Family, uint Ink, uint Edge);
    private readonly record struct HudTypography(HudFont Key, HudFont Timer, HudFont Cost, HudFont Quantity);
    private const uint White = 0xFFFFFFFF, Dark = 0xF52B211F;

    private IFontHandle? LoadFont(GameFontFamilyAndSize font)
    {
        try { return pi.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(font)); }
        catch (Exception ex) { log.Debug(ex, $"Hotbar font {font} unavailable"); return null; }
    }

    private static HudFont StyleOf(AtkTextNode* text, float unit)
    {
        if (text == null || text->FontSize == 0) return default;
        return new(text->FontSize / unit, text->FontType, text->TextColor.RGBA, text->EdgeColor.RGBA);
    }

    private static bool ShowsNumber(AtkTextNode* text, uint expected, bool allowAdjacent = false, bool quantity = false)
    {
        if (text == null || !text->IsVisible()) return false;
        var bytes = text->NodeText.AsSpan();
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0) bytes = bytes[..nul];
        bytes = bytes.Trim((byte)' ');
        if (quantity)
        {
            if (bytes.StartsWith("×"u8)) bytes = bytes[2..];
            else if (bytes.StartsWith("x"u8)) bytes = bytes[1..];
        }
        return Utf8Parser.TryParse(bytes, out uint value, out int used) && used == bytes.Length &&
            (value == expected || allowAdjacent && Math.Abs((long)value - expected) == 1);
    }

    // Walk only this slot's cooldown container. The root of the selected HUD can be hidden.
    private static AtkTextNode* TimerNode(AtkResNode* node, uint seconds, int depth, ref int budget)
    {
        if (node == null || budget-- <= 0) return null;
        if (node->Type == NodeType.Text)
        {
            var text = node->GetAsAtkTextNode();
            return ShowsNumber(text, seconds, true) ? text : null;
        }
        if (depth >= 3) return null;
        for (var child = node->ChildNode; child != null && budget > 0; child = child->PrevSiblingNode)
        {
            var text = TimerNode(child, seconds, depth + 1, ref budget);
            if (text != null) return text;
        }
        return null;
    }

    private static AtkTextNode* CostIn(AtkUldManager* uld, AtkTextNode* hint, AtkTextNode* timer, uint cost)
    {
        if (uld == null || uld->NodeList == null) return null;
        int count = Math.Min((int)uld->NodeListCount, 64);
        for (int i = 0; i < count; i++)
        {
            var n = uld->NodeList[i];
            if (n == null || n->Type != NodeType.Text) continue;
            var text = n->GetAsAtkTextNode();
            if (text != hint && text != timer && ShowsNumber(text, cost)) return text;
        }
        return null;
    }

    private void ReadTypography(AddonActionBarBase* hud, RaptureHotbarModule* module)
    {
        Array.Clear(typography);
        if (hud == null) return;
        var slots = hud->ActionBarSlotVector.AsSpan();
        int limit = Math.Min(view.Length, Math.Min((int)hud->SlotCount, slots.Length));
        for (int i = 0; i < limit; i++)
        {
            ref var field = ref slots[i];
            var cell = view[i];
            if (cell.Stored == 0) continue;
            int width = field.IconFrame == null ? 0 : field.IconFrame->Width;
            float unit = width is >= 24 and <= 128 ? width : 48f;
            var hint = field.ControlHintTextNode;
            AtkTextNode* timer = null;
            if (cell.Remaining > 0 && cell.Total > 3)
            {
                int budget = 32;
                timer = TimerNode(field.RecastOverlayContainer, (uint)MathF.Ceiling(cell.Remaining), 0, ref budget);
            }
            var drag = field.ComponentDragDrop;
            var widget = drag == null ? null : drag->AtkComponentIcon;
            if (widget == null && field.Icon != null) widget = field.Icon->GetAsAtkComponentIcon();
            AtkTextNode* cost = null;
            var source = module->GetSlotById((uint)(p.Bar - 1), (uint)i);
            if (cell.Cost > 0 && source != null && source->CostDisplayMode == 1 &&
                source->CostType == (byte)cell.Resource && source->CostValue == cell.Cost)
            {
                if (widget != null && widget->QuantityText != hint && widget->QuantityText != timer &&
                    ShowsNumber(widget->QuantityText, cell.Cost))
                    cost = widget->QuantityText;
                if (cost == null && widget != null) cost = CostIn(&widget->UldManager, hint, timer, cell.Cost);
                if (cost == null && drag != null) cost = CostIn(&drag->UldManager, hint, timer, cell.Cost);
            }
            int count = cell.Quantity >= 0 ? cell.Quantity : cell.Charges;
            var quantity = widget == null ? null : widget->QuantityText;
            if (quantity == cost || count < 0 || !ShowsNumber(quantity, (uint)count, quantity: true)) quantity = null;
            typography[i] = new(StyleOf(hint, unit), StyleOf(timer, unit), StyleOf(cost, unit), StyleOf(quantity, unit));
        }
    }

    private static uint ResourceColor(SlotCostType resource) => resource switch
    {
        SlotCostType.Magic => 0xFFF5D4FA, SlotCostType.Health => 0xFFA0E79B,
        SlotCostType.Tactical => 0xFF80B8FB, SlotCostType.Crafting => 0xFFCE9FF8,
        SlotCostType.Gathering => 0xFF8BE1FD, SlotCostType.JobGauge => 0xFFFFC99B,
        SlotCostType.Ceruleum => 0xFF57EAFF, _ => White
    };

    private void DrawSlotText(ImDrawListPtr dl, SlotView cell, int i, Vector2 min, Vector2 max, int size, float gap)
    {
        var native = typography[i];
        if (cell.Total > 3 && cell.Remaining > 0)
            CaptionText(dl, MathF.Ceiling(cell.Remaining).ToString(CultureInfo.InvariantCulture), Caption.Timer,
                native.Timer, White, min, max, size, gap);
        bool count = cell.Quantity >= 0 || cell.Charges >= 0;
        bool showCost = cell.Cost > 0 || !string.IsNullOrWhiteSpace(cell.CostText);
        float costWidth = 0;
        if (showCost)
            costWidth = CaptionText(dl, string.IsNullOrWhiteSpace(cell.CostText) ? cell.Cost.ToString(CultureInfo.InvariantCulture) : cell.CostText, Caption.Cost, native.Cost,
                ResourceColor(cell.Resource), min, max, size, gap, widthLimit: count ? .55f : .90f, format: p.ResourceText);
        if (count)
            CaptionText(dl, cell.Quantity >= 0 ? $"×{cell.Quantity}" : cell.Charges.ToString(CultureInfo.InvariantCulture),
                Caption.Count, native.Quantity, White, min, max, size, gap, widthLimit: .90f - costWidth / size,
                format: cell.Quantity >= 0 ? p.StockText : p.ChargeText);
    }

    private void DrawKeyText(ImDrawListPtr dl, int i, Vector2 min, Vector2 max, int size, float gap, float press)
    {
        string label = nativeLabels[i] ?? "";
        if (label.Length == 0) label = keys[2 * i].Key != 0 ? keys[2 * i].Label : keys[2 * i + 1].Label;
        if (label.Length != 0) CaptionText(dl, label, Caption.Key, typography[i].Key, White, min, max, size, gap, keyPress: press);
    }

    private IFontHandle? Face(Caption role, HudFont native)
    {
        if (native.EmRatio > 0)
        {
            switch (native.Family)
            {
                case FontType.Axis: return valueFont;
                case FontType.TrumpGothic: return timerFont;
                case FontType.MiedingerMed:
                case FontType.Miedinger: return labelFont;
            }
        }
        return role switch { Caption.Key => labelFont, Caption.Timer => timerFont, _ => valueFont };
    }

    // ImGui's line height includes ascent/descent and horizontal advance includes side bearings.
    // Use the same glyph quads that AddText emits to center the visible digits at any UI scale.
    private static bool InkBounds(ImFontPtr font, string text, out Vector2 lo, out Vector2 hi)
    {
        lo = new(float.MaxValue); hi = new(float.MinValue);
        float pen = 0;
        foreach (char ch in text)
        {
            if (char.IsSurrogate(ch)) return false;
            var g = font.FindGlyph(ch);
            if (g == null) return false;
            if (g->X1 > g->X0 && g->Y1 > g->Y0)
            {
                lo = Vector2.Min(lo, new(pen + g->X0, g->Y0));
                hi = Vector2.Max(hi, new(pen + g->X1, g->Y1));
            }
            pen += g->AdvanceX;
        }
        return hi.X > lo.X && hi.Y > lo.Y;
    }

    private float CaptionText(ImDrawListPtr dl, string text, Caption role, HudFont native, uint fallback,
        Vector2 min, Vector2 max, float size, float gap, float widthLimit = .90f, float keyPress = 0, NumberStyle? format = null)
    {
        var overrideFace = format?.Font switch
        {
            NumberFont.Axis => valueFont,
            NumberFont.Miedinger => labelFont,
            NumberFont.TrumpGothic => timerFont,
            _ => null
        };
        using var face = (overrideFace ?? Face(role, native))?.Push();
        var font = ImGui.GetFont();
        float em = font.FontSize;
        if (em <= 0) return 0;
        if (!InkBounds(font, text, out var lo, out var hi))
        {
            lo = Vector2.Zero;
            hi = ImGui.CalcTextSize(text) * (em / MathF.Max(1f, ImGui.GetFontSize()));
        }
        var ink = hi - lo;
        if (ink.X <= 0 || ink.Y <= 0) return 0;
        float height = role switch { Caption.Timer => .44f, Caption.Key => .235f, _ => .25f };
        float targetHeight = size * height;
        if (native.EmRatio > 0)
            targetHeight = Math.Clamp(size * native.EmRatio * ink.Y / em, targetHeight * .85f, targetHeight * 1.10f);
        // The game atlas includes transparent padding in glyph quads. The 202 screenshot
        // shows 16px vs 24px for "97", and 7px vs 11px for the key labels at the same HUD scale.
        if (role == Caption.Timer)
            targetHeight = MathF.Min(size * .94f, targetHeight * 1.50f * p.TimerTextPercent / 100);
        else if (role == Caption.Key)
            targetHeight = MathF.Min(size * .57f, targetHeight * 1.55f * p.KeyTextPercent / 100);
        else if (format != null) targetHeight *= format.Percent / 100f;
        float width = role switch
        {
            Caption.Timer => .84f, Caption.Key => .94f,
            _ => widthLimit
        };
        float scale = MathF.Min(targetHeight / ink.Y, (width * size) / ink.X);
        var drawn = ink * scale;
        float margin = MathF.Max(1, size * .028f);
        float x = role switch { Caption.Cost => min.X + margin, Caption.Count => max.X - margin - drawn.X,
            _ => (min.X + max.X - drawn.X) * .5f };
        float y = role switch { Caption.Key => min.Y - MathF.Min(drawn.Y * .50f, size * .225f),
            Caption.Timer => (min.Y + max.Y - drawn.Y) * .5f,
            _ => max.Y + MathF.Min(size * .055f, gap * .5f) - drawn.Y };
        var clipMin = new Vector2(min.X, min.Y - size * .275f);
        var clipMax = new Vector2(max.X, max.Y + MathF.Min(size * .08f, gap * .5f + 2));
        if (role == Caption.Key)
        {
            float keyInset = MathF.Min(margin, MathF.Max(0, (size - drawn.X) / 2));
            float left = min.X + keyInset, right = MathF.Max(left, max.X - keyInset - drawn.X);
            float anchor = p.KeyAlign switch { KeyAlignment.Left => left, KeyAlignment.Right => right, _ => (left + right) / 2 };
            var vp = ImGui.GetMainViewport();
            var guard = ImGui.GetStyle().WindowPadding * .5f + Vector2.One;
            var lower = vp.WorkPos + guard;
            var upper = Vector2.Max(lower, vp.WorkPos + vp.WorkSize - guard - drawn);
            var placed = Vector2.Clamp(new Vector2(anchor, y) + new Vector2(p.KeyOffsetX, p.KeyOffsetY) * (size / 48f), lower, upper);
            x = placed.X; y = placed.Y;
            float outline = MathF.Max(4, size * .07f);
            clipMin = Vector2.Min(clipMin, placed - new Vector2(outline));
            clipMax = Vector2.Max(clipMax, placed + drawn + new Vector2(outline));
            // Integer clip rectangles must include the outline after its subpixel translation.
            clipMin = new(MathF.Floor(clipMin.X), MathF.Floor(clipMin.Y));
            clipMax = new(MathF.Ceiling(clipMax.X), MathF.Ceiling(clipMax.Y));
        }
        var origin = new Vector2(x, y) - lo * scale;
        var at = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        var fraction = role == Caption.Key ? origin - at : Vector2.Zero;
        float edge = Math.Clamp(MathF.Round(size / 48), 1, 2);
        uint fill = (native.Ink >> 24) == 0 ? fallback : native.Ink;
        uint fallbackEdge = role == Caption.Cost && fallback == ResourceColor(SlotCostType.Magic) ? 0xFF512B54 : Dark;
        uint border = (native.Edge >> 24) != 0 ? native.Edge : fallbackEdge;
        dl.PushClipRect(clipMin, clipMax, true);
        try
        {
            int first = dl.VtxBuffer.Size;
            float px = em * scale;
            if (keyPress > 0)
            {
                // Follow the letter shapes so the pressed binding is legible over busy artwork.
                float glow = edge + MathF.Max(1, size * .025f);
                uint bloom = WithAlpha(0x004AA9FF, .80f * keyPress);
                dl.AddText(font, px, at - new Vector2(glow, 0), bloom, text);
                dl.AddText(font, px, at + new Vector2(glow, 0), bloom, text);
                dl.AddText(font, px, at - new Vector2(0, glow), bloom, text);
                dl.AddText(font, px, at + new Vector2(0, glow), bloom, text);
                fill = 0xFFE9FAFF;
            }
            dl.AddText(font, px, at - new Vector2(edge, 0), border, text);
            dl.AddText(font, px, at + new Vector2(edge, 0), border, text);
            dl.AddText(font, px, at - new Vector2(0, edge), border, text);
            dl.AddText(font, px, at + new Vector2(0, edge), border, text);
            dl.AddText(font, px, at, fill, text);
            // ImGui snaps text origins to pixels. Move only this label's new vertices
            // so a 0.1px adjustment applies equally to the outline and the glyph face.
            if (fraction != Vector2.Zero)
                foreach (ref var vertex in dl.VtxBuffer.AsSpan()[first..]) vertex.Pos += fraction;
        }
        finally { dl.PopClipRect(); }
        return drawn.X;
    }
}
