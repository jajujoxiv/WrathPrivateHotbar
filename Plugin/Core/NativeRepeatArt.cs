using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace WrathPrivateHotbar;

internal sealed unsafe partial class NativePressArt
{
    // Some native press animations outlast the user's entire repeat interval. Use the
    // game's loaded icon texture and its draw-time additive channels for a short accent.
    // Nothing is written to the hotbar registration, recast, label or neighbouring UI.
    private readonly record struct Channels(short R, short G, short B)
    {
        internal Channels Lit(int power) => new(Brighten(R,power), Brighten(G,(power*9)/10), Brighten(B,(power*7)/10));
        private static short Brighten(short value, int by) => (short)(value >= 255 ? value : Math.Min(255, value + by));
        internal int Contrast(Channels other) => Math.Max(R-other.R, Math.Max(G-other.G, B-other.B));
    }

    private sealed class Accent
    {
        internal nint Node;
        internal Channels Local, LocalLit, Draw, DrawLit;
        internal int Previous, Amount;
        internal bool Sampled, Probe, Native;
        internal Channels Before;
    }

    private static Channels Local(AtkResNode* n) => new(n->AddRed,n->AddGreen,n->AddBlue);
    private static Channels Rendered(AtkResNode* n) => new(n->AddRed_2,n->AddGreen_2,n->AddBlue_2);

    private static bool LoadedIcon(AtkResNode* node)
    {
        if (!VisibleNode(node) || node->Type != NodeType.Image) return false;
        var image = (AtkImageNode*)node;
        var parts = image->PartsList;
        if (parts == null || parts->Parts == null || image->PartId >= parts->PartCount) return false;
        var part = parts->Parts + image->PartId;
        return part->Width > 0 && part->Height > 0 && part->UldAsset != null &&
            part->UldAsset->AtkTexture.GetTextureWidth() > 0 && part->UldAsset->AtkTexture.GetTextureHeight() > 0;
    }

    public void PrepareRepeat(int addon, Address button, long age)
    {
        var sample = ordinary[addon,button.Slot];
        var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[addon]);
        if (sample == null || !GameBars.Visible(hud) || !OnPage(hud,button) || !Same(sample,hud,addon,button)) return;
        var n = IconNode(hud,button.Slot);
        if (!LoadedIcon(n)) return;
        var a = sample.Accent;
        a.Sampled = true;
        a.Amount = 0;
        if (a.Probe)
        {
            a.Probe = false;
            // When the native retrigger itself brightens the icon, preserve that exact frame.
            a.Native = Rendered(n).Contrast(a.Before) >= a.Previous + 18;
        }
        if (a.Native)
        {
            a.Amount = Math.Max(0, Rendered(n).Contrast(a.Before));
            return;
        }
        if (age >= 72) return; // the intervening unaccented draw rearms this slot
        int power = (int)Math.Round(86 * Math.Pow(1 - age / 72.0, 1.5));
        if (power <= 0) return;
        a.Node = (nint)n;
        a.Local = Local(n); a.Draw = Rendered(n);
        a.LocalLit = a.Local.Lit(power); a.DrawLit = a.Draw.Lit(power);
        (n->AddRed,n->AddGreen,n->AddBlue) = (a.LocalLit.R,a.LocalLit.G,a.LocalLit.B);
        (n->AddRed_2,n->AddGreen_2,n->AddBlue_2) = (a.DrawLit.R,a.DrawLit.G,a.DrawLit.B);
        a.Amount = a.DrawLit.Contrast(a.Draw);
    }

    public bool FinishRepeat(int addon, int slot, bool drawn)
    {
        var sample = ordinary[addon,slot];
        if (sample == null || !sample.Accent.Sampled) return false;
        var a = sample.Accent;
        a.Sampled = false;
        bool painted = drawn;
        if (a.Node != 0)
        {
            // Revalidate live ownership before touching a pointer saved across addon callbacks.
            var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[addon]);
            var n = hud != null && slot < hud->SlotCount && slot < hud->ActionBarSlotVector.AsSpan().Length
                ? IconNode(hud,slot) : null;
            painted &= (nint)hud == sample.Hud && OnPage(hud,sample.Button);
            if ((nint)n == a.Node)
            {
                painted &= VisibleNode(n) && Rendered(n) == a.DrawLit;
                Restore(ref n->AddRed, a.LocalLit.R, a.Local.R);
                Restore(ref n->AddGreen, a.LocalLit.G, a.Local.G);
                Restore(ref n->AddBlue, a.LocalLit.B, a.Local.B);
                Restore(ref n->AddRed_2, a.DrawLit.R, a.Draw.R);
                Restore(ref n->AddGreen_2, a.DrawLit.G, a.Draw.G);
                Restore(ref n->AddBlue_2, a.DrawLit.B, a.Draw.B);
            }
            else painted = false;
            a.Node = 0;
        }
        if (!painted) return false;
        bool rise = a.Amount >= a.Previous + 18;
        a.Previous = a.Amount;
        return rise;
    }

    private static void Restore(ref short channel, short applied, short original)
    {
        if (channel == applied) channel = original;
    }
}
