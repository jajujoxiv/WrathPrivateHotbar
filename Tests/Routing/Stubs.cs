using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
namespace Dalamud.Hooking
{
    public class Hook<T>(T original) : IDisposable where T : Delegate
    {
        public T Original { get; } = original;
        public bool IsEnabled
        {
            get; private set;
        }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
        public void Dispose() => Disable();
    }
}
namespace Dalamud.Plugin.Services
{
    public interface IGameInteropProvider
    {
        Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint addr, T detour) where T : Delegate;
    }
    public unsafe interface IGameGui
    {
        bool GameUiHidden => false;
        T* GetAddonByName<T>(string name) where T : unmanaged;
    }
}
namespace FFXIVClientStructs.FFXIV.Common.Math
{
    public struct Bounds
    {
        public System.Drawing.Point Pos1, Pos2; public int Width => Pos2.X - Pos1.X; public int Height => Pos2.Y - Pos1.Y;
    }
}
namespace FFXIVClientStructs.FFXIV.Client.System.Input
{
    public enum SeVirtualKey : byte
    {
        NO_KEY, A = 0x41
    }
    public enum InputId
    {
        HOTBAR_1_1 = 57
    }
    public struct KeySetting
    {
        public SeVirtualKey Key; public byte KeyModifier;
    }
    public static class InputData { public static class MemberFunctionPointers { public static nint GetKeybind = 1; } }
    public unsafe struct Keybind
    {
        public KeySetting* KeySettings;
    }
}
namespace FFXIVClientStructs.FFXIV.Client.UI
{
    using FFXIVClientStructs.FFXIV.Common.Math;
    using FFXIVClientStructs.FFXIV.Client.System.Input;
    public unsafe struct UIInputData
    {
        public static UIInputData* Current; public static UIInputData* Instance() => Current;
        public int NumKeybinds; public byte CurrentKeyModifier; public Keybind* Keybinds;
        public static readonly Dictionary<int,int> Remap = new();
        public Keybind* GetKeybind(InputId id) => Keybinds + Remap.GetValueOrDefault((int)id, (int)id);
        public static HashSet<int> Down = [];
        public bool IsKeyDown(SeVirtualKey k) => Down.Contains((byte)k);
        public bool IsKeyPressed(SeVirtualKey k) => false;
    }
    public unsafe struct Node
    {
        public ushort Width, Height; public int Left, Top; public Node* ParentNode; public FFXIVClientStructs.FFXIV.Component.GUI.ByteColor Color; public bool Visible; public bool IsVisible() => Visible;
        public void ToggleVisibility(bool v) => Visible = v;
        public void GetBounds(Bounds* b)
        {
            *b = new()
            {
                Pos1 = new(Left, Top),
                Pos2 = new(Left + Width, Top + Height)
            };
        }
    }
    public unsafe struct IconWidget
    {
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode* QuantityText;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkUldManager UldManager;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkResNode* FrameContainer, Frame, ComboBorder, OuterResNode;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode* IconImage, FrameIcon;
        public uint IconId;
        public int Loads;
        public bool LoadIcon(uint icon) { IconId = icon; Loads++; return true; }
    }
    public unsafe struct Component
    {
        public Node AtkResNode;
        public IconWidget* Icon;
        public IconWidget* GetAsAtkComponentIcon() => Icon;
    }
    public unsafe struct DragDrop
    {
        public int SoundEffectId;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkUldManager UldManager;
        public Component* OwnerNode;
        public IconWidget* AtkComponentIcon;
    }
    public unsafe struct HintBytes
    {
        public byte* Data;
        public int Length;
        public ReadOnlySpan<byte> AsSpan() => new(Data, Length);
    }
    public unsafe struct HudSlot
    {
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkResNode* IconFrame; public Component* Icon; public DragDrop* ComponentDragDrop; public FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode* ControlHintTextNode;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkResNode* RecastOverlayContainer;
        public FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode* ChargeIcon;
    }
    public unsafe struct Vec
    {
        public HudSlot* Pointer; public int Count; public Span<HudSlot> AsSpan() => new(Pointer, Count);
    }
    public unsafe struct AddonActionBarBase
    {
        public byte Alpha; public short CurrentPulsingSlots; public int RaptureHotbarId, SlotCount; public bool IsVisible; public Node* RootNode; public Vec ActionBarSlotVector;
        public static List<(int, int)> Pulses = [];
        public static List<(int, int)> HiddenPulses = [];
#if CLIENT_SCENARIOS
        public struct Table { public nint PulseActionBarSlot, PlaySoundEffect; }
        public Table* VirtualTable;
        public static class Delegates
        {
            public delegate void PulseActionBarSlot(AddonActionBarBase* hud, int slot);
            public delegate void PlaySoundEffect(AddonActionBarBase* hud, int id);
        }
        public static Delegates.PulseActionBarSlot? RoutedPulse;
        public static Delegates.PlaySoundEffect? RoutedSound;
        public static bool NativeSound, Paint, AnimateRequests = true;
        public static void NativeButtonSound(AddonActionBarBase* hud, int id) => UIGlobals.PlaySoundEffect((uint)id);
        public static void NativePulse(AddonActionBarBase* hud, int slot)
        {
            hud->Animate(slot);
            if (NativeSound) (RoutedSound ?? NativeButtonSound)(hud, 12);
        }
        private void Shade(int i, short amount)
        {
            var drag = ActionBarSlotVector.Pointer[i].ComponentDragDrop;
            if (drag != null && drag->AtkComponentIcon != null && drag->AtkComponentIcon->IconImage != null)
                drag->AtkComponentIcon->IconImage->AtkResNode.AddRed = drag->AtkComponentIcon->IconImage->AtkResNode.AddRed_2 = amount;
        }
        public static int PulseDurationMs = 45;
        private fixed long Ends[12];
        public void AdvancePulseTimeline(long now)
        {
            for (int i = 0; i < 12; i++)
                if (Ends[i] != 0 && now >= Ends[i])
                {
                    CurrentPulsingSlots &= (short)~(1 << i);
                    Ends[i] = 0; Shade(i,0);
                }
        }
#endif
        public void PulseActionBarSlot(int i)
        {
#if CLIENT_SCENARIOS
            fixed (AddonActionBarBase* self = &this) (RoutedPulse ?? NativePulse)(self,i);
#else
            Animate(i);
#endif
        }
        private void Animate(int i)
        {
#if CLIENT_SCENARIOS
            if (AnimateRequests)
            {
                Ends[i] = Environment.TickCount64 + PulseDurationMs;
                if (Paint) Shade(i,90);
                CurrentPulsingSlots |= (short)(1 << i);
            }
#endif
            (IsVisible && RootNode != null && RootNode->Visible ? Pulses : HiddenPulses).Add((RaptureHotbarId + 1, i));
        }
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public enum ActionType { Action, Item }
    public unsafe partial struct ActionManager
    {
#if CLIENT_SCENARIOS
        public RecastDetail* Gcd;
#endif
        public float AnimationLock, CastTimeElapsed, CastTimeTotal;
        public ActionType CastActionType;
        public uint CastActionId, CastSpellId;
        public ulong CastTargetId, QueuedTargetId;
        public bool ActionQueued;
        public uint QueuedActionId;
        public ActionType QueuedActionType;
        public ushort LastUsedActionSequence;
        public static Func<uint, uint> GameAdjustment = id => id;
        public uint GetAdjustedActionId(uint id) => GameAdjustment(id);
        public static ActionManager* Current; public static ActionManager* Instance() => Current;
        public static class MemberFunctionPointers
        {
            public static nint GetAdjustedActionId = 1, UseActionLocation = 5;
        }
    }
}
namespace FFXIVClientStructs.FFXIV.Client.UI.Misc
{
    public enum SlotCostType : byte { Default, Health, Magic, Tactical, Crafting, Gathering, JobGauge, Ceruleum }
    public enum HotbarSlotApparentActionMode : byte { AdjustedAction = 3, PvPCombo = 4, SkipActionResolution = 7, None = 255 }
    public unsafe partial struct RaptureHotbarModule
    {
        public enum HotbarSlotType
        {
            None, Action, Item, GeneralAction, Macro
        }
        public struct HotbarUIIntermediate
        {
            public uint ActionId, IconId, CostValue; public int Cost, Recast; public bool DrawAnts;
        }
        public struct HotbarSlot
        {
            public FFXIVClientStructs.FFXIV.Client.UI.HintBytes TooltipHint, ShortHint;
            public ReadOnlySpan<byte> PopUpKeybindHint => TooltipHint.AsSpan();
            public ReadOnlySpan<byte> KeybindHint => ShortHint.AsSpan();
            public HotbarSlotType CommandType, ApparentSlotType;
            public HotbarSlotApparentActionMode ApparentActionMode;
            public uint CommandId, ApparentActionId, NativeIconCache, IconId;
            public uint CostValue;
            public byte CostType, CostDisplayMode;
            public string CostTextString => NativeText.GetValueOrDefault(ApparentActionId, "");
            public string GetCostTextForSlot(HotbarSlotType type, uint id) => NativeText.GetValueOrDefault(id, "");
            public static readonly Dictionary<uint, string> NativeText = [];
            public static class MemberFunctionPointers
            {
                public static bool CostLookup = true;
                public static void* GetCostTypeForSlot => CostLookup ? (void*)1 : null;
                public static void* GetCostTextForSlot => (void*)1;
            }
            public static readonly Dictionary<uint, (uint Amount, SlotCostType Type)> Costs = [];
            public uint GetCostValueForSlot(HotbarSlotType kind, uint id) => Costs.TryGetValue(id, out var cost) ? cost.Amount : id;
            public SlotCostType GetCostTypeForSlot(HotbarSlotType kind, uint id) => Costs.TryGetValue(id, out var cost) ? cost.Type : SlotCostType.Default;
            public bool IsActionHighlighted(HotbarSlotType kind, uint id) => id == 300;
            public int GetIconIdForSlot(HotbarSlotType kind, uint id) => (int)(id + 500);
        }
        public static class MemberFunctionPointers
        {
            public static nint ExecuteSlotById = 2, ExecuteSlot = 3, PrepareSlotForRender = 4, GetSlotAppearance = 6;
        }
        public bool ModuleReady; public HotbarSlot* Slots;
        public static RaptureHotbarModule* Live; public static RaptureHotbarModule* Instance() => Live;
        public HotbarSlot* GetSlotById(uint b, uint i) => b < 10 && i < 12 ? Slots + b * 12 + i : null;
    }
}
#if !CLIENT_SCENARIOS
namespace Dalamud.Bindings.ImGui
{
    public enum ImDrawFlags
    {
        None
    }
    public class View
    {
        public Vector2 Pos = Vector2.Zero;
    }
    public class Draw
    {
        public int Fills, Borders; public void AddRectFilled(Vector2 a, Vector2 b, uint c, float f) => Fills++; public void AddRect(Vector2 a, Vector2 b, uint c, float f, ImDrawFlags flag, float width) => Borders++;
    }
    public static class ImGui
    {
        public static readonly Draw Draw = new(); public static Draw GetForegroundDrawList() => Draw; public static View GetMainViewport() => new();
    }
}
namespace WrathPrivateHotbar
{
    public class WrathLink
    {
        public bool Ready = true; public Func<uint, uint>? Lookup;
        public uint Standard(uint id) => FFXIVClientStructs.FFXIV.Client.Game.ActionManager.GameAdjustment(id);
        public void EnsureNative() { }
    }
}

#endif

namespace Lumina.Text.ReadOnly
{
    public readonly ref struct ReadOnlySeStringSpan
    {
        private readonly ReadOnlySpan<byte> bytes;
        public ReadOnlySeStringSpan(ReadOnlySpan<byte> value) => bytes = value;
        public string ExtractText() => System.Text.Encoding.UTF8.GetString(bytes);
    }
}

namespace FFXIVClientStructs.FFXIV.Component.GUI
{
    public enum FontType { Axis, MiedingerMed, Miedinger, TrumpGothic }
    public enum NodeType { Res, Text, Image, NineGrid }
#if !CLIENT_SCENARIOS
    public struct AtkImageNode { public AtkResNode AtkResNode; }
    public unsafe struct AtkStage { public System.Drawing.Size ScreenSize; public static AtkStage* Instance() => null; }
#endif
    public struct ByteColor { public uint RGBA; }
    public unsafe struct AtkResNode
    {
        public ushort Width, Height;
        public short X,Y, AddRed, AddGreen, AddBlue, AddRed_2, AddGreen_2, AddBlue_2; public float ScaleX, ScaleY;
        public unsafe void GetBounds(FFXIVClientStructs.FFXIV.Common.Math.Bounds* b) =>
            *b = new() { Pos1 = new(X,Y), Pos2 = new(X+Width,Y+Height) };
        public ByteColor Color; public bool IsVisible() => Visible;
        public NodeType Type; public AtkResNode* ChildNode; public AtkResNode* PrevSiblingNode; public AtkResNode* ParentNode; public bool Visible;
        public AtkTextNode* GetAsAtkTextNode() { fixed (AtkResNode* self = &this) return Type == NodeType.Text ? (AtkTextNode*)self : null; }
    }
    public unsafe struct AtkTextNode
    {
        public AtkResNode AtkResNode;
        public FFXIVClientStructs.FFXIV.Client.UI.HintBytes NodeText;
        public byte FontSize; public FontType FontType; public ByteColor TextColor, EdgeColor;
        public bool IsVisible() => AtkResNode.Visible;
    }
    public unsafe struct AtkUldManager { public AtkResNode** NodeList; public ushort NodeListCount; }
}
