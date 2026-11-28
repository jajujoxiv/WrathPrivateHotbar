using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace WrathPrivateHotbar;

internal sealed unsafe class WrathLink(IDalamudPluginInterface pi)
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Member = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private delegate uint Resolve(ActionManager* manager, uint id);
    private delegate uint ResolveAddress(nint manager, uint id);
    private object? instance;
    private Func<bool>? alive, savedEnabled;
    private Func<uint, uint>? vanilla;
    private Control? adjust, replaceability;
    private bool providerSeen;
    private sealed class Control(Func<bool> enabled, Action disable, Action enable)
    {
        public bool Enabled() => enabled();
        private bool owned;
        public void Suspend()
        {
            if (!enabled()) return;
            owned = true;
            disable();
        }
        public void Restore()
        {
            if (owned && !enabled()) enable();
        }
    }
    private volatile bool invalidated;
    private int refreshing;
    internal bool Invalidated => invalidated;
    internal bool Ready
    {
        get; private set;
    }
    internal Func<uint, uint>? Lookup
    {
        get; private set;
    }
    internal string Status { get; private set; } = "WrathComboを待機中";
    internal void Invalidate()
    {
        invalidated = true;
        Ready = false;
    }

    internal void Refresh()
    {
        if (Interlocked.Exchange(ref refreshing, 1) != 0) return;
        invalidated = false;
        try
        {
            providerSeen = pi.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName.Equals("WrathCombo", StringComparison.OrdinalIgnoreCase));
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "WrathCombo").ToArray();
            foreach (var assembly in candidates)
            {
                var owner = pi.GetPlugin(assembly);
                if (owner == null && AssemblyLoadContext.GetLoadContext(assembly) is { } context)
                    owner = pi.GetPlugin(context);
                // Some development builds are not attributed by GetPlugin. Only accept
                // an unambiguous loaded assembly with a matching installed version.
                if (owner == null && candidates.Length == 1)
                    owner = pi.InstalledPlugins.FirstOrDefault(p => p.IsLoaded && p.InternalName == "WrathCombo" &&
                        p.Version == assembly.GetName().Version);
                if (owner?.IsLoaded != true)
                    continue;
                var service = assembly.GetType("WrathCombo.Services.Service");
                var candidate = service?.GetProperty("ActionReplacer", Static)?.GetValue(null)
                    ?? service?.GetField("ActionReplacer", Static)?.GetValue(null);
                if (candidate == null)
                    continue;
                if (!ReferenceEquals(instance, candidate))
                {
                    Release();
                    var type = candidate.GetType();
                    var isEnabled = type.GetProperty("ActionReplacingEnabled", Member)?.GetGetMethod(true);
                    var disable = type.GetMethod("DisableActionReplacingIfRequired", Member, null, Type.EmptyTypes, null);
                    var enable = type.GetMethod("EnableActionReplacingIfRequired", Member, null, Type.EmptyTypes, null);
                    if (isEnabled?.ReturnType != typeof(bool) ||
                       disable?.ReturnType != typeof(void) || enable?.ReturnType != typeof(void))
                        continue;
                    var query = BindQuery(type, candidate);
                    var readEnabled = isEnabled.CreateDelegate<Func<bool>>(candidate);
                    var disableGlobal = disable.CreateDelegate<Action>(candidate);
                    var restoreGlobal = enable.CreateDelegate<Action>(candidate);
                    var liveService = service!;
                    bool IsCurrent() => owner.IsLoaded && ReferenceEquals(
                        liveService.GetProperty("ActionReplacer", Static)?.GetValue(null)
                        ?? liveService.GetField("ActionReplacer", Static)?.GetValue(null), candidate);
                    instance = candidate;
                    adjust = new Control(readEnabled, disableGlobal, restoreGlobal);
                    // The provider also overrides the game's decision about which actions
                    // may change. Its public disable method only releases the ID hook.
                    replaceability = BindReplaceability(type, candidate);
                    vanilla = BindVanilla(type, candidate);
                    savedEnabled = () =>
                    {
                        var config = ReadStatic(liveService, "Configuration");
                        if (config == null) return false;
                        var option = config.GetType();
                        return (option.GetProperty("ActionChanging", Member)?.GetValue(config) ??
                                option.GetField("ActionChanging", Member)?.GetValue(config)) is true;
                    };
                    alive = IsCurrent;
                    Lookup = query == null ? null : id =>
                    {
                        var am = ActionManager.Instance();
                        return !Ready || invalidated || !owner.IsLoaded || am == null ? 0 : query(am, id);
                    };
                }
                Maintain();
                return;
            }
            Release();
            Status = candidates.Length == 0 ? "WrathCombo未接続" : "WrathComboの分離機能を利用できません";
        }
        catch (Exception ex)
        {
            Release();
            Status = $"WrathCombo接続待ち（{ex.GetType().Name}）";
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }

    private static object? ReadStatic(Type type, string name) =>
        type.GetProperty(name, Static)?.GetValue(null) ?? type.GetField(name, Static)?.GetValue(null);

    private static Control? BindReplaceability(Type type, object provider)
    {
        var handle = type.GetField("isActionReplaceableHook", Member)?.GetValue(provider);
        if (handle == null) return null;
        var hook = handle.GetType();
        var running = hook.GetProperty("IsEnabled", Member)!.GetGetMethod(true)!.CreateDelegate<Func<bool>>(handle);
        var disable = hook.GetMethod("Disable", Member, null, Type.EmptyTypes, null)!.CreateDelegate<Action>(handle);
        var enable = hook.GetMethod("Enable", Member, null, Type.EmptyTypes, null)!.CreateDelegate<Action>(handle);
        return new Control(running, disable, enable);
    }

    private static Func<uint, uint>? BindVanilla(Type type, object provider)
    {
        var original = type.GetMethod("OriginalHook", Member, null, [typeof(uint)], null);
        return original?.ReturnType == typeof(uint) ? original.CreateDelegate<Func<uint, uint>>(provider) : null;
    }

    private static Resolve? BindQuery(Type type, object provider)
    {
        var typed = type.GetMethod("GetAdjustedActionDetour", Member, null, [typeof(ActionManager*), typeof(uint)], null);
        if (typed?.ReturnType == typeof(uint))
            return typed.CreateDelegate<Resolve>(provider);

        var address = type.GetMethod("GetAdjustedActionDetour", Member, null, [typeof(nint), typeof(uint)], null);
        if (address?.ReturnType == typeof(uint))
        {
            var call = address.CreateDelegate<ResolveAddress>(provider);
            return (am, id) => call((nint)am, id);
        }

        var direct = type.GetMethod("GetAdjustedAction", Member, null, [typeof(uint)], null);
        var refresh = type.GetMethod("UpdateFilteredCombos", Member, null, Type.EmptyTypes, null);
        var filtered = type.GetField("FilteredCombos", Static);
        if (direct?.ReturnType != typeof(uint) || refresh?.ReturnType != typeof(void) || filtered == null)
            return null;
        var resolve = direct.CreateDelegate<Func<uint, uint>>(provider);
        var init = refresh.CreateDelegate<Action>(provider);
        return (_, id) =>
        {
            if (filtered.GetValue(null) == null)
                init();
            return resolve(id);
        };
    }

    internal void EnsureNative()
    {
        if (invalidated) Refresh();
        if (invalidated || adjust == null) return;
        try
        {
            if (adjust.Enabled() || replaceability?.Enabled() == true)
                Maintain();
        }
        catch (Exception ex) { Ready = false; Status = $"WrathComboの切替を待機中（{ex.GetType().Name}）"; }
    }

    internal uint Standard(uint id)
    {
        var manager = ActionManager.Instance();
        if (id == 0 || manager == null) return id;
        // Avoid a replacement entry point even for the very first render after
        // Wrath is re-enabled between two framework updates.
        if (invalidated) EnsureNative();
        if (!invalidated && vanilla != null)
        {
            try
            {
                uint result = vanilla(id);
                if (result != 0) return result;
            }
            catch (Exception) { Invalidate(); }
        }
        if (providerSeen)
        {
            if (invalidated || adjust == null) return id;
            EnsureNative();
            try { if (adjust.Enabled()) return id; }
            catch (Exception) { Invalidate(); return id; }
        }
        uint native = manager->GetAdjustedActionId(id);
        return native == 0 ? id : native;
    }

    internal void Maintain()
    {
        if (invalidated || adjust == null) { Ready = false; return; }
        try
        {
            if (adjust.Enabled() || replaceability?.Enabled() == true)
            {
                Ready = false;
                if (alive?.Invoke() != true) { Invalidate(); return; }
                replaceability?.Suspend();
                adjust.Suspend();
            }
            bool neutral = !adjust.Enabled() && replaceability?.Enabled() != true;
            Ready = neutral && Lookup != null;
            Status = Ready ? "選択HBのみ有効" : neutral ? "通常HBは標準表示・選択HBのコンボ判定待ち" : "WrathComboの全体置換を停止できません";
        }
        catch (Exception ex) { Ready = false; Status = $"全体置換の切替待ち（{ex.GetType().Name}）"; }
    }

    internal void Release()
    {
        Ready = false;
        Status = "WrathComboを待機中";
        try
        {
            if (alive?.Invoke() == true)
            {
                if (savedEnabled?.Invoke() == true) replaceability?.Restore();
                adjust?.Restore(); // the provider checks its own setting when enabling
            }
        }
        catch (Exception ex) { Status = $"WrathCombo再接続待ち（{ex.GetType().Name}）"; }
        instance = null;
        alive = savedEnabled = null;
        adjust = replaceability = null;
        Lookup = vanilla = null;
    }
}
