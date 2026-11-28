using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using WrathPrivateHotbar;
using M = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using S = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using O = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarUIIntermediate;
using K = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

internal sealed unsafe class Simulation : IDalamudPluginInterface, IFramework, ICommandManager, IClientState, IPlayerState, ICondition, IGameGui, IGameInteropProvider, IDataManager, ITextureProvider, ITextureReadbackProvider, IPluginLog, IAddonLifecycle, IDisposable
{
    internal delegate byte ById(M* m, uint b, uint i);
    internal delegate byte BySlot(M* m, S* s);
    internal delegate void Prepare(M* m, S* s, O* o);
    internal delegate byte Used(ActionManager* m, ActionType type, uint id, ulong target, Vector3* pos, uint extra, byte flag);
    private delegate uint Appearance(K* type, uint* id, ushort* state, M* m, S* slot);
    private Appearance appearance = NativeAppearance;
    private ById byId; private BySlot bySlot; private Prepare draw; private Used used;
    public event Action<IActivePluginsChangedEventArgs>? ActivePluginsChanged;
    public event Action<IFramework>? Update;
    public Ui UiBuilder { get; } = new();
    internal PluginInfo Wrath = new(); internal bool ByContext, ByRegistry;
    internal readonly PluginInfo ReAction = new() { InternalName = "ReAction", IsLoaded = false };
    public IEnumerable<PluginInfo> InstalledPlugins => [Wrath, ReAction];
    public PluginInfo? GetPlugin(Assembly asm) => !ByRegistry && !ByContext && asm == GetType().Assembly ? Wrath : null;
    public PluginInfo? GetPlugin(AssemblyLoadContext c) => !ByRegistry && c == AssemblyLoadContext.GetLoadContext(GetType().Assembly) ? Wrath : null;
    public bool IsLoggedIn { get; set; } = true; public bool IsLoaded { get; set; } = true;
    public uint TerritoryType => 0; public uint JobId = 25; public Row ClassJob => new() { RowId = JobId };
    public HashSet<ConditionFlag> Flags = []; public bool this[ConditionFlag f] => Flags.Contains(f);
    public List<string> Errors = []; public void Debug(Exception e, string message) => Errors.Add(message + " " + e.GetType().Name);
    public bool FailRouter, FailObserver; public bool GameUiHidden
    {
        get; set;
    }
    private readonly Dictionary<string, CommandInfo> commands = [];
    private readonly Dictionary<(AddonEvent Event, string Name), IAddonLifecycle.Listener> listeners = [];
    private readonly List<nint> allocated = [];
    private readonly string config = Directory.CreateTempSubdirectory("wph-").FullName;
    private T* Allocate<T>(int count = 1) where T : unmanaged
    {
        var p = NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
        allocated.Add((nint)p);
        return (T*)p;
    }
    public string GetPluginConfigDirectory() => config;
    public void AddHandler(string name, CommandInfo handler) => commands.Add(name, handler);
    public void RemoveHandler(string name) => commands.Remove(name);
    public void Command(string text) => commands["/wprivate"].Invoke("/wprivate", text);
    public void RegisterListener(AddonEvent e, IEnumerable<string> names, IAddonLifecycle.Listener cb)
    {
        foreach (var n in names)
            listeners.Add((e, n), cb);
    }
    public void UnregisterListener(AddonEvent e, IEnumerable<string> names, IAddonLifecycle.Listener cb)
    {
        foreach (var n in names)
            listeners.Remove((e, n));
    }
    public Sheet<T> GetExcelSheet<T>() where T : struct => new(); public List<GameIconLookup> IconLookups = [];
    internal const uint PressTexture = 900002;
    internal byte[] Mask = [];
    internal bool FailMask;
    public Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap CreateFromRaw(RawImageSpecification specs, ReadOnlySpan<byte> pixels, string name)
    {
        if (FailMask) throw new NotSupportedException("No runtime textures");
        if (pixels.Length != 4*specs.Width*specs.Height) throw new ArgumentException();
        Mask = pixels.ToArray(); return new Texture(PressTexture,false);
    }
    public Texture GetFromGame(string path) => new(900001, false);
    internal byte AtlasAlpha = 255;
    internal int AtlasReads;
    internal TaskCompletionSource<(RawImageSpecification, byte[])>? DelayedRead;
    public Task<(RawImageSpecification, byte[])> GetRawImageAsync(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap wrap,
        TextureModificationArgs args, bool leaveWrapOpen, CancellationToken token)
    {
        AtlasReads++;
        if (DelayedRead != null) return DelayedRead.Task.WaitAsync(token);
        if (wrap.Handle != 900001 || args.Uv0.X != .25f || args.Uv1.X != .4375f)
            throw new ArgumentException("Expected the selected press sprite crop");
        var spec = RawImageSpecification.Rgba32(args.NewWidth, args.NewHeight) with { Pitch = args.NewWidth * 4 + 16 };
        var raster = Enumerable.Repeat((byte)255, spec.Pitch * spec.Height).ToArray();
        for (int y = 0; y < spec.Height; y++)
        for (int x = 0; x < spec.Width; x++)
            raster[y * spec.Pitch + x * 4 + 3] = AtlasAlpha;
        return Task.FromResult((spec, raster));
    }
    public Texture GetFromGameIcon(GameIconLookup icon)
    {
        IconLookups.Add(icon);
        return new(icon.IconId, icon.HiRes);
    }
    private HintBytes StoreHint(string value)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(value);
        var target = Allocate<byte>(data.Length + 1);
        data.CopyTo(new Span<byte>(target, data.Length));
        return new HintBytes { Data = target, Length = data.Length };
    }
    public void SetHint(int addon, int slotIndex, string text)
    {
        var node = Allocate<AtkTextNode>();
        node->NodeText = StoreHint(text);
        Huds[addon - 1].ActionBarSlotVector.Pointer[slotIndex].ControlHintTextNode = node;
    }
    public AtkTextNode* TextNode(string value, byte size, FontType family, uint ink, uint edge)
    {
        var t = Allocate<AtkTextNode>();
        t->AtkResNode.Type = NodeType.Text;
        t->AtkResNode.Visible = true;
        t->NodeText = StoreHint(value);
        t->FontSize = size; t->FontType = family;
        t->TextColor.RGBA = ink; t->EdgeColor.RGBA = edge;
        return t;
    }

    public void SetTimerNode(int bar, int slot, AtkTextNode* text)
    {
        var parent = Allocate<AtkResNode>();
        parent->ChildNode = (AtkResNode*)text;
        Huds[bar - 1].ActionBarSlotVector.Pointer[slot].RecastOverlayContainer = parent;
    }

    public AtkImageNode* InstallPressFrame(int bar, int slot)
    {
        var img = Allocate<AtkImageNode>();
        img->AtkResNode.Type = NodeType.Image;
        img->AtkResNode.Width = img->AtkResNode.Height = 48;
        img->AtkResNode.Color.RGBA = 0xFFFFFFFF;
        img->PartsList = Allocate<AtkUldPartsList>();
        img->PartsList->PartCount = 1;
        var part = img->PartsList->Parts = Allocate<AtkUldPart>();
        part->U = 64; part->V = 32; part->Width = part->Height = 48;
        part->UldAsset = Allocate<AtkUldAsset>();
        var tex = &part->UldAsset->AtkTexture;
        tex->Width = tex->Height = 256;
        tex->Resource = Allocate<AtkTextureResource>();
        tex->Resource->TexFileResourceHandle = Allocate<FileHandle>();
        Huds[bar - 1].ActionBarSlotVector.Pointer[slot].IconFrame = (AtkResNode*)img;
        return img;
    }

    internal AtkNineGridNode* InstallOuterGrip(int bar, int slot)
    {
        var original=InstallPressFrame(bar,slot);
        var grid=Allocate<AtkNineGridNode>();
        grid->AtkResNode=original->AtkResNode;
        grid->AtkResNode.Type=NodeType.NineGrid; grid->PartsList=original->PartsList;
        grid->TopOffset=grid->BottomOffset=grid->LeftOffset=grid->RightOffset=6;
        var parent=Allocate<AtkResNode>();
        *parent=new(){ Type=NodeType.Res, Visible=true, Width=48, Height=48, Color=new(){ RGBA=0xffffffff }, ChildNode=(AtkResNode*)grid };
        grid->AtkResNode.ParentNode=parent;
        Huds[bar-1].ActionBarSlotVector.Pointer[slot].IconFrame=null;
        Widget(bar,slot)->OuterResNode=parent;
        return grid;
    }

    public AtkImageNode* InstallLayeredPressFrame(int bar, int slot, out nint unrelated)
    {
        var image = InstallPressFrame(bar, slot);
        var skill = InstallPressFrame(bar, slot);
        unrelated = (nint)skill;
        var frame = Allocate<AtkResNode>();
        frame->Visible = true;
        frame->Type = NodeType.Res;
        frame->Color.RGBA = 0x80ffffff;
        frame->Width = frame->Height = 48;
        image->AtkResNode.X = 6;
        image->AtkResNode.Y = 9;
        image->AtkResNode.Width = 24;
        image->AtkResNode.Height = 18;
        image->AtkResNode.Color.RGBA = 0x80ffffff;
        frame->ChildNode = (AtkResNode*)image;
        image->AtkResNode.PrevSiblingNode = (AtkResNode*)skill;
        Widget(bar, slot)->FrameContainer = frame;
        Widget(bar, slot)->IconImage = skill;
        Huds[bar - 1].ActionBarSlotVector.Pointer[slot].IconFrame = frame;
        return image;
    }

    public void SetCostNode(int bar, int slot, AtkTextNode* text)
    {
        var entries = (AtkResNode**)Allocate<nint>();
        entries[0] = (AtkResNode*)text;
        Widget(bar, slot)->UldManager = new() { NodeList = entries, NodeListCount = 1 };
    }

    public void SetTooltipHint(int bar, int i, string text) =>
        Module->GetSlotById((uint)(bar - 1), (uint)i)->TooltipHint = StoreHint(text);
    private static readonly string[] NativeNames = ["_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09"];
    internal M* Module; internal AddonActionBarBase* Huds; internal int Dispatches; internal uint LastUsed;
    public bool RenderCached, RejectNext, QueueNext, DeferNextCast;
    public Action? BeforeNativePointer;
    public Simulation()
    {
        UIInputData.Remap.Clear();
        AddonActionBarBase.Pulses.Clear();
        AddonActionBarBase.HiddenPulses.Clear();
        AddonActionBarBase.AnimateRequests = AddonActionBarBase.Paint = true;
        AddonActionBarBase.NativeSound = false; AddonActionBarBase.RoutedPulse = null; AddonActionBarBase.RoutedSound = null;
        UIGlobals.Routed = null; UIGlobals.Played.Clear();
        AddonActionBarBase.PulseDurationMs = 45;
        TestWrath.Root = TestWrath.NativeRoot = 100;
        S.Costs.Clear(); S.NativeText.Clear(); S.MemberFunctionPointers.CostLookup = true;
        Lumina.Excel.Sheets.Action.CostTypes.Clear();
        ActionManager.Charges.Clear(); ActionManager.Status.Clear();
        ActionManager.Timers.Clear();
        TestWrath.Next = 300;
        TestWrath.Queries = 0;
        UIGlobals.Sounds = 0;
        byId = NativeId;
        bySlot = NativeSlot;
        draw = NativeDraw;
        used = NativeUsed;
        Module = Allocate<M>();
        M.Live = Module;
        Module->ModuleReady = true;
        Module->Slots = Allocate<S>(120);
        InventoryManager.Live = Allocate<InventoryManager>(); InventoryManager.Stock.Clear();
        ActionManager.Current = Allocate<ActionManager>();
        ActionManager.Current->Gcd = Allocate<RecastDetail>();
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Live = Allocate<FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem>();
        ActionManager.GameAdjustment = TestWrath.Global;
        AtkStage.Live = Allocate<AtkStage>();
        AtkStage.Live->ScreenSize = new(1920, 1080);
        AtkStage.Live->AtkInputManager = Allocate<InputManager>();
        var input = Allocate<UIInputData>();
        UIInputData.Current = input;
        input->NumKeybinds = 200;
        input->Keybinds = Allocate<Keybind>(200);
        var bindings = Allocate<KeySetting>(400);
        for (int i = 0; i < 200; i++)
            input->Keybinds[i].KeySettings = bindings + 2 * i;
        var iconParts = Allocate<AtkUldPartsList>();
        iconParts->PartCount = 1;
        iconParts->Parts = Allocate<AtkUldPart>();
        iconParts->Parts->Width = iconParts->Parts->Height = 48;
        iconParts->Parts->UldAsset = Allocate<AtkUldAsset>();
        iconParts->Parts->UldAsset->AtkTexture.Width = iconParts->Parts->UldAsset->AtkTexture.Height = 48;
        Huds = Allocate<AddonActionBarBase>(10);
        for (int b = 0; b < 10; b++)
        {
            var a = Huds + b;
            a->VirtualTable = Allocate<AddonActionBarBase.Table>();
            *a->VirtualTable = new() { PulseActionBarSlot = 8, PlaySoundEffect = 9 };
            a->RaptureHotbarId = b;
            a->IsVisible = true;
            a->RootNode = Allocate<Node>();
            a->Alpha = 255;
            *a->RootNode = new() { Visible = true, Color = new() { RGBA = 0xffffffff }, Width = 600, Height = 48 };
            a->SlotCount = 12;
            a->ActionBarSlotVector = new()
            {
                Pointer = Allocate<HudSlot>(12),
                Count = 12
            };
            for (int i = 0; i < 12; i++)
            {
                var widget = Allocate<IconWidget>();
                widget->IconImage = Allocate<AtkImageNode>();
                widget->IconImage->AtkResNode = new() { Type=NodeType.Image, Visible=true, Width=48,Height=48, Color=new(){ RGBA=0xffffffff } };
                widget->IconImage->PartsList = iconParts;
                var drag = Allocate<DragDrop>();
                drag->AtkComponentIcon = widget;
                drag->OwnerNode = Allocate<Component>();
                drag->OwnerNode->AtkResNode = new() { Visible = true, Width = 48, Height = 48, Left = 40 + i * 50,
                    Top = 20 + b * 50, ParentNode = a->RootNode, Color = new() { RGBA = 0xffffffff } };
                var hudSlot = a->ActionBarSlotVector.Pointer + i;
                hudSlot->ComponentDragDrop = drag;
                hudSlot->IconFrame = Allocate<AtkResNode>();
                Place(b + 1, i, 100);
            }
        }
        M.Execute = (b, i) => byId(Module, b, i);
    }
    public void Bind(int bar, int slot, int vk, byte modifiers = 0, int column = 0) =>
        UIInputData.Current->Keybinds[(int)InputId.HOTBAR_1_1 + (bar - 1) * 12 + slot].KeySettings[column] = new() { Key = (SeVirtualKey)vk, KeyModifier = modifiers };
    public void Place(int b, int i, uint id) => *Module->GetSlotById((uint)b - 1, (uint)i) = new() { CommandType = K.Action, CommandId = id, ApparentActionMode = HotbarSlotApparentActionMode.AdjustedAction, ApparentSlotType = K.Action, ApparentActionId = id, IconId = id + 500 };
    public T* GetAddonByName<T>(string name) where T : unmanaged
    {
        int b = Array.IndexOf(NativeNames, name);
        return b < 0 ? null : (T*)(Huds + b);
    }
    public bool DrawPrevented(int physicalBar)
    {
        var a = new AddonArgs((nint)(Huds + physicalBar - 1), NativeNames[physicalBar - 1]);
        if (listeners.TryGetValue((AddonEvent.PreDraw, NativeNames[physicalBar - 1]), out var cb))
            cb(AddonEvent.PreDraw, a);
        return a.Prevented;
    }
    public bool AutoDraw = true;
    public Action<int>? AtNativeDraw;
    public void DrawNative(int bar, bool skipped = false)
    {
        var args = new AddonArgs((nint)(Huds + bar - 1), NativeNames[bar - 1]) { Prevented = skipped };
        if (!skipped && listeners.TryGetValue((AddonEvent.PreDraw, args.AddonName), out var pre)) pre(AddonEvent.PreDraw, args);
        if (!args.Prevented) AtNativeDraw?.Invoke(bar);
        if (listeners.TryGetValue((AddonEvent.PostDraw, args.AddonName), out var cb)) cb(AddonEvent.PostDraw, args);
    }
    public void Tick()
    {
        for (int b = 0; b < 10; b++) Huds[b].AdvancePulseTimeline(Environment.TickCount64);
        Update?.Invoke(this);
        if (AutoDraw)
            for (int b = 0; b < 10; b++)
                if (Huds[b].IsVisible && Huds[b].RootNode->Visible) DrawNative(b + 1);
    }
    public void RenderGui()
    {
        ImGui.Frame();
        UiBuilder.Frame();
    }
    public void Notify() => ActivePluginsChanged?.Invoke(new Change("WrathCombo"));
    public O RenderSlot(int b, int i)
    {
        O o = default;
        draw(Module, Module->GetSlotById((uint)b - 1, (uint)i), &o);
        return o;
    }
    public void PressSlot(int b, int i) => byId(Module, (uint)b - 1, (uint)i);
    internal readonly List<(int Bar, int Slot)> Numbered = [];
    public ulong Target
    {
        get => FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Live->Id;
        set => FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Live->Id = value;
    }
    public void DirectUse(uint id) => used(ActionManager.Instance(), ActionType.Action, id, Target, null, 0, 0);
    private byte NativeId(M* m, uint b, uint i)
    {
        Numbered.Add(((int)b + 1, (int)i));
        BeforeNativePointer?.Invoke();
        return bySlot(m, m->GetSlotById(b, i));
    }
    public void PressPointer(int bar, int slot) => bySlot(Module, Module->GetSlotById((uint)(bar - 1), (uint)slot));
    public IconWidget* Widget(int bar, int slot) => Huds[bar - 1].ActionBarSlotVector.Pointer[slot].ComponentDragDrop->AtkComponentIcon;
    public uint AppearanceOf(int bar, int i)
    {
        var slot = Module->GetSlotById((uint)(bar - 1), (uint)i);
        K kind = slot->CommandType; uint id = 0; ushort state = 0;
        var returned = appearance(&kind, &id, &state, Module, slot);
        if (returned != id) throw new Exception("appearance result disagrees with out parameter");
        return id;
    }
    private static uint NativeAppearance(K* kind, uint* id, ushort* state, M* m, S* s)
    {
        *kind = s->CommandType;
        return *id = s->ApparentActionId;
    }
    public void FireQueued()
    {
        var am = ActionManager.Instance();
        if (!am->ActionQueued) return;
        am->ActionQueued = false;
        used(am, ActionType.Action, am->QueuedActionId, Target, null, 0, 0);
    }
    private byte NativeSlot(M* m, S* s)
    {
        LastUsed = s->CommandType == K.GeneralAction && s->CommandId == 4 ? 3u : ActionManager.GameAdjustment(s->CommandId);
        Dispatches++;
        if (DeferNextCast) { DeferNextCast = false; return 1; }
        if (QueueNext)
        {
            QueueNext = false;
            var am = ActionManager.Instance();
            am->ActionQueued = true; am->QueuedActionType = ActionType.Action; am->QueuedActionId = LastUsed; am->QueuedTargetId = Target;
            return 1;
        }
        return used(ActionManager.Instance(), ActionType.Action, LastUsed, Target, null, 0, 0);
    }
    private byte NativeUsed(ActionManager* m, ActionType t, uint id, ulong target, Vector3* loc, uint e, byte f)
    {
        if (RejectNext) { RejectNext = false; return 0; }
        m->LastUsedActionSequence++;
        return 1;
    }
    private void NativeDraw(M* m, S* s, O* o)
    {
        if (!RenderCached) s->ApparentActionId = ActionManager.GameAdjustment(s->CommandId);
        s->IconId = s->ApparentActionId + 500;
        o->ActionId = s->ApparentActionId;
        o->IconId = s->IconId;
    }
    public Hook<T> HookFromAddress<T>(nint addr, T detour) where T : Delegate
    {
        Delegate original;
        switch ((int)addr)
        {
            case 7:
                UIGlobals.Routed = detour.Method.CreateDelegate<UIGlobals.Delegates.PlaySoundEffect>(detour.Target);
                original = (UIGlobals.Delegates.PlaySoundEffect)UIGlobals.Native; break;
            case 8:
                AddonActionBarBase.RoutedPulse = detour.Method.CreateDelegate<AddonActionBarBase.Delegates.PulseActionBarSlot>(detour.Target);
                original = (AddonActionBarBase.Delegates.PulseActionBarSlot)AddonActionBarBase.NativePulse; break;
            case 9:
                AddonActionBarBase.RoutedSound = detour.Method.CreateDelegate<AddonActionBarBase.Delegates.PlaySoundEffect>(detour.Target);
                original = (AddonActionBarBase.Delegates.PlaySoundEffect)AddonActionBarBase.NativeButtonSound; break;
            case 2 when !FailRouter:
                byId = detour.Method.CreateDelegate<ById>(detour.Target);
                original = (ById)NativeId;
                break;
            case 3:
                bySlot = detour.Method.CreateDelegate<BySlot>(detour.Target);
                original = (BySlot)NativeSlot;
                break;
            case 4:
                draw = detour.Method.CreateDelegate<Prepare>(detour.Target);
                original = (Prepare)NativeDraw;
                break;
            case 6:
                appearance = detour.Method.CreateDelegate<Appearance>(detour.Target);
                original = (Appearance)NativeAppearance;
                break;
            case 5 when !FailObserver:
                used = detour.Method.CreateDelegate<Used>(detour.Target);
                original = (Used)NativeUsed;
                break;
            default:
                throw new InvalidOperationException("unavailable hook");
        }
        return new(original.Method.CreateDelegate<T>(original.Target));
    }
    public void Dispose()
    {
        UIInputData.Down.Clear();
        WrathPrivateHotbar.DesktopInput.DesktopKeys = null;
        UIInputData.Current = null;
        M.Live = null;
        InventoryManager.Live = null;
        ActionManager.Current = null;
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Live = null;
        AtkStage.Live = null;
        foreach (var p in allocated)
            NativeMemory.Free((void*)p);
        Directory.Delete(config, true);
    }
}
