using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet;

internal static partial class Program
{
    // Catches refusal leaving half-arranged state, fabricated acceptance, an
    // unbounded retry, or a stale callback publishing after a successor owner.
    static void RejectionAtomicity(MainWindow pet,string mode,string directory)
    {
        var image=(Image)Field(pet,"PetImage")!;var surface=(Canvas)Field(pet,"PetSurface")!;
        var player=Field(pet,"_gifPlayer")!;var source=(HwndSource)Field(pet,"_pilotSource")!;
        var hwnd=new WindowInteropHelper(pet).Handle;
        bool Stopped()=>(bool)Field(pet,"_pilotStopped")!;
        if(mode is "C-idle-missing" or "C-fit-fallback") {
            // Real Fit refusal via an oversized requested return, without editing
            // monitor topology or replacing a geometry/checkpoint result.
            var trusted=Field(pet,"_pilotRect")!;var placement=Field(pet,"_crossPlacementId");
            if(mode=="C-idle-missing")LateSet(pet,"_effectiveBaseIdlePath","NecoPetMaterials/absent-phase30c-idle.gif");
            Check((bool)Call(player,"TrySwitchTo",AdmissionCaper,true)!,"select real candidate before corridor refusal");
            LateSet(pet,"_pilotVisualReturn",Activator.CreateInstance(trusted.GetType(),new object[]{0d,0d,100000d,100000d}));
            for(int i=0;i<50&&!Stopped()&&!LateSettled(pet);i++)Pump(20);
            if(mode=="C-fit-fallback") {
                Check(!Stopped()&&LateSettled(pet)&&Equals(Field(pet,"_pilotVisualSizePath"),LateIdle)&&
                    Equals(LateProp(player,"ResourcePath"),LateIdle),"real Fit denial falls back to coherent admitted Idle");
                Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Check(!Stopped(),"watch survives ordinary corridor fallback");return;
            }
            // PilotStop closes HWND on the dispatcher; an unreadable destroyed
            // HWND is not evidence of native movement. Prove no placement request.
            Check(Stopped()&&Equals(placement,Field(pet,"_crossPlacementId"))&&Equals(trusted,Field(pet,"_pilotRect")),"missing Idle after real Fit refusal explicitly stops without native commit");
            return;
        }
        object Presentation()=>new {
            surface.Width,surface.Height,imageWidth=image.Width,imageHeight=image.Height,
            left=Canvas.GetLeft(image),top=Canvas.GetTop(image),transform=image.RenderTransform.Value,
            origin=image.RenderTransformOrigin,windowWidth=pet.Width,windowHeight=pet.Height,
            anchor=Field(pet,"_pilotVisualAnchorLocal"),visible=Field(pet,"_pilotVisualVisibleBounds"),
            baseSize=Field(pet,"_pilotVisualBaseSize"),path=Field(pet,"_pilotVisualSizePath"),
            height=Field(pet,"_pilotVisualSizeHeight"),dpi=Field(pet,"_pilotVisualSizeDpi"),union=Field(pet,"_pilotVisualSizeUnion"),
            calibrated=Field(pet,"_pilotVisualCalibrated")};
        var before=Presentation();var bitmap=image.Source;var arranged=Field(pet,"_pilotVisualArrangedFrame");
        var accepted=Field(pet,"_crossAcceptedId");var owner=Call(pet,"CrossOwnerSnapshot");
        GetWindowRect(hwnd,out var nativeBefore);
        var attempts=0;var restored=false;Action? stale=null;
        Action Body(DispatcherOperation op)=>(Action)typeof(DispatcherOperation).GetField("_method",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(op)!;
        DispatcherHookEventHandler observe=(_,_)=> {
            if(attempts!=1||restored||Stopped()||!Equals(Field(pet,"_pilotVisualSizePath"),LateIdle))return;
            if(Field(pet,"_pilotVisualPending") is not DispatcherOperation pending)return;
            restored=true;stale=Body(pending);
            LateSnapshot(pet,Path.Combine(directory,"rollback.jsonl"),"before-retry");
            Check(before.Equals(Presentation())&&ReferenceEquals(bitmap,image.Source)&&ReferenceEquals(arranged,Field(pet,"_pilotVisualArrangedFrame")),
                "rollback restores full presentation, transform, bounds, cache, frame and window");
            Check(LateReady(pet)&&Equals(accepted,Field(pet,"_crossAcceptedId"))&&Field(pet,"_crossInFlightId")==null,
                "rollback independently decidable without accepting rejected request");
            Check((bool)Field(pet,"_pilotVisualPresentationDeferred")!&&(bool)Field(pet,"_pilotVisualResume")!&&
                (bool)LateProp(player,"PilotPresentationHeld")!&&!(bool)LateProp(player,"PilotFrameTimerEnabled")!&&
                Field(pet,"_pilotVisualHeldFrame")==null&&Field(pet,"_pilotVisualHeldIdentity")==null&&
                Field(pet,"_pilotVisualRetainedViewport")==null&&Field(pet,"_pilotVisualWait")==null,
                "rollback restores deferred/resume/held/timer/retained viewport flags without resurrecting gesture ownership");
            Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Check(!Stopped(),"watch accepts restored state before retry");
            if(mode=="C-successor")Check((bool)Call(player,"TrySwitchTo",LatePhone,true)!,"successor playback queued before retry");
            if(mode=="C-cancel")Call(pet,"PilotCancelGesture","cancel bounded visual retry");
        };
        HwndSourceHook refuse=(nint h,int message,nint w,nint l,ref bool handled)=> {
            if(message==0x0046&&(int)Field(pet,"_pilotPlacementDepth")!>0) {
                attempts++;
                if(mode!="C-publish-successor"&&(attempts==1||mode is "C-repeat" or "C-native-diverged")) {
                    var pos=System.Runtime.InteropServices.Marshal.PtrToStructure<AdmissionWindowPos>(l);
                    if(mode=="C-native-diverged"){pos.X=nativeBefore.Left+3;pos.Y=nativeBefore.Top;pos.Width=nativeBefore.Right-nativeBefore.Left;pos.Height=nativeBefore.Bottom-nativeBefore.Top;}
                    else pos.Flags|=3;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos,l,false);
                }
                if(attempts==1&&mode=="C-native-successor")
                    Check((bool)Call(player,"TrySwitchTo",LatePhone,true)!,"native hook selects successor playback");
                if(attempts==1&&mode=="C-native-cancel")
                    Call(pet,"PilotCancelGesture","real native-hook cancellation");
            }
            if(message==0x0047&&attempts==1&&mode=="C-publish-successor")
                Check((bool)Call(player,"TrySwitchTo",LatePhone,true)!,"native placement succeeds before visual publication is superseded");
            return 0;
        };
        source.AddHook(refuse);pet.Dispatcher.Hooks.OperationStarted+=observe;
        try {
            Check((bool)Call(player,"TrySwitchTo",AdmissionCaper,true)!,"candidate Caper selected");
            for(int i=0;i<100&&!Stopped()&&!LateSettled(pet);i++)Pump(20);
        } finally {source.RemoveHook(refuse);pet.Dispatcher.Hooks.OperationStarted-=observe;}
        LateSnapshot(pet,Path.Combine(directory,"rollback.jsonl"),"terminal");
        if(mode is "C-repeat" or "C-native-diverged") {
            Check(Stopped(),"unsafe or exhausted refusal stops explicitly");
            Check(attempts==(mode=="C-repeat"?2:1),"retry budget never renews and diverged native is never retried");
            return;
        }
        if(mode is "C-native-successor" or "C-publish-successor" or "C-native-cancel") {
            var finalPath=mode=="C-native-cancel"?AdmissionCaper:LatePhone;
            Check(!Stopped()&&LateSettled(pet)&&Equals(Field(pet,"_pilotVisualSizePath"),finalPath),"native reentry successor owns final admitted frame");
            if(mode=="C-native-cancel")Check(!Equals(owner,Call(pet,"CrossOwnerSnapshot")),"native cancellation revokes old owner");
            Check(!restored,"superseded native completion cannot roll back successor");
            Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Check(!Stopped(),"watch accepts successor publication");return;
        }
        Check(restored,"rejected placement visibly restores old state before bounded retry");
        Check(!Stopped()&&LateSettled(pet),"refusal converges with no stranded dirty/pending/wait");
        if(mode=="C-cancel")Check(!Equals(owner,Call(pet,"CrossOwnerSnapshot")),"real cancellation revoked retry owner");
        if(mode=="C-once") {
            Check(attempts==2&&Equals(Field(pet,"_pilotVisualSizePath"),AdmissionCaper),"one refusal then exactly one successful retry");
            var current=(BitmapSource)image.Source;
            Check(Math.Abs(image.Width/image.Height-(double)current.PixelWidth/current.PixelHeight)<1e-9,"new GIF aspect matches admitted box");
        }
        if(stale!=null) {
            var current=image.Source;var rect=Field(pet,"_pilotRect");var id=Field(pet,"_crossPlacementId");
            stale();
            Check(ReferenceEquals(current,image.Source)&&Equals(rect,Field(pet,"_pilotRect"))&&Equals(id,Field(pet,"_crossPlacementId")),
                "completed or superseded retry delegate cannot replay placement");
        }
        Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Check(!Stopped(),"watch survives converged rejection");
    }
}
