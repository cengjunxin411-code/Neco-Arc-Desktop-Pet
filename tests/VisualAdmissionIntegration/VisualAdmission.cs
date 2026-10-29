using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet;
using NecoArcDesktopPet.Settings;

internal static partial class Program
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct AdmissionWindowPos { public nint Hwnd,After;public int X,Y,Width,Height;public uint Flags; }
    const string AdmissionRotation="NecoPetMaterials/click/rotation.gif";
    const string AdmissionCaper="NecoPetMaterials/angry/caper.gif";
    const string AdmissionAngry="NecoPetMaterials/angry/angry1.gif";
    static int VisualAdmission(string[] args)
    {
        var mode=args[Array.IndexOf(args,"--visual-admission")+1];
        var directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
        var logPath=Path.Combine(directory,"events.jsonl");
        Check(!File.Exists(logPath),"fresh admission evidence");
        var assembly=typeof(MainWindow).Assembly;
        Console.WriteLine("ASSEMBLY "+assembly.Location+" SHA256="+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))));
        _=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var app=assembly.GetType("NecoArcDesktopPet.Pilot.PilotApplication")!;
        foreach(var name in new[]{"CrossScreen","SeamHandoff","Rocket","Attachment","RestSleep","RandomIdle","AutonomousMovement","Physics","Failed"})
            app.GetProperty(name,Flags)!.SetValue(null,name=="CrossScreen");
        var settings=PetSettingsDefaults.Create();settings.Movement.Enabled=false;
        settings.Rest.AmbientEnabled=settings.Sleep.AmbientEnabled=false;settings.Voice.Enabled=false;
        var logType=assembly.GetType("NecoArcDesktopPet.Pilot.PilotLog")!;
        var log=(IDisposable)Activator.CreateInstance(logType,Flags,null,new object?[]{logPath},null)!;
        var pet=new MainWindow(settings);Exception? failure=null;
        try {
            Call(pet,"EnablePmv2Pilot",log);pet.Show();
            LateUntil(()=> (bool)Field(pet,"_pilotReady")!&&LateSettled(pet),"live admission HWND ready");
            // Invoke the production watch deterministically at the adverse interleaving.
            // No checkpoint result, layout, native rectangle or safety budget is mocked.
            ((DispatcherTimer)Field(pet,"_pilotWatch")!).Stop();
            Call(pet,"PilotSetPetHeight",170d,false);LateUntil(()=>LateSettled(pet),"170 DIP admitted");
            var surface=(Canvas)Field(pet,"PetSurface")!;var image=(Image)Field(pet,"PetImage")!;
            var player=Field(pet,"_gifPlayer")!;var hwnd=new WindowInteropHelper(pet).Handle;
            object? Prop(string name)=>LateProp(player,name);
            string Gif()=>(string)Prop("ResourcePath")!;
            bool B(string name)=>(bool)Field(pet,name)!;
            bool PlayerB(string name)=>(bool)Prop(name)!;
            bool Sizes()=>(bool)LateProp(Call(pet,"CrossCheckpoint")!,"Sizes")!;
            void Snapshot(string stage) {
                LateSnapshot(pet,Path.Combine(directory,"snapshots.jsonl"),stage);
                var bitmap=(BitmapSource)image.Source;
                File.AppendAllText(Path.Combine(directory,"presentation.jsonl"),JsonSerializer.Serialize(new {
                    stage,dpi=GetDpiForWindow(hwnd),gif=Gif(),playback=Prop("PlaybackGeneration"),frame=Prop("PilotFrameIndex"),
                    bitmap=new{bitmap.PixelWidth,bitmap.PixelHeight},arranged=ReferenceEquals(image.Source,Field(pet,"_pilotVisualArrangedFrame")),
                    deferred=B("_pilotVisualPresentationDeferred"),resume=B("_pilotVisualResume"),
                    held=PlayerB("PilotPresentationHeld"),timer=PlayerB("PilotFrameTimerEnabled"),
                    gesture=Field(pet,"_pilotGesture"),clickHeld=Field(pet,"_pilotClicks") is {} clicks?LateProp(clicks,"Held"):null
                })+Environment.NewLine);
            }
            void Coherent(string name) {
                pet.UpdateLayout();Check(Sizes(),name+" actual CrossCheckpoint Sizes");
                var bitmap=(BitmapSource)image.Source;
                Check(Math.Abs(image.Width/image.Height-(double)bitmap.PixelWidth/bitmap.PixelHeight)<1e-9,
                    name+" source and explicit box have matching aspect");
                Check(Math.Abs(image.ActualWidth-image.Width)<1e-6&&Math.Abs(image.ActualHeight-image.Height)<1e-6,
                    name+" WPF actual image equals explicit box");
            }
            void Clean(string path) {
                LateUntil(()=>LateSettled(pet),"settled "+path);
                Check(Gif()==path&&Equals(Field(pet,"_pilotVisualSizePath"),path),"current clip admitted "+path);
                Check(!B("_pilotVisualPresentationDeferred")&&!B("_pilotVisualResume")&&!PlayerB("PilotPresentationHeld")&&
                    Field(pet,"_pilotVisualHeldFrame")==null&&!B("_pilotVisualDirty")&&
                    Field(pet,"_pilotVisualPending")==null&&Field(pet,"_pilotVisualWait")==null,"no visual state leak "+path);
                Coherent(path);Call(pet,"PilotWatchTick",null!,EventArgs.Empty);
                Check(!B("_pilotStopped"),"production watch survives "+path);
            }
            void Switch(string path) {Check((bool)Call(player,"TrySwitchTo",path,true)!,"real GIF switch "+path);Call(player,"Resume");Clean(path);}
            void Grab() {
                pet.Activate();pet.Focus();
                Check(surface.CaptureMouse()&&surface.IsMouseCaptured,"real WPF mouse capture");
                LateSet(pet,"_crossActive",true);Call(pet,"PilotClickBegin");
                Check((bool)LateProp(Field(pet,"_pilotClicks")!,"Held")!,"controlled admitted click owner holds gesture");
            }
            void Release() {
                // Deterministic no-move Up; native capture is released by the real adapter.
                LateSet(pet,"_crossActive",false);
                surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
                Check(!surface.IsMouseCaptured&&!(bool)LateProp(Field(pet,"_pilotClicks")!,"Held")!,"production Up revokes capture and click hold");
            }
            void Triple() {
                Call(pet,"PilotClickResolve",Enum.Parse(assembly.GetType("NecoArcDesktopPet.Animation.ClickTier")!,"Triple"));
                Clean(AdmissionRotation);
            }
            Action QueuedBody() {
                var pending=(DispatcherOperation)Field(pet,"_pilotVisualPending")!;
                return (Action)typeof(DispatcherOperation).GetField("_method",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pending)!;
            }
            if(mode=="R1") {
                Triple();Grab();var old=image.Source;var width=image.Width;var height=image.Height;
                GetWindowRect(hwnd,out var before);var placement=Field(pet,"_crossPlacementId");var gesture=Field(pet,"_pilotGesture");
                var rotationCompletion=(Action)Field(pet,"_pilotClickCompletionBody")!;
                Call(pet,"PilotClickBeginCaper",Field(pet,"_pilotClickActionId")!,Field(pet,"_pilotClickRotation")!);
                Check(Gif()==AdmissionCaper,"Caper playback selected during capture");
                pet.UpdateLayout();var sizes=Sizes();Snapshot("captured-caper-before-watch");
                Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Snapshot("captured-caper-after-watch");
                // Let RED preserve the real watchdog result before failing the invariant.
                Check(sizes,"R1 held Rotation box and frame must keep Sizes=true");
                Check(!B("_pilotStopped"),"R1 captured clip switch survives actual watchdog");
                Check(ReferenceEquals(old,image.Source)&&image.Width==width&&image.Height==height,"R1 admitted Rotation frame and box retained");
                GetWindowRect(hwnd,out var during);
                Check(before.Equals(during)&&Equals(placement,Field(pet,"_crossPlacementId"))&&Equals(gesture,Field(pet,"_pilotGesture")),
                    "R1 capture switch neither places native HWND nor changes gesture owner");
                Check(PlayerB("PilotPresentationHeld")&&!PlayerB("PilotFrameTimerEnabled"),"unadmitted Caper cannot play");
                Pump(180);Coherent("captured Caper");
                Check(ReferenceEquals(old,image.Source),"held presentation remains Rotation through timer interval");
                Release();Clean(AdmissionCaper);Snapshot("released-caper-admitted");
                Check(!ReferenceEquals(old,image.Source)&&(image.Width!=width||image.Height!=height),"release admits Caper bitmap and new footprint");
                var generation=Prop("PlaybackGeneration");rotationCompletion();
                Check(Equals(generation,Prop("PlaybackGeneration"))&&Gif()==AdmissionCaper,"stale Rotation completion cannot overwrite Caper");
                var frame=Prop("PilotFrameIndex");
                for(int i=0;i<25&&Equals(frame,Prop("PilotFrameIndex"));i++)Pump(20);
                Check(!Equals(frame,Prop("PilotFrameIndex"))&&PlayerB("PilotFrameTimerEnabled"),"Caper resumes real GIF playback");
                LateUntil(()=>Gif()==AdmissionAngry&&LateSettled(pet),"captured click upgrades Caper to Angry1 at loop boundary");Clean(AdmissionAngry);
                for(int i=0;i<300&&(B("_pilotClickAction")||Gif()!=LateIdle||!LateSettled(pet));i++)Pump(20);
                Check(!B("_pilotClickAction")&&Gif()==LateIdle&&LateSettled(pet),"Angry1 naturally completes to Idle");Clean(LateIdle);
            } else if(mode=="R2") {
                foreach(var captured in new[]{false,true})
                foreach(var pair in new[]{(AdmissionRotation,AdmissionCaper),(AdmissionCaper,AdmissionAngry),(LateIdle,LatePhone),(LateIdle,LateIdle)}) {
                    Switch(pair.Item1);var old=image.Source;GetWindowRect(hwnd,out var before);
                    if(captured)Grab();
                    Check((bool)Call(player,"TrySwitchTo",pair.Item2,true)!,"matrix destination selected");Call(player,"Resume");
                    if(captured) {
                        Coherent("matrix capture "+pair);Call(pet,"PilotWatchTick",null!,EventArgs.Empty);
                        GetWindowRect(hwnd,out var during);Check(before.Equals(during),"matrix capture never moves/resizes HWND");
                        if(pair.Item1!=pair.Item2)Check(ReferenceEquals(old,image.Source),"matrix retains admitted source before release");
                        Snapshot("matrix-held-"+Path.GetFileName(pair.Item2));Release();
                        // These direct clip switches have no click action; cancel aggregation only.
                        Call(pet,"PilotClickStopTimer");Call(Field(pet,"_clickResolver")!,"Cancel");
                    }
                    Clean(pair.Item2);Snapshot("matrix-settled-"+captured+"-"+Path.GetFileName(pair.Item2));
                }
            } else if(mode is "R3-3" or "R3-30") {
                var delta=mode=="R3-3"?3:30;GetWindowRect(hwnd,out var before);
                Check(SetWindowPos(hwnd,0,before.Left+delta,before.Top,0,0,0x15),"real external native overwrite +"+delta);
                Call(pet,"PilotWatchTick",null!,EventArgs.Empty);Snapshot("external-overwrite-after-watch");
                Check(B("_pilotStopped"),"external overwrite still invokes Safety Stop");
            } else if(mode=="R4") {
                Triple();Grab();Call(pet,"PilotClickDragWins");
                var held=image.Source;Check(Field(pet,"_pilotVisualHeldFrame")!=null,"drag interruption holds admitted Rotation frame");
                Check((bool)Call(player,"TrySwitchTo",AdmissionCaper,true)!,"new clip while admitted held frame exists");Call(player,"Resume");
                Coherent("existing held frame");Check(ReferenceEquals(held,image.Source),"existing admitted held frame survives new clip");
                Release();Clean(LateIdle);
                Triple();Grab();Call(pet,"PilotClickBeginCaper",Field(pet,"_pilotClickActionId")!,Field(pet,"_pilotClickRotation")!);
                var canceledCaper=(Action)Field(pet,"_pilotClickCompletionBody")!;
                Call(pet,"PilotCancelGesture","higher priority cancellation during deferred Caper");
                Clean(LateIdle);var generation=Prop("PlaybackGeneration");canceledCaper();
                Check(Gif()==LateIdle&&Equals(generation,Prop("PlaybackGeneration")),"canceled Caper completion cannot republish");
                Triple();Call(pet,"PilotClickBeginCaper",Field(pet,"_pilotClickActionId")!,Field(pet,"_pilotClickRotation")!);Clean(AdmissionCaper);
                Grab();Release();Check((bool)LateProp(Field(pet,"_pilotAngry")!,"EscalateToAngry1")!,"new Caper click retains Angry owner and records upgrade");
                LateUntil(()=>Gif()==AdmissionAngry&&LateSettled(pet),"Caper upgrades after complete loops");Clean(AdmissionAngry);
                Call(pet,"PilotClickCancel","higher priority owner");Clean(LateIdle);
                Check((bool)Call(player,"TrySwitchTo",LatePhone,true)!,"queue older visual playback");var stale=QueuedBody();
                Check((bool)Call(player,"TrySwitchTo",AdmissionCaper,true)!,"queue newer visual playback");
                var newer=Field(pet,"_pilotVisualPending");generation=Prop("PlaybackGeneration");var revision=Field(pet,"_pilotVisualRevision");
                stale();Check(ReferenceEquals(newer,Field(pet,"_pilotVisualPending"))&&Equals(revision,Field(pet,"_pilotVisualRevision"))&&
                    Equals(generation,Prop("PlaybackGeneration")),"forced aborted visual callback cannot clear or publish newer transaction");Clean(AdmissionCaper);
                Check((bool)Call(player,"TrySwitchTo",LatePhone,true)!,"queue callback before owner cancellation");stale=QueuedBody();
                Call(pet,"PilotCancelGesture","higher priority revokes queued visual");var source=image.Source;var trusted=Field(pet,"_pilotRect");
                stale();Check(ReferenceEquals(source,image.Source)&&Equals(trusted,Field(pet,"_pilotRect")),"forced canceled-owner callback cannot publish or place");
                Clean(LatePhone);Snapshot("interruptions-clean");
            } else if(mode=="F3-place") {
                RejectionAtomicity(pet,"C-once",directory);
            } else if(mode.StartsWith("C-")) {
                RejectionAtomicity(pet,mode,directory);
            } else throw new ArgumentException("Unknown admission mode "+mode);
        } catch(Exception e) {failure=e;}
        finally {try{pet.Close();Pump(60);}finally{log.Dispose();}}
        var rows=File.ReadAllLines(logPath).Select(line=>JsonDocument.Parse(line)).ToArray();
        try {
            int Count(string kind)=>rows.Count(r=>r.RootElement.GetProperty("kind").GetString()==kind);
            Console.WriteLine($"EVENT COUNTS safety-stop={Count("safety-stop")} stable-late-mismatch={Count("stable-late-mismatch")}");
            if(failure!=null)throw failure;
            var expected=mode.StartsWith("R3-")?1:0;
            var stops=mode is "C-repeat" or "C-native-diverged" or "C-idle-missing"?1:expected;
            Check(Count("safety-stop")==stops&&Count("stable-late-mismatch")==expected,"exact watchdog event counts");
            if(mode.StartsWith("R3-")) {
                var mismatch=rows.Single(r=>r.RootElement.GetProperty("kind").GetString()=="stable-late-mismatch").RootElement.GetProperty("data");
                Check(!mismatch.GetProperty("nativeNear").GetBoolean(),"true positive proves native/trusted divergence");
            }
        } finally {foreach(var row in rows)row.Dispose();}
        Console.WriteLine($"PASS {mode}: {checks} checks; CONTROLLED REAL WPF/HWND/GIF/CAPTURE; HUMAN HARDWARE PENDING");return 0;
    }
}
