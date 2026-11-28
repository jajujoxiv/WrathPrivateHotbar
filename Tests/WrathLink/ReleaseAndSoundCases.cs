using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using WrathCombo.Core;
using WrathCombo.Services;
using WrathPrivateHotbar;

internal static unsafe class ReleaseAndSoundCases
{
    internal static void KeyUp(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        foreach (bool cast in new[] { true, false })
        {
            using var clock = new SimulationClock(); using var game = new Simulation();
            using var provider = new ActionReplacer(); Service.ActionReplacer = provider;
            File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),
                """{"Schema":3,"Bar":8,"PulseMin":80,"PulseMax":81,"EarlyPreview":true}""");
            game.Bind(8,0,0x41); game.Place(2,0,300); game.Place(6,4,400); game.Place(9,5,500);
            game.ReAction.IsLoaded = true; DesktopInput.DesktopKeys = [];
            using var plugin = load(game); game.Tick();
            DesktopInput.DesktopKeys.Add(0x41); UIInputData.Down.Add(0x41);
            TestWrath.Next = 300; game.DeferNextCast = cast; game.PressSlot(8,0);
            var am=ActionManager.Instance();
            if (cast) { am->CastActionId=am->CastSpellId=300; am->CastTimeTotal=2; am->CastTargetId=game.Target; }
            else { am->Gcd->IsActive=true; am->Gcd->ActionId=300; am->Gcd->Total=2.5f; }
            game.Tick(); game.RenderGui();
            var pulses=AddonActionBarBase.Pulses;
            check(pulses.SequenceEqual([(2,0)]), "missing physical press before key-up");
            int uses=game.Dispatches;
            TestWrath.Next=400; DesktopInput.DesktopKeys.Clear(); // deliberately stale game key for 150ms
            foreach(var t in new[]{10,100,160,350,800,1400,1800})
            {
                if(t==160) UIInputData.Down.Clear();
                if(t==350) TestWrath.Next=500;
                long now=Environment.TickCount64; clock.Advance((int)(100000+t-now));
                if(cast) am->CastTimeElapsed=t/1000f; else am->Gcd->Elapsed=t/1000f;
                game.Tick(); game.RenderGui();
            }
            check(pulses.Count==1 && UIGlobals.Sounds==1 && game.Dispatches==uses,
                "release continued into a live or predicted next skill during cast/GCD");
            check(ImGui.Bar.Images.All(i=>i.Texture!=Simulation.PressTexture), "private button was still illuminated without contact");
            if(cast) { am->CastTimeElapsed=am->CastTimeTotal; game.DirectUse(300); }
            clock.Advance(100); game.Tick();
            check(pulses.Count==1 && UIGlobals.Sounds==1, "a late cast receipt restarted a released press");
            DesktopInput.DesktopKeys.Add(0x41); clock.Advance(100); game.Tick();
            check(pulses.Count==2 && pulses[^1]==(9,5) && UIGlobals.Sounds==2,
                "actual repress did not immediately follow the new candidate");
        }
    }

    internal static void SourceAudio(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock = new SimulationClock(); using var game = new Simulation();
        using var provider=new ActionReplacer(); Service.ActionReplacer=provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),"""{"Schema":3,"Bar":8}""");
        game.Place(3,4,300);
        using var plugin=load(game); game.Tick();
        var source=game.Huds+7;
        AddonActionBarBase.NativeSound=true;
        source->PulseActionBarSlot(0);
        check(UIGlobals.Played.Count==0, "hidden source pulse produced its own native click");
        AddonActionBarBase.RoutedSound!(source,12); // native UI outside the pulse callback
        game.BeforeNativePointer=()=>UIGlobals.PlaySoundEffect(12); // global sound inside the selected dispatch
        game.PressSlot(8,0); game.Tick(); game.RenderGui();
        check(UIGlobals.Played.SequenceEqual([12u]) && AddonActionBarBase.Pulses.SequenceEqual([(3,4)]),
            "source, synthetic ordinary animation and confirmed presentation did not produce exactly one click");
        AddonActionBarBase.RoutedSound!(source,62); UIGlobals.PlaySoundEffect(12);
        check(UIGlobals.Played.SequenceEqual([12u,62u,12u]), "unrelated UI click or a different source sound was silenced");
        game.Command("config");
        ImGui.ToggleLabel="隠した元HBの押下SEを消す"; game.RenderGui();
        AddonActionBarBase.RoutedSound!(source,12);
        check(UIGlobals.Played.Count==4, "explicit source-audio opt out did not restore its click");
        ImGui.ToggleLabel="隠した元HBの押下SEを消す"; game.RenderGui();
        AddonActionBarBase.Paint=false; clock.Advance(320); game.Tick();
        game.PressSlot(8,0); game.Tick(); clock.Advance(40); game.Tick();
        check(UIGlobals.Played.Count==4 && AddonActionBarBase.Pulses.Count==2,
            "native bit without visible artwork still produced a click");
        clock.Advance(330); game.Tick(); AddonActionBarBase.Paint=true;
        game.PressPointer(3,4); game.Tick();
        check(UIGlobals.Played.Count==5, "a real ordinary click and a confirmed mirrored click doubled");
    }

    internal static void NativeMaterial(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock=new SimulationClock(); using var game=new Simulation();
        using var provider=new ActionReplacer(); Service.ActionReplacer=provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),"""{"Schema":3,"Bar":8,"Size":48}""");
        var grid=game.InstallOuterGrip(8,1);
        using var plugin=load(game); game.Tick(); game.RenderGui();
        check(ImGui.Bar.Images.All(i=>i.Texture!=900001), "idle frame lit the private icon");
        game.Huds[7].PulseActionBarSlot(1); // native press arrives between Framework.Update and UiBuilder.Draw
        grid->AtkResNode.Visible = true;
        game.RenderGui();
        var nine=ImGui.Bar.Images.Where(i=>i.Texture==900001).ToArray();
        var at=ImGui.Targets["1"].At;
        check(nine.Length==9 && nine[0].Min==at && nine[^1].Max==at+new Vector2(48) &&
            nine[0].Uv0==new Vector2(.25f,.125f) && nine[^1].Uv1==new Vector2(.4375f,.3125f) &&
            ImGui.Bar.Images.All(i=>i.Texture!=Simulation.PressTexture) && UIGlobals.Sounds==0,
            "hidden selected HUD's native nine-part press frame was not drawn on the private icon");
    }

    internal static void Surface(Func<Simulation,Plugin> load, Action<bool,string> check)
    {
        using var clock=new SimulationClock(); using var game=new Simulation();
        using var provider=new ActionReplacer(); Service.ActionReplacer=provider;
        File.WriteAllText(Path.Combine(game.GetPluginConfigDirectory(),"preferences-2.0.0.json"),"""{"Schema":3,"Bar":8,"Pulse":false,"Size":48}""");
        int first=(int)FFXIVClientStructs.FFXIV.Client.System.Input.InputId.HOTBAR_1_1 + 7*12;
        UIInputData.Remap[first]=first+1; UIInputData.Remap[first+1]=first;
        game.Bind(8,1,0x41); // native lookup maps this record to logical slot 0
        using var plugin=load(game); game.Tick(); game.RenderGui();
        var idle=ImGui.Targets["0"]; UIInputData.Down.Add(0x41);
        game.Tick(); game.RenderGui();
        var layer=ImGui.Bar.Images.Single(i=>i.Texture==Simulation.PressTexture);
        check(layer.Min==idle.At && layer.Max==idle.At+idle.Size && (layer.Color>>24)>=220 && ImGui.Front.Images.Count==0,
            "press overlay missed the selected cell or appeared on another capture layer");
        var pixels=game.Mask;
        int side=(int)Math.Sqrt(pixels.Length/4);
        foreach(var background in new[] { new Vector3(27,37,87), new Vector3(216,223,218) })
        {
            int visible=0; int preserved=0;
            for(int y=0;y<side;y++) for(int x=0;x<side;x++)
            {
                int i=4*(y*side+x);
                float a=(pixels[i+3]/255f)*((layer.Color>>24)/255f);
                var composited=Vector3.Lerp(background,new(pixels[i],pixels[i+1],pixels[i+2]),a);
                if(Vector3.Distance(composited,background)>26) visible++;
                if(x>side*.32 && x<side*.68 && y>side*.32 && y<side*.68 && composited==background) preserved++;
            }
            check(visible>side*side*.16 && preserved>side*side*.1 && pixels[3]==0,
                $"pressed pixels did not read over {background}: changed={visible}, clear center={preserved}");
        }
        UIInputData.Down.Clear(); game.Tick(); clock.Advance(350); game.RenderGui();
        check(ImGui.Bar.Images.All(i=>i.Texture!=Simulation.PressTexture) && UIGlobals.Sounds==0,
            "release did not finish or pseudo-only display emitted an SE");
    }
}
