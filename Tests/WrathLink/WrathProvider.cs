// The optional UPSTREAM build links the unmodified ActionReplacer.cs from WrathCombo.
// The simulated game and the small deterministic job preset are test fixtures.
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using WrathCombo.Services;

internal static unsafe class TestWrath
{
    internal static uint Next = 300;
    internal static uint Root = 100;
    internal static uint NativeRoot = 100;
    internal static int Queries;
    internal static uint Native(uint id) => id == Root ? NativeRoot : id;
    internal static uint Pick(uint id)
    {
        Queries++;
        return id == Root ? Next : Native(id);
    }
    internal static uint Global(uint id)
    {
        if (Service.ActionReplacer?.ActionReplacingEnabled != true)
            return Native(id);
        var target = Service.ActionReplacer;
        var fn = target.GetType().GetMethod("GetAdjustedActionDetour", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return fn.CreateDelegate<Adjustment>(target)(ActionManager.Instance(), id);
    }
    private delegate uint Adjustment(ActionManager* manager, uint id);
}
namespace WrathCombo.Services
{
    internal sealed class Options
    {
        public bool ActionChanging = true;
#if UPSTREAM
        public Options CustomActionSettings=>this;
        public bool AlwaysShowIcon=>false; public int Throttle=>0;public bool BlockSpellOnMove=>false;public int PenaltyPause=>0;
#endif
    }
    internal static class Service
    {
        public static Options Configuration = new();
        public static Core.ActionReplacer ActionReplacer { get; set; } = null!;
#if UPSTREAM
        internal sealed class Addresses {public nint IsActionIdReplaceable=>112;}
        public static Addresses Address=new();
#endif
    }
}
#if !UPSTREAM
namespace WrathCombo.Core
{
    internal unsafe sealed class ActionReplacer : IDisposable
    {
        public bool ActionReplacingEnabled { get; private set; } = Service.Configuration.ActionChanging;
        public void EnableActionReplacingIfRequired()
        {
            if (Service.Configuration.ActionChanging)
                ActionReplacingEnabled = true;
        }
        public void DisableActionReplacingIfRequired() => ActionReplacingEnabled = false;
        private sealed class Replaceability
        {
            public bool IsEnabled { get; private set; } = Service.Configuration.ActionChanging;
            public void Enable() => IsEnabled = true;
            public void Disable() => IsEnabled = false;
        }
        private readonly Replaceability isActionReplaceableHook = new();
        internal uint OriginalHook(uint id) => TestWrath.Native(id);
        private uint GetAdjustedActionDetour(ActionManager* manager, uint id) => TestWrath.Pick(id);
        public void Dispose() => ActionReplacingEnabled = false;
    }
}
#else
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public unsafe partial struct ActionManager
    {
        public static class Delegates {public delegate uint GetAdjustedActionId(ActionManager* am,uint id);}
        public static class Addresses { public static class GetAdjustedActionId {public static nint Value=>111;} }
        public static int GetAdjustedCastTime(ActionType t,uint id)=>0;
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game.UI
{
    public unsafe struct UIState {public static UIState* Instance()=>null;public bool IsUnlockLinkUnlockedOrQuestCompleted(uint id)=>true;}
}
namespace ECommons
{
    public static class GenericHelpers {public static bool IsScreenReady()=>true;}
    public static class TestLogExtension { public static void Log(this Exception problem)=>throw new InvalidOperationException("upstream error",problem);}
}
namespace ECommons.ExcelServices {public enum Job {ADV,GLA,PGL,MRD,LNC,ARC,CNJ,THM,MIN,BTN,FSH,ACN,ROG,BLM}}
namespace ECommons.GameHelpers
{
    public static class Player { public static bool Available=>true;public static object? Object=>typeof(Player);public static int Level=>20; public static ECommons.ExcelServices.Job Job=>ECommons.ExcelServices.Job.BLM;}
}
namespace ECommons.Throttlers { public static class EzThrottler { public static bool Throttle(string key,int wait)=>true; } }
namespace ECommons.DalamudServices
{
    public sealed class TestHooks
    {
        private static unsafe uint Original(ActionManager* m,uint id)=>TestWrath.Native(id);
        private static bool Replaceable(uint id)=>true;
        public Hook<T> HookFromAddress<T>(nint address,T callback) where T:Delegate
        {
            var method=typeof(TestHooks).GetMethod(address==111?"Original":"Replaceable",BindingFlags.NonPublic|BindingFlags.Static)!;
            return new(method.CreateDelegate<T>());
        }
    }
    public sealed class Client {public bool IsLoggedIn=>true;public bool IsPvP=>false;}
    public sealed class Log {public void Debug(string text){} public void Error(Exception e,string text)=>throw new InvalidOperationException(text,e);}
    public sealed class Conditions {public bool this[ConditionFlag flag]=>false;}
    public static class Svc { public static readonly TestHooks Hook=new();public static readonly Client ClientState=new();public static readonly Log Log=new();public static readonly Conditions Condition=new();}
}
namespace WrathCombo.Combos.PvE {public static class All {public const uint SingleTargetDPS=90000,Cease=99999;}}
namespace WrathCombo.CustomComboNS.Functions
{
    public static class Jobs {public static ECommons.ExcelServices.Job[] DisabledJobsPVE=[],DisabledJobsPVP=[];}
    public static class CustomComboFunctions {public static TimeSpan TimeMoving=>TimeSpan.Zero;public static bool InPvP()=>false;public static bool PlayerHasActionPenalty(bool x)=>false;}
}
namespace WrathCombo.Data { public class Placeholder{} }
namespace WrathCombo.Data.BattleData {public static class ContentCheck { public static bool IsInPOTD=>false; }}
namespace WrathCombo.CustomComboNS
{
    public abstract class CustomCombo {public int Preset=>1;public abstract bool TryInvoke(uint id,out uint result);}
    public sealed class TestPreset:CustomCombo { public override bool TryInvoke(uint id,out uint result){result=TestWrath.Pick(id);return id==TestWrath.Root;}}
}
namespace WrathCombo.Extensions
{
    public sealed class PresetData
    {
        public bool IsPvP=>false;public PresetData JobInfo=>this;public int RootParent=>1;
        public ECommons.ExcelServices.Job Job=>ECommons.ExcelServices.Job.BLM;public int Role=>1;public string Name=>"test";
    }
    public static class Extensions
    {
        public static PresetData Attributes(this int n)=>new();
        public static ECommons.ExcelServices.Job GetUpgradedJob(this ECommons.ExcelServices.Job j)=>j;
        public static bool MatchesPlayerJob(this int i)=>true;
    }
}
#endif
