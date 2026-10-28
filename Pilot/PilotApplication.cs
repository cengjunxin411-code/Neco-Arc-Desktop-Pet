using System;
using System.Windows;
using System.Windows.Threading;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Pilot;

internal static class PilotApplication
{
    internal static PilotLog Log { get; private set; }=null!;
    internal static int? DurationSeconds { get; private set; }
    internal static bool Failed { get; set; }
    internal static bool MissingIdleGif { get; set; }
    internal static bool CrossScreen { get; set; }
    internal static bool SeamHandoff { get; set; }
    internal static bool Rocket { get; set; }
    internal static bool Attachment { get; set; }
    internal static bool RestSleep { get; set; }
    internal static bool RandomIdle { get; set; }
    internal static bool AutonomousMovement { get; set; }
    internal static bool Physics { get; set; }
    internal static PetSettings CreateSettings() => PetSettingsDefaults.Create();

    internal static PetSettingsStore CreateSettingsStore()
    {
        var path = SourceOnlyPaths.SettingsFile("pmv2-settings.json");
        return new PetSettingsStore(path, SettingsPersistenceDiagnostics.FromEnvironment());
    }

    internal static MainWindow CreateWindow(PetSettingsStore store)
    {
        var startup = store.Load(CreateSettings());
        var window = new MainWindow(startup, store);
        window.PilotPhysicsRestoreMode(startup.PhysicsMode);
        return window;
    }
    internal static int Run(PilotLog log,PilotOptions options)
    {
        Log=log; DurationSeconds=options.DurationSeconds; CrossScreen=options.CrossScreen; SeamHandoff=options.SeamHandoff; Rocket=options.Rocket; Attachment=options.Attachment; RestSleep=options.RestSleep; RandomIdle=options.RandomIdle; AutonomousMovement=options.AutonomousMovement; Failed=false;
        MissingIdleGif=false;
        Physics=options.Physics;
        var app=new App(); // App.xaml has no resources/StartupUri; its generated Main only constructs App.
#if PMV2_PRODUCT
        var fatalShutdownStarted=false;
        app.DispatcherUnhandledException+=(_,e)=> {
            e.Handled=true;
            if(fatalShutdownStarted)return;
            fatalShutdownStarted=true;Failed=true;
            // Do not display modal UI while Dispatcher exception recovery is active.
            // ProductEntry reports once after Run returns (or its outer catch).
            try {log.Write("unhandled-wpf",e.Exception.ToString(),"FAIL");}
            finally {app.Shutdown(4);}
        };
#else
        app.DispatcherUnhandledException+=(_,e)=> {
            Failed=true;e.Handled=true;
            try {log.Write("unhandled-wpf",e.Exception.ToString(),"FAIL");} finally {app.Shutdown(4);}
        };
#endif
        app.Run();
        log.Write("trial-ended",new{Failed},Failed ? "FAIL" : "ENDED; USER HARDWARE PENDING");
        return Failed ? 4 : 0;
    }
    internal static void StartWindow()
    {
        var window=CreateWindow(CreateSettingsStore());
        window.EnablePmv2Pilot(Log);
        Application.Current.MainWindow=window;
        window.Show();
        if(DurationSeconds is {} seconds)
        {
            var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(seconds)};
            timer.Tick+=(_,_)=>{timer.Stop();window.Close();};
            window.Closed+=(_,_)=>timer.Stop();timer.Start();
        }
    }
}
