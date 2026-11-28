using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Dalamud.Plugin
{
    public interface IDalamudPlugin : IDisposable
    {
    }
    public sealed class PluginInfo
    {
        public bool IsLoaded { get; set; } = true;
        public string InternalName { get; init; } = "WrathCombo";
        public Version Version => typeof(PluginInfo).Assembly.GetName().Version!;
    }
    public interface IActivePluginsChangedEventArgs
    {
        IEnumerable<string> AffectedInternalNames
        {
            get;
        }
    }
    public sealed class Change(params string[] names) : IActivePluginsChangedEventArgs
    {
        public IEnumerable<string> AffectedInternalNames => names;
    }
    public sealed class Ui
    {
        public Dalamud.Interface.ManagedFontAtlas.FontAtlas FontAtlas { get; } = new();
        public bool CutsceneActive; public event Action? Draw, OpenConfigUi, OpenMainUi;
        public void Frame() => Draw?.Invoke();
        public void Config() => OpenConfigUi?.Invoke();
        public void MainUi() => OpenMainUi?.Invoke();
    }
    public interface IDalamudPluginInterface
    {
        Ui UiBuilder
        {
            get;
        }
        IEnumerable<PluginInfo> InstalledPlugins
        {
            get;
        }
        event Action<IActivePluginsChangedEventArgs>? ActivePluginsChanged;
        string GetPluginConfigDirectory();
        PluginInfo? GetPlugin(Assembly assembly);
        PluginInfo? GetPlugin(AssemblyLoadContext context);
    }
}
namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag
    {
        BetweenAreas, BetweenAreas51, InCombat, OccupiedInEvent, OccupiedInQuestEvent, OccupiedInCutSceneEvent, WatchingCutscene, WatchingCutscene78, BoundByDuty56
    }
}
namespace Dalamud.Game.Command
{
    public sealed class CommandInfo(Action<string, string> handler)
    {
        public string HelpMessage = ""; public void Invoke(string name, string arg) => handler(name, arg);
    }
}
namespace Dalamud.Game.Addon.Lifecycle
{
    public enum AddonEvent
    {
        PreDraw, PostDraw
    }
}
namespace Dalamud.Game.Addon.Lifecycle.AddonArgTypes
{
    public unsafe sealed class AddonArgs(nint address, string name)
    {
        public string AddonName => name; public bool PreventOriginalRequested => Prevented;
        public bool Prevented; public T* GetAddonPointer<T>() where T : unmanaged => (T*)address;
        public void PreventOriginal() => Prevented = true;
    }
}
namespace Dalamud.Interface.Textures
{
    public struct GameIconLookup
    {
        public uint IconId; public bool HiRes;
    }
    public record struct RawImageSpecification(int Width, int Height)
    {
        public int DxgiFormat => 28;
        public int Pitch { get; init; } = Width * 4;
        public static RawImageSpecification Rgba32(int w,int h) => new(w,h);
    }
    public record struct TextureModificationArgs(Vector2 Uv0, Vector2 Uv1, int NewWidth, int NewHeight, int DxgiFormat);
    public sealed class Texture(uint id, bool hiRes) : Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap
    {
        public static uint PendingIcon, PendingHighRes;
        public uint Handle => id;
        public Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap CreateWrapSharingLowLevelResource() => new Texture(id, hiRes);
        public void Dispose() {}
        public bool TryGetWrap(out Texture t, out object? e)
        {
            t = this;
            e = null;
            return id != PendingIcon && (!hiRes || id != PendingHighRes);
        }
    }
}
namespace Dalamud.Plugin.Services
{
    public interface ICommandManager
    {
        void AddHandler(string name, CommandInfo handler); void RemoveHandler(string name);
    }
    public interface IFramework
    {
        event Action<IFramework>? Update;
    }
    public interface IClientState
    {
        bool IsLoggedIn
        {
            get;
        }
        uint TerritoryType
        {
            get;
        }
    }
    public struct Row
    {
        public uint RowId;
    }
    public interface IPlayerState
    {
        bool IsLoaded
        {
            get;
        }
        Row ClassJob
        {
            get;
        }
    }
    public interface ICondition
    {
        bool this[ConditionFlag flag] { get; }
    }
    public interface ITextureProvider
    {
        Texture GetFromGame(string path);
        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap CreateFromRaw(RawImageSpecification specs, ReadOnlySpan<byte> bytes, string name);
        Texture GetFromGameIcon(GameIconLookup icon);
    }
    public interface ITextureReadbackProvider
    {
        Task<(RawImageSpecification, byte[])> GetRawImageAsync(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap wrap,
            TextureModificationArgs args, bool leaveWrapOpen, CancellationToken token);
    }
    public interface IPluginLog
    {
        void Debug(Exception ex, string message);
    }
    public sealed class Sheet<T> where T : struct
    {
        public T? GetRowOrDefault(uint id)
        {
            if (typeof(T) == typeof(Lumina.Excel.Sheets.Action))
                return (T)(object)new Lumina.Excel.Sheets.Action { Icon = id + 500, Name = "test", PrimaryCostType = Lumina.Excel.Sheets.Action.CostTypes.GetValueOrDefault(id) };
            if (typeof(T) == typeof(Lumina.Excel.Sheets.Item))
                return (T)(object)new Lumina.Excel.Sheets.Item { Icon = id + 500 };
            return null;
        }
    }
    public interface IDataManager
    {
        Sheet<T> GetExcelSheet<T>() where T : struct;
    }
    public interface IAddonLifecycle
    {
        delegate void Listener(AddonEvent e, AddonArgs args);
        void RegisterListener(AddonEvent e, IEnumerable<string> names, Listener callback);
        void UnregisterListener(AddonEvent e, IEnumerable<string> names, Listener callback);
    }
}
namespace Lumina.Excel.Sheets
{
    public struct Action
    {
        public uint Icon; public string Name; public byte PrimaryCostType;
        public static readonly Dictionary<uint, byte> CostTypes = [];
    }
    public struct Item
    {
        public uint Icon;
    }
}
namespace FFXIVClientStructs.FFXIV.Component.GUI
{
    public unsafe struct AtkUnitBase
    {
        public bool IsVisible; public Node* RootNode;
    }
    public struct InputManager
    {
        public bool IsTextInputActive;
    }
    public unsafe struct AtkStage
    {
        public System.Drawing.Size ScreenSize;
        public static AtkStage* Live; public static AtkStage* Instance() => Live; public InputManager* AtkInputManager;
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public struct HotbarCooldownInfo
    {
        public bool CooldownActive; public float PrimaryTotalTime, PrimaryElapsedTime;
    }
    public unsafe struct InventoryManager
    {
        public static InventoryManager* Live;
        public static InventoryManager* Instance() => Live;
        public static Dictionary<(uint Id, bool High), int> Stock = [];
        public int GetInventoryItemCount(uint id, bool h, bool a, bool b) => Stock.GetValueOrDefault((id,h));
    }
    public unsafe partial struct ActionManager
    {
        public RecastDetail* GetRecastGroupDetail(int group) => group == 57 ? Gcd : null;
        public int GetRecastGroup(int t, uint id) => Gcd != null && Gcd->ActionId == id ? 57 : -1;
        public int GetAdditionalRecastGroup(ActionType t, uint id) => -1;
        public static readonly Dictionary<uint, uint> Status = [];
        public uint GetActionStatus(ActionType type, uint id) => Status.GetValueOrDefault(id);
        public static readonly Dictionary<uint, uint> Charges = [];
        public static readonly Dictionary<uint, HotbarCooldownInfo> Timers = [];
        public static int GetMaxCharges(uint id, int level) => Charges.ContainsKey(id) ? 2 : 1;
        public uint GetCurrentCharges(uint id) => Charges.GetValueOrDefault(id);
        public bool IsActionHighlighted(ActionType kind, uint id) => false;
        public void GetHotbarCooldownInfo(HotbarCooldownInfo* info, ActionType kind, uint id) => *info = Timers.GetValueOrDefault(id);
    }
}
namespace FFXIVClientStructs.FFXIV.Client.UI
{
    public static unsafe class UIGlobals
    {
        public static class MemberFunctionPointers { public static nint PlaySoundEffect = 7; }
        public static class Delegates { public delegate void PlaySoundEffect(uint id, FFXIVClientStructs.FFXIV.Client.Sound.SoundData** a, FFXIVClientStructs.FFXIV.Client.Sound.SoundData** b,byte flags); }
        public static Delegates.PlaySoundEffect? Routed;
        public static int Sounds;
        public static readonly List<uint> Played = new();
        public static void Native(uint id, FFXIVClientStructs.FFXIV.Client.Sound.SoundData** a, FFXIVClientStructs.FFXIV.Client.Sound.SoundData** b, byte flags) { Sounds++; Played.Add(id); }
        public static void PlaySoundEffect(uint id) => (Routed ?? Native)(id,null,null,0);
    }
}
namespace FFXIVClientStructs.FFXIV.Client.UI.Misc
{
    public unsafe partial struct RaptureHotbarModule
    {
        public static Func<uint, uint, byte> Execute = (_, _) => 0;
        public byte ExecuteSlotById(uint bar, uint slot) => Execute(bar, slot);
    }
}
namespace WrathPrivateHotbar
{
    internal static class DesktopInput
    {
        public static bool Focused = true;
        public static bool Foreground() => Focused;
        public static HashSet<int>? DesktopKeys;
        public static unsafe bool PhysicalDown(int key)
        {
            if (DesktopKeys != null) return DesktopKeys.Contains(key);
            var input = FFXIVClientStructs.FFXIV.Client.UI.UIInputData.Instance();
            int bit = key switch { 0x10 => 1, 0x11 => 2, 0x12 => 4, _ => 0 };
            return bit != 0 && input != null ? (input->CurrentKeyModifier & bit) != 0 :
                FFXIVClientStructs.FFXIV.Client.UI.UIInputData.Down.Contains(key);
        }
    }
}

namespace Dalamud.Interface.GameFonts
{
    public enum GameFontFamilyAndSize { MiedingerMid36 = 1, Axis36 = 2, TrumpGothic34 = 3 }
    public readonly record struct GameFontStyle(GameFontFamilyAndSize Font);
}
namespace Dalamud.Interface.ManagedFontAtlas
{
    public interface IFontHandle : IDisposable { IDisposable Push(); }
    public sealed class FontAtlas
    {
        public static bool Unavailable;
        public IFontHandle NewGameFontHandle(Dalamud.Interface.GameFonts.GameFontStyle style) =>
            Unavailable ? throw new InvalidOperationException("atlas unavailable") : new Handle((int)style.Font);
        private sealed class Handle(int face) : IFontHandle
        {
            public IDisposable Push()
            {
                var previous = Dalamud.Bindings.ImGui.ImGui.ActiveFont;
                Dalamud.Bindings.ImGui.ImGui.ActiveFont = face;
                return new Pop(previous);
            }
            public void Dispose() { }
        }
        private sealed class Pop(int previous) : IDisposable
        {
            public void Dispose() => Dalamud.Bindings.ImGui.ImGui.ActiveFont = previous;
        }
    }
}

namespace FFXIVClientStructs.FFXIV.Client.Game.Control
{
    public unsafe struct TargetSystem
    {
        public static TargetSystem* Live; public static TargetSystem* Instance() => Live;
        public ulong Id; public ulong GetTargetObjectId() => Id;
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public struct RecastDetail { public bool IsActive; public float Elapsed, Total; public uint ActionId; }
}
namespace FFXIVClientStructs.FFXIV.Component.GUI
{
    public unsafe struct AtkImageNode { public AtkResNode AtkResNode; public AtkUldPartsList* PartsList; public ushort PartId; }
    public unsafe struct AtkUldPartsList { public uint PartCount; public AtkUldPart* Parts; }
    public unsafe struct AtkUldPart { public ushort U,V,Width,Height; public AtkUldAsset* UldAsset; }
    public struct AtkUldAsset { public AtkTexture AtkTexture; }
    public enum TextureType { Resource, KernelTexture }
    public unsafe struct AtkTexture
    {
        public TextureType TextureType; public AtkTextureResource* Resource; public uint Width, Height;
        public uint GetTextureWidth() => Width; public uint GetTextureHeight() => Height;
    }
    public unsafe struct AtkTextureResource { public FileHandle* TexFileResourceHandle; }
    public struct FileHandle { public FakePath FileName; }
    public struct FakePath { public override string ToString() => "ui/uld/fixture.tex"; }
}

namespace FFXIVClientStructs.FFXIV.Client.Sound { public struct SoundData {} }
namespace FFXIVClientStructs.FFXIV.Component.GUI { public unsafe struct AtkNineGridNode { public AtkResNode AtkResNode; public AtkUldPartsList* PartsList; public uint PartId; public short TopOffset, BottomOffset, LeftOffset, RightOffset; } }
namespace Dalamud.Interface.Textures.TextureWraps
{
    public interface IDalamudTextureWrap : IDisposable
    {
        uint Handle { get; }
        IDalamudTextureWrap CreateWrapSharingLowLevelResource();
    }
}
