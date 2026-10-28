using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet;
using NecoArcDesktopPet.Settings;

static class ProfileAdmissionCases
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string name) { if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name); }
    static void Pump(int ms) {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) };
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    public static int Generate(string root)
    {
        // Original vector block fixture. No media byte strings, private assets or
        // precomputed character silhouettes are used to generate this test GIF.
        Directory.CreateDirectory(root);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen()) drawing.DrawRectangle(Brushes.SlateBlue,null,new Rect(8,12,64,96));
        var bitmap=new RenderTargetBitmap(80,120,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();
        var encoder=new GifBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(root,"synthetic-block.gif")))encoder.Save(stream);
        Console.WriteLine("PASS generated original WPF block GIF solely for isolated integration fixture");return 0;
    }
    public static int Run(string root)
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var type=typeof(MainWindow);var assembly=type.Assembly;
        var pet=new MainWindow(new PetSettingsStore(Path.Combine(root,"pmv2-settings.json")));
        IDisposable? log=null;
        try {
            var player=type.GetField("_gifPlayer",F)!.GetValue(pet);
            Check(player!=null,"synthetic GIF with matching explicit profile is genuinely decoded");
            var playerType=player!.GetType();
            Check((int)playerType.GetProperty("PixelWidth")!.GetValue(player)! == 80,"decoded source canvas is independently generated width");
            var generation=playerType.GetProperty("PlaybackGeneration")!;
            var originalGeneration=generation.GetValue(player);
            var contacts=assembly.GetType("NecoArcDesktopPet.AttachmentContactProfile")!;
            var sideType=assembly.GetType("NecoArcDesktopPet.AttachmentSide")!;
            foreach(var name in new[]{"Top","Left","Right"})
                Check(!(bool)contacts.GetMethod("HasContact")!.Invoke(null,new[]{(object)"NecoPetMaterials/idle/base_idle.gif",Enum.Parse(sideType,name)})!,"missing contact denied: "+name);
            if(type.GetMethod("EnablePmv2Pilot",F) is {} enable) {
                log=(IDisposable)Activator.CreateInstance(assembly.GetType("NecoArcDesktopPet.Pilot.PilotLog")!,new object[]{Path.Combine(root,"profile-refusal.jsonl")})!;
                enable.Invoke(pet,new object[]{log});pet.Show();Pump(200);
                Check((bool)type.GetField("_pilotReady",F)!.GetValue(pet)!&&!(bool)type.GetField("_pilotStopped",F)!.GetValue(pet)!,"real PMv2 HWND admits calibrated synthetic idle through existing startup gates");
                var nativeBefore=new System.Windows.Interop.WindowInteropHelper(pet).Handle;
                var leaseField=type.GetField("_pilotAttachmentTarget",F)!;
                var lease=RuntimeHelpers.GetUninitializedObject(leaseField.FieldType);
                // A field-only nonnative sentinel is used solely to test refusal
                // before accessing a target lease. It is never read or disposed.
                leaseField.SetValue(pet,lease);
                try {
                    foreach(var side in new[]{"Top","Left","Right"}) {
                        Check(!(bool)type.GetMethod("PilotAttachmentBeginPose",F)!.Invoke(pet,new[]{Enum.Parse(sideType,side),(object)20d,20d})!,"real pose GIF cannot begin without "+side+" contact");
                        Check(!(bool)type.GetField("_pilotAttachmentPosePending",F)!.GetValue(pet)!,"missing contact publishes no pose admission");
                    }
                    var tierType=assembly.GetType("NecoArcDesktopPet.Animation.ClickTier")!;
                    foreach(var side in new[]{"Top","Left","Right"}) {
                        type.GetField("_pilotAttachmentPoseSide",F)!.SetValue(pet,Enum.Parse(sideType,side));
                        foreach(var tier in new[]{"Single","Triple"}) {
                            var fields=new[]{"_pilotClickActionId","_pilotPhysicsRevision","_pilotMotionRevision","_pilotRocketRevision","_pilotAttachmentRevision"};
                            var before=fields.Select(n=>type.GetField(n,F)!.GetValue(pet)).ToArray();
                            type.GetMethod("PilotClickResolve",F)!.Invoke(pet,new[]{Enum.Parse(tierType,tier)});
                            Check(!(bool)type.GetField("_pilotClickAction",F)!.GetValue(pet)!&&fields.Select(n=>type.GetField(n,F)!.GetValue(pet)).SequenceEqual(before),"missing "+side+" overlay refuses "+tier+" BEFORE claiming/cancelling owners");
                        }
                        type.GetField("_pilotRocketEnabled",F)!.SetValue(pet,true);
                        var revision=type.GetField("_pilotPhysicsRevision",F)!.GetValue(pet);
                        type.GetField("_pilotGravityGrounded",F)!.SetValue(pet,true);
                        type.GetMethod("PilotRocketBegin",F)!.Invoke(pet,null);
                        Check(Equals(revision,type.GetField("_pilotPhysicsRevision",F)!.GetValue(pet))&&(bool)type.GetField("_pilotGravityGrounded",F)!.GetValue(pet)!&&type.GetField("_pilotRocketTimer",F)!.GetValue(pet)==null,"missing "+side+" Rocket geometry preserves physics state and creates no timer");
                    }
                    Check(ReferenceEquals(lease,leaseField.GetValue(pet)),"refused actions retain existing relation sentinel");
                } finally {leaseField.SetValue(pet,null);}
                Check(new System.Windows.Interop.WindowInteropHelper(pet).Handle==nativeBefore,"refusals retain native HWND identity");
            } else {
                pet.Show();Pump(150);
                Check(pet.IsVisible&&double.IsFinite(pet.Width)&&double.IsFinite(pet.Height),"ordinary calibrated synthetic window has finite layout");
                Check(!(bool)type.GetMethod("TryAttachToNearbyWindow",F)!.Invoke(pet,new object?[]{null})!,"ordinary mode cannot attach when contacts are absent");
            }
            Check(Equals(originalGeneration,generation.GetValue(player)),"refused contact actions do not switch/restart playback");
            Console.WriteLine($"PASS {checks} actual synthetic-profile admission/refusal checks; no real-character or multi-display acceptance implied");return 0;
        } finally {pet.Close();log?.Dispose();app.Shutdown();}
    }
}
