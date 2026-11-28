using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace WrathPrivateHotbar;

// The actual selected addon's native pulse remains private when its root is hidden.
// Borrow its animated frame parts in the ImGui pass. Never move or reveal the game addon.
internal enum PressSignal { Contact, HeldBeat, Dispatch, Native, NativeEdge }

internal sealed unsafe partial class NativePressArt(IGameGui gui, ITextureProvider textures, ITextureReadbackProvider readback) : IOrdinaryPressArt, IDisposable
{
    private readonly PressAtlas atlas = new(readback);
    private readonly record struct Part(nint Node, nint Parts, uint PartId, bool Visible, uint Color, int Additive, float X, float Y, float ScaleX, float ScaleY);
    private readonly record struct Node(nint Address, bool Visible, uint Tint, int Additive);
    private sealed class Sample
    {
        internal int Addon = -1, Count;
        internal Address Button;
        internal nint Hud, Frame, IconNode;
        internal Vector3 IconLight;
        internal long BaselineAt, LastPulse, PoseAt, MovingUntil;
        internal int LastPose;
        internal readonly Accent Accent = new();
        internal readonly Part[] Idle = new Part[32];
    }
    private sealed class Contact
    {
        internal readonly Sample Own = new();
        internal long LastNative, LastDispatch, NativeUntil, ObservedUntil;
        internal int Period;
        internal PressSignal LastSignal;
        internal long LastPress, Released, ExternalUntil, VisibleAt;
        internal bool Unpainted, DimFrameDrawn, Held, HasPhysical;
        internal void ResetTiming()
        {
            LastNative = LastDispatch = NativeUntil = ObservedUntil = 0;
            Period = 0;
        }
        internal void Clear()
        {
            Own.Count = 0; Own.Hud = 0; Own.LastPulse = 0;
            ResetTiming();
            LastPress = Released = ExternalUntil = VisibleAt = 0;
            Unpainted = DimFrameDrawn = Held = HasPhysical = false;
        }
    }
    private readonly Contact[] contacts = Enumerable.Range(0, 12).Select(_ => new Contact()).ToArray();
    private readonly Sample?[,] ordinary = new Sample[10,12];
    internal string Last { get; private set; } = "入力待ち";
    internal int LastSlot { get; private set; } = -1;
    internal long Inputs { get; private set; }
    internal long Onsets { get; private set; }
    internal void Reset()
    {
        foreach (var c in contacts) c.Clear();
        Array.Clear(ordinary);
        Last = "入力待ち";
        LastSlot = -1;
        Inputs = Onsets = 0;
    }
    internal void ResetSlot(int slot) => contacts[slot].Clear();

    internal void Press(int slot, long now, int repeatLimit, PressSignal signal)
    {
        var c = contacts[slot];
        long since = now - c.LastPress;
        if (signal == PressSignal.NativeEdge)
        {
            // The bit rises after the actual pulse hook. It is only a fallback when the hook missed it.
            if (c.LastNative != 0 && now - c.LastNative <= 180) return;
            signal = PressSignal.Native;
        }
        bool native = signal == PressSignal.Native;
        if (native || signal == PressSignal.Dispatch)
        {
            long previous = native ? c.LastNative : c.LastDispatch;
            if (native) c.LastNative = now;
            else c.LastDispatch = now;
            if (!native && (now < c.NativeUntil || c.LastNative != 0 && now - c.LastNative <= 24)) return;
            long gap = now - previous;
            if (previous != 0 && gap is >= 8 && gap <= Math.Clamp(repeatLimit + 50, 140, 1000))
            {
                c.Period = (int)gap;
                c.ObservedUntil = now + gap + Math.Clamp(gap / 2, 30, 110);
                if (native) c.NativeUntil = c.ObservedUntil;
            }
        }
        else if (signal == PressSignal.HeldBeat && (now < c.ObservedUntil ||
            now - Math.Max(c.LastDispatch,c.LastNative) < Math.Min(60,repeatLimit / 2))) return;
        // The selected HUD callback and its slot dispatch usually describe the same tap.
        // Distinct repeats on the same native path, including short rejected presses, remain visible.
        if (c.LastPress != 0 && since < (signal == c.LastSignal && (native || signal == PressSignal.Dispatch) ? 8 : 28)) return;
        if (!c.Held && (c.LastPress == 0 || c.Released != 0 || since > Math.Max(240, repeatLimit + 70)))
        {
            c.HasPhysical = false;
            c.Released = c.ExternalUntil = 0;
        }
        if (!c.HasPhysical && c.LastPress != 0 && since <= Math.Clamp(repeatLimit + 65, 100, 320))
            c.ExternalUntil = now + Math.Clamp(repeatLimit + 45, 110, 280);
        Accept(c, slot, now, signal);
    }

    private void Accept(Contact c, int slot, long now, PressSignal signal)
    {
        c.LastSignal = signal;
        c.LastPress = now;
        c.Unpainted = true;
        LastSlot = slot;
        Last = "描画待ち";
        Inputs++;
    }

    internal void ContactState(int i, bool held, long now)
    {
        var c = contacts[i];
        if (held && !c.Held)
        {
            if (c.LastPress == 0 || c.Released != 0 || now - c.LastPress >= 45)
                Accept(c, i, now, PressSignal.Contact);
            c.HasPhysical = true;
            c.Released = c.ExternalUntil = 0;
        }
        else if (!held && c.Held)
        {
            c.Released = now;
            c.ResetTiming();
            c.Unpainted = c.DimFrameDrawn = false;
            c.VisibleAt = 0;
        }
        c.Held = held;
    }

    // Each observed input has an attack and a release. Between beats the contact stays faint.
    internal float ContactLight(int i, long now)
    {
        var c = contacts[i];
        if (c.LastPress == 0 || c.HasPhysical && !c.Held && c.Released != 0) return 0;
        if (c.Unpainted)
        {
            c.Unpainted = false;
            // At most one visible downstroke per frame. A burst of callbacks must
            // allow at least one dim frame between peaks, even at low frame rates.
            if (now - c.LastPress <= 300 && (c.VisibleAt == 0 || c.DimFrameDrawn && now - c.VisibleAt >= 45))
            {
                c.VisibleAt = now;
                c.DimFrameDrawn = false;
                Onsets++;
            }
        }
        if (c.VisibleAt == 0) return 0;
        long end = Math.Max(c.VisibleAt + 65, c.ExternalUntil);
        float decay = c.Held ? 1 : 1 - Math.Clamp((now - end) / 130f, 0, 1);
        float duration = c.Period > 0 ? Math.Clamp(c.Period * .8f, 32, 90) : 90;
        float phase = 1 - Math.Clamp((now - c.VisibleAt) / duration, 0, 1);
        float level = decay * (.28f + .72f * phase * phase);
        c.DimFrameDrawn |= level <= .60f;
        return level;
    }

    internal void Update(GameBars bars, int selected, int heldMask, long now)
    {
        var hud = bars.SelectedHud(selected, out int addon);
        int count = hud == null || hud->RootNode == null ? 0 : Math.Min(12, Math.Min((int)hud->SlotCount, hud->ActionBarSlotVector.AsSpan().Length));
        for (int i = 0; i < contacts.Length; i++)
        {
            var c = contacts[i];
            ContactState(i, (heldMask & (1 << i)) != 0, now);
            if (i >= count) { c.Own.Count = 0; continue; }
            var button = new Address(selected, i);
            var s = c.Own;
            bool active = IsPulsing(hud, i);
            bool same = Same(s, hud, addon, button);
            if (!same || (!active && (s.Count == 0 || s.LastPulse == 0 || now - s.LastPulse > 650) && now - s.BaselineAt >= 200))
                TryCapture(s, hud, addon, button, now, active);
            if (active && (c.Held || now - c.LastPress < 250)) s.LastPulse = now;
        }
    }

    internal void BeforeSelectedPulse(AddonActionBarBase* hud, int addon, int index, long now)
    {
        var button = new Address(hud->RaptureHotbarId + 1, index);
        if (!OnPage(hud, button)) return;
        var sample = contacts[index].Own;
        if (!IsPulsing(hud, index)) TryCapture(sample, hud, addon, button, now, false);
        sample.LastPulse = now;
    }

    public void BeforePulse(int slot, int addon, Address button, long now)
    {
        var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[addon]);
        if (!GameBars.Visible(hud) || !OnPage(hud, button)) return;
        var s = ordinary[addon,button.Slot] ??= new();
        bool active = IsPulsing(hud, button.Slot);
        if (!active || !Same(s, hud, addon, button))
            TryCapture(s, hud, addon, button, now, active);
        s.LastPulse = now;
        s.Accent.Probe = active && slot >= 0;
        s.Accent.Native = false;
        var icon = IconNode(hud,button.Slot);
        s.Accent.Before = icon == null ? default : Rendered(icon);
    }

    private static bool OnPage(AddonActionBarBase* hud, Address a) => hud != null && a.Valid &&
        hud->RaptureHotbarId + 1 == a.Bar && a.Slot < hud->SlotCount && a.Slot < hud->ActionBarSlotVector.AsSpan().Length;
    private static bool IsPulsing(AddonActionBarBase* hud, int slot) => ((ushort)hud->CurrentPulsingSlots & (1 << slot)) != 0;
    private static bool Same(Sample s, AddonActionBarBase* hud, int addon, Address a) =>
        s.Hud == (nint)hud && s.Addon == addon && s.Button == a && s.Frame == (nint)hud->ActionBarSlotVector.AsSpan()[a.Slot].IconFrame;

    private static void Collect(AtkResNode* n, Span<Node> nodes, ref int count, ReadOnlySpan<nint> excluded, int depth, bool shown = true, uint opacity = 255)
    {
        if (n == null || count >= nodes.Length || depth > 5 || excluded.Contains((nint)n)) return;
        for (int i = 0; i < count; i++) if (nodes[i].Address == (nint)n) return;
        shown &= n->IsVisible();
        uint color = n->Color.RGBA;
        opacity = (opacity * (color >> 24) + 127) / 255;
        int additive = Math.Clamp((int)n->AddRed, 0, 255) | Math.Clamp((int)n->AddGreen, 0, 255) << 8 |
            Math.Clamp((int)n->AddBlue, 0, 255) << 16;
        nodes[count++] = new((nint)n, shown, (color & 0xffffff) | (opacity << 24), additive);
        if (n->Type != NodeType.Res) return;
        int siblings = 0;
        for (var child = n->ChildNode; child != null && count < nodes.Length && siblings++ < 32; child = child->PrevSiblingNode)
            Collect(child, nodes, ref count, excluded, depth + 1, shown, opacity);
    }

    private static int Layers(AddonActionBarBase* hud, int slot, Span<Node> nodes)
    {
        var field = hud->ActionBarSlotVector.AsSpan()[slot];
        int count = 0;
        var drag = field.ComponentDragDrop;
        var widget = drag == null ? null : drag->AtkComponentIcon;
        if (widget == null && field.Icon != null) widget = field.Icon->GetAsAtkComponentIcon();
        // Frame nodes can be siblings in a different ULD layout. Keep the action icon,
        // charge counter, combo border and recast animation out of the press snapshot.
        ReadOnlySpan<nint> excluded = stackalloc nint[]
        {
            (nint)field.RecastOverlayContainer, (nint)field.ChargeIcon, (nint)field.ControlHintTextNode,
            widget == null ? 0 : (nint)widget->IconImage, widget == null ? 0 : (nint)widget->ComboBorder
        };
        if (widget != null) Collect(widget->FrameContainer, nodes, ref count, excluded, 0);
        Collect(field.IconFrame, nodes, ref count, excluded, 0);
        if (widget != null)
        {
            Collect(widget->OuterResNode, nodes, ref count, excluded, 0);
            Collect(widget->Frame, nodes, ref count, excluded, 0);
            Collect((AtkResNode*)widget->FrameIcon, nodes, ref count, excluded, 0);
        }
        return count;
    }

    private static AtkUldPartsList* Parts(AtkResNode* n, out uint part)
    {
        if (n->Type == NodeType.Image) { var image = (AtkImageNode*)n; part = image->PartId; return image->PartsList; }
        if (n->Type == NodeType.NineGrid) { var grid = (AtkNineGridNode*)n; part = grid->PartId; return grid->PartsList; }
        part = 0; return null;
    }

    private static void TryCapture(Sample s, AddonActionBarBase* hud, int addon, Address button, long now, bool active)
    {
        try { Capture(s, hud, addon, button, now, active); }
        catch (Exception) { s.Count = 0; }
    }

    private static void Capture(Sample s, AddonActionBarBase* hud, int addon, Address button, long now, bool alreadyActive)
    {
        s.Addon = addon; s.Button = button; s.Hud = (nint)hud; s.BaselineAt = now;
        s.Frame = (nint)hud->ActionBarSlotVector.AsSpan()[button.Slot].IconFrame;
        s.Count = 0; s.LastPulse = s.PoseAt = s.MovingUntil = 0; s.Accent.Previous = 0;
        s.IconNode = 0; s.IconLight = default;
        if (alreadyActive) return;
        var icon = IconNode(hud, button.Slot);
        if (icon != null) { s.IconNode = (nint)icon; s.IconLight = LightOf(icon); }
        Span<Node> nodes = stackalloc Node[32];
        int count = Layers(hud, button.Slot, nodes);
        foreach (var node in nodes[..count])
        {
            var n = (AtkResNode*)node.Address;
            var parts = Parts(n, out var part);
            if (parts == null) continue;
            s.Idle[s.Count++] = new(node.Address, (nint)parts, part, node.Visible, node.Tint, node.Additive, n->X, n->Y, n->ScaleX, n->ScaleY);
        }
    }

    private static AtkResNode* IconNode(AddonActionBarBase* hud, int slot)
    {
        var field = hud->ActionBarSlotVector.AsSpan()[slot];
        var icon = field.ComponentDragDrop == null ? null : field.ComponentDragDrop->AtkComponentIcon;
        if (icon == null && field.Icon != null) icon = field.Icon->GetAsAtkComponentIcon();
        return icon == null ? null : (AtkResNode*)icon->IconImage;
    }

    private static Vector3 LightOf(AtkResNode* n) => new(n->AddRed, n->AddGreen, n->AddBlue);

    private static bool Changed(Sample s, Node node, AtkUldPartsList* list, uint id, bool active)
    {
        var n = (AtkResNode*)node.Address;
        for (int j = 0; j < s.Count; j++)
        {
            var idle = s.Idle[j];
            if (idle.Node == node.Address && idle.Parts == (nint)list)
                return !idle.Visible || idle.PartId != id || idle.Color != node.Tint || idle.Additive != node.Additive ||
                    idle.X != n->X || idle.Y != n->Y || idle.ScaleX != n->ScaleX || idle.ScaleY != n->ScaleY;
        }
        return active && s.Count != 0;
    }

    private static bool VisibleNode(AtkResNode* node)
    {
        if (node == null || node->Width == 0 || node->Height == 0) return false;
        uint alpha = 255;
        int depth = 0;
        for (var parent = node; parent != null; parent = parent->ParentNode)
        {
            alpha = (alpha * (parent->Color.RGBA >> 24) + 127) / 255;
            if (!parent->IsVisible() || alpha < 12 || ++depth > 64) return false;
        }
        Bounds b = default;
        node->GetBounds(&b);
        if (b.Width <= 0 || b.Height <= 0 || b.Pos2.X <= 0 || b.Pos2.Y <= 0) return false;
        var stage = AtkStage.Instance();
        return stage == null || stage->ScreenSize.Width <= 0 || stage->ScreenSize.Height <= 0 ||
            b.Pos1.X < stage->ScreenSize.Width && b.Pos1.Y < stage->ScreenSize.Height;
    }

    public bool VisibleOrdinaryPress(int addon, Address button)
    {
        var sample = ordinary[addon, button.Slot];
        var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[addon]);
        if (sample == null || !GameBars.Visible(hud) || !OnPage(hud, button) || !Same(sample, hud, addon, button)) return false;
        var icon = IconNode(hud, button.Slot);
        if (icon != null && sample.IconNode == (nint)icon && LoadedIcon(icon) &&
            Vector3.Max(Vector3.Zero, LightOf(icon) - sample.IconLight).LengthSquared() >= 144) return true;
        Span<Node> nodes = stackalloc Node[32];
        int count = Layers(hud, button.Slot, nodes);
        foreach (var node in nodes[..count])
        {
            var n = (AtkResNode*)node.Address;
            if (!node.Visible || node.Tint >> 24 < 12 || !VisibleNode(n)) continue;
            var list = Parts(n, out var part);
            if (list == null || list->Parts == null || part >= list->PartCount || !Changed(sample,node,list,part,true)) continue;
            var frame = list->Parts + part;
            if (frame->UldAsset != null && frame->Width > 0 && frame->Height > 0 &&
                frame->UldAsset->AtkTexture.GetTextureWidth() > 0 && frame->UldAsset->AtkTexture.GetTextureHeight() > 0) return true;
        }
        return false;
    }

    internal bool Draw(int slot, ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, long now, float contact)
    {
        var c = contacts[slot];
        if (c.LastPress == 0 || (!c.Held && now - c.LastPress > 650)) return false;
        try
        {
            if (DrawSample(c.Own, dl, min, max, radius, now, contact))
            {
                if (slot == LastSlot) Last = "選択HBの純正押下";
                return true;
            }
        }
        catch (Exception ex)
        {
            c.Own.Count = 0;
            if (slot == LastSlot) Last = $"押下の縁（{ex.GetType().Name}）";
            return false;
        }
        if (slot == LastSlot) Last = "疑似アイコンの押し込み・内側光";
        return false;
    }

    private bool DrawSample(Sample s, ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, long now, float opacity)
    {
        if (s.Count == 0 && s.IconNode == 0) return false;
        var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[s.Addon]);
        if (!OnPage(hud, s.Button) || !Same(s, hud, s.Addon, s.Button)) return false;
        if (!IsPulsing(hud,s.Button.Slot) && (s.LastPulse == 0 || now - s.LastPulse is < 0 or > 650)) return false;
        var anchor = hud->ActionBarSlotVector.AsSpan()[s.Button.Slot].IconFrame;
        Span<Node> nodes = stackalloc Node[32];
        int count = Layers(hud, s.Button.Slot, nodes);
        opacity = SourceOpacity(s, nodes[..count], IconNode(hud,s.Button.Slot), now, opacity);
        bool drawn = false;
        Span<float> xs = stackalloc float[4], ys = stackalloc float[4], us = stackalloc float[4], vs = stackalloc float[4];
        foreach (var node in nodes[..count])
        {
            var n = (AtkResNode*)node.Address;
            if (!node.Visible || n->Width == 0 || n->Height == 0 || node.Tint >> 24 == 0) continue;
            var parts = Parts(n, out var partId);
            if (parts == null || parts->Parts == null || partId >= parts->PartCount) continue;
            if (!Changed(s, node, parts, partId, IsPulsing(hud, s.Button.Slot))) continue;
            var part = parts->Parts + partId;
            if (part->UldAsset == null || part->Width == 0 || part->Height == 0) continue;
            var t = &part->UldAsset->AtkTexture;
            if (t->TextureType != TextureType.Resource || t->Resource == null || t->Resource->TexFileResourceHandle == null) continue;
            float w = t->GetTextureWidth(), h = t->GetTextureHeight();
            if (w <= 0 || h <= 0 || part->U + part->Width > w || part->V + part->Height > h) continue;
            string path = t->Resource->TexFileResourceHandle->FileName.ToString();
            if (!path.StartsWith("ui/uld/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                !textures.GetFromGame(path).TryGetWrap(out var wrap, out _) || wrap == null) continue;
            var lo = min; var hi = max;
            if (anchor != null && n != anchor)
            {
                Bounds a = default, b = default;
                anchor->GetBounds(&a); n->GetBounds(&b);
                if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) continue;
                var scale = (max - min) / new Vector2(a.Width, a.Height);
                lo += new Vector2(b.Pos1.X - a.Pos1.X, b.Pos1.Y - a.Pos1.Y) * scale;
                hi = lo + new Vector2(b.Width, b.Height) * scale;
            }
            var inside = Vector2.Min(hi,max) - Vector2.Max(lo,min);
            if (inside.X <= 0 || inside.Y <= 0) continue;
            uint tint = (node.Tint & 0x00ffffff) | (uint)MathF.Round((node.Tint >> 24) * opacity) << 24;
            var uv = new Vector2(part->U / w, part->V / h);
            var end = new Vector2((part->U + part->Width) / w, (part->V + part->Height) / h);
            if (n->Type == NodeType.NineGrid)
            {
                // ULD nine-part frames retain edge widths when resized. Subdivide the
                // original atlas rect rather than stretching the outline with the center.
                var grid = (AtkNineGridNode*)n;
                GridAxis(lo.X,hi.X,uv.X,end.X,part->Width,n->Width,grid->LeftOffset,grid->RightOffset,xs,us);
                GridAxis(lo.Y,hi.Y,uv.Y,end.Y,part->Height,n->Height,grid->TopOffset,grid->BottomOffset,ys,vs);
                for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    if (xs[x+1] > xs[x] && ys[y+1] > ys[y])
                        dl.AddImageRounded(wrap.Handle, new(xs[x],ys[y]), new(xs[x+1],ys[y+1]),
                            new(us[x],vs[y]), new(us[x+1],vs[y+1]), tint, 0);
            }
            else dl.AddImageRounded(wrap.Handle, lo, hi, uv, end, tint, n == anchor ? radius : 0);
            // An atlas handle and an unclipped quad can both be valid for a fully
            // transparent sprite. Only trust bright texels that can fall inside this slot.
            float area = (hi.X - lo.X) * (hi.Y - lo.Y);
            float cell = (max.X-min.X) * (max.Y-min.Y);
            if (!drawn && (tint >> 24) >= 48 && inside.X * inside.Y >= cell * .05f)
            {
                float ink = atlas.Coverage(path, wrap, uv, end, tint);
                float outside = MathF.Max(0, area - inside.X * inside.Y);
                drawn = (area * ink - outside) * ((tint >> 24) / 255f) >= cell * .016f;
            }
        }
        if (!drawn)
        {
            // Some layouts pulse the action artwork itself by animating AddRed/Green/Blue.
            // Apply the observed channels to the private icon, never copy an ordinary slot's skill.
            var icon = IconNode(hud, s.Button.Slot);
            if (icon != null && (nint)icon == s.IconNode && icon->IsVisible())
            {
                var delta = Vector3.Clamp(LightOf(icon) - s.IconLight, Vector3.Zero, new Vector3(255));
                float peak = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
                float alpha = MathF.Min(peak, 142) * opacity;
                if (alpha >= 32)
                {
                    var rgb = delta * (255 / peak);
                    uint color = (uint)rgb.X | (uint)rgb.Y << 8 | (uint)rgb.Z << 16 | (uint)alpha << 24;
                    dl.AddRectFilled(min + Vector2.One, max - Vector2.One, color, radius);
                    // A colored wash can be indistinguishable on a matching skill
                    // icon. Keep the contrasting bevel when there is no press sprite.
                }
            }
        }
        return drawn;
    }
    // Preserve the native animation's own opacity while its frame, position or tint is moving.
    // A hidden HUD can leave one bright pose latched throughout several real key presses.
    private static float SourceOpacity(Sample sample, ReadOnlySpan<Node> layers, AtkResNode* icon, long now, float contact)
    {
        var pose = new HashCode();
        if (icon != null) pose.Add(LightOf(icon));
        foreach (var layer in layers)
        {
            var node = (AtkResNode*)layer.Address;
            var list = Parts(node,out uint part);
            if (list == null) continue;
            pose.Add(layer); pose.Add(part);
            pose.Add(node->X); pose.Add(node->Y); pose.Add(node->ScaleX); pose.Add(node->ScaleY);
        }
        int state = pose.ToHashCode();
        if (sample.PoseAt != 0 && sample.LastPose != state) sample.MovingUntil = now + 42;
        sample.PoseAt = now;
        sample.LastPose = state;
        return now < sample.MovingUntil ? 1 : contact;
    }

    private static void GridAxis(float lo, float hi, float uv0, float uv1, float texels, float extent,
        int start, int end, Span<float> positions, Span<float> uvs)
    {
        float a = Math.Clamp(start, 0, texels), b = Math.Clamp(end, 0, texels - a);
        float scale = (hi-lo) / MathF.Max(extent, a+b);
        positions[0] = lo; positions[1] = lo+a*scale; positions[2] = hi-b*scale; positions[3] = hi;
        uvs[0] = uv0; uvs[1] = uv0+(uv1-uv0)*a/texels; uvs[2] = uv1-(uv1-uv0)*b/texels; uvs[3] = uv1;
    }

    public void Dispose() => atlas.Dispose();
}
