using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NecoArcDesktopPet;
using NecoArcDesktopPet.Settings;

internal static partial class Program
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception("FAIL REAL WPF OS: "+why);Console.WriteLine("PASS "+why);}
    static object? Field(object target,string name)=>target.GetType().GetField(name,Flags)!.GetValue(target);
    static object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags)!.Invoke(target,args);
    static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
    [StructLayout(LayoutKind.Sequential)] struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    const string LateIdle="NecoPetMaterials/idle/base_idle.gif";
    const string LatePhone="NecoPetMaterials/idle/watch_phone.gif";
    static object? LateProp(object target,string name)=>target.GetType().GetProperty(name,Flags)!.GetValue(target);
    static void LateSet(object target,string name,object? value)=>target.GetType().GetField(name,Flags)!.SetValue(target,value);
    static bool LateReady(MainWindow pet)=>(bool)LateProp(Call(pet,"CrossCheckpoint")!,"Ready")!;
    static bool LateSettled(MainWindow pet)=>!(bool)Field(pet,"_pilotStopped")! &&
        !(bool)Field(pet,"_pilotTopologyHold")! && Field(pet,"_pilotVisualPending")==null &&
        Field(pet,"_pilotVisualWait")==null && !(bool)Field(pet,"_pilotVisualDirty")! &&
        (!(bool)Field(pet,"_crossScreen")! || LateReady(pet));
    static void LateUntil(Func<bool> predicate,string name)
    {
        for(int i=0;i<100&&!predicate();i++)Pump(20);
        Check(predicate(),"SETUP "+name);
    }
    static void LateSnapshot(MainWindow pet,string path,string stage)
    {
        var image=(Image)Field(pet,"PetImage")!;var surface=(Canvas)Field(pet,"PetSurface")!;
        var cp=Call(pet,"CrossCheckpoint")!;
        var nativeRead=GetWindowRect(new WindowInteropHelper(pet).Handle,out var native);
        File.AppendAllText(path,JsonSerializer.Serialize(new {stage,
            checkpoint=cp,ready=LateProp(cp,"Ready"),decidable=LateProp(cp,"Decidable"),
            layout=new {windowMeasure=pet.IsMeasureValid,windowArrange=pet.IsArrangeValid,
                surfaceMeasure=surface.IsMeasureValid,surfaceArrange=surface.IsArrangeValid,
                imageMeasure=image.IsMeasureValid,imageArrange=image.IsArrangeValid},
            visual=new {dirty=Field(pet,"_pilotVisualDirty"),pending=Field(pet,"_pilotVisualPending")!=null,
                wait=Field(pet,"_pilotVisualWait")!=null,revision=Field(pet,"_pilotVisualRevision"),
                sizePath=Field(pet,"_pilotVisualSizePath"),path=LateProp(Field(pet,"_gifPlayer")!,"ResourcePath")},
            nativeRead,native=new {native.Left,native.Top,native.Right,native.Bottom},
            trusted=Field(pet,"_pilotRect"),expected=Field(pet,"_crossExpected"),
            imageExplicit=new {image.Width,image.Height},imageActual=new {image.ActualWidth,image.ActualHeight},
            surfaceExplicit=new {surface.Width,surface.Height},layoutDepth=Field(pet,"_crossLayoutDepth"),
            stopped=Field(pet,"_pilotStopped")})+Environment.NewLine);
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if (args.Length != 3 || args[1] != "--visual-admission")
                throw new ArgumentException("Usage: <fresh evidence directory under .local> --visual-admission R1|R2|R3-3|R3-30|R4|C-once|C-repeat|C-native-diverged|C-idle-missing|C-fit-fallback|C-successor|C-cancel|C-native-successor|C-native-cancel|C-publish-successor");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NECO_CHARACTER_PROFILE")))
                throw new InvalidOperationException("Requires your own compatible embedded GIFs and explicit Character Profile; unavailable in an unmodified source-only checkout.");
            return VisualAdmission(args);
        } catch (Exception e) { Console.Error.WriteLine(e.GetBaseException()); return 1; }
        finally { Application.Current?.Shutdown(); }
    }
}
