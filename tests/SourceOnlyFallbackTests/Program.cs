using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using NecoArcDesktopPet;
using NecoArcDesktopPet.Settings;

static class Program
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static void Pump(int ms) {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    // These checks exercise the actual PMv2 MainWindow build, never a placeholder admission.
    static int Pmv2(string root)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var type = typeof(MainWindow);
        var assembly = type.Assembly;
        var logPath = Path.Combine(root, "missing-idle.jsonl");
        using var log = (IDisposable)Activator.CreateInstance(assembly.GetType("NecoArcDesktopPet.Pilot.PilotLog")!, new object[] { logPath })!;
        var pet = new MainWindow(new PetSettingsStore(Path.Combine(root, "pmv2-settings.json")));
        try {
            Check(type.GetField("_gifPlayer", F)!.GetValue(pet) == null, "PMv2 fixture has no GIF player");
            type.GetMethod("EnablePmv2Pilot", F)!.Invoke(pet, new object[] { log });
            pet.Show(); Pump(250);
            log.Dispose();
            var events = File.ReadAllLines(logPath).Select(line => JsonDocument.Parse(line)).ToArray();
            try {
                var stopped = events.Single(e => e.RootElement.GetProperty("kind").GetString() == "safety-stop").RootElement;
                var reason = stopped.GetProperty("data").GetProperty("reason").GetString()!;
                Check(reason.Contains("idle GIF", StringComparison.OrdinalIgnoreCase), "missing idle stops with the asset cause before native layout: " + reason);
                Check(stopped.GetProperty("verdict").GetString() == "FAIL-CLOSED", "missing idle uses existing fail-closed stop");
                Check(!(bool)type.GetField("_pilotReady", F)!.GetValue(pet)!, "missing idle never establishes native layout readiness");
                Check(!pet.IsVisible && !(bool)type.GetField("_pilotReady", F)!.GetValue(pet)!, "missing idle window closes without native admission");
                Check((bool)assembly.GetType("NecoArcDesktopPet.Pilot.PilotApplication")!.GetProperty("MissingIdleGif", F)!.GetValue(null)!, "Product notice receives the specific missing-idle cause");
                Check(!events.Any(e => e.RootElement.GetProperty("kind").GetString() == "product-ready"), "placeholder never becomes Product/Pilot ready");
            } finally { foreach (var entry in events) entry.Dispose(); }
        } finally { pet.Close(); }

        SizeInputs();
        LayoutBoundary(root);
        app.Shutdown();
        Console.WriteLine($"PASS: {checks} PMv2 source-only regression checks");
        return 0;
    }
    static void LayoutBoundary(string root)
    {
        // Arrange an ordinary fallback HWND, then exercise only the PMv2 input boundary.
        // This deliberately does not represent admission of a real idle/physics owner.
        var type = typeof(MainWindow);
        var logType = type.Assembly.GetType("NecoArcDesktopPet.Pilot.PilotLog")!;
        foreach (var invalid in new[] { double.NaN, 0d }) {
            var logPath = Path.Combine(root, "boundary-" + Guid.NewGuid().ToString("N") + ".jsonl");
            using var log = (IDisposable)Activator.CreateInstance(logType, new object[] { logPath })!;
            var window = new MainWindow(new PetSettingsStore(Path.Combine(root, "pmv2-settings.json")));
            try {
                window.Show(); Pump(60);
                var surface = (FrameworkElement)window.FindName("PetSurface");
                var previousWidth = window.Width;
                type.GetField("_pilotLog", F)!.SetValue(window, log);
                type.GetField("_pilotSource", F)!.SetValue(window, PresentationSource.FromVisual(window));
                var dpi = VisualTreeHelper.GetDpi(window);
                var scaleType = type.Assembly.GetType("NecoArcDesktopPet.Coordinates.DpiScale")!;
                type.GetField("_pilotScale", F)!.SetValue(window, Activator.CreateInstance(scaleType, dpi.DpiScaleX, dpi.DpiScaleY));
                type.GetField("_pilotReady", F)!.SetValue(window, true);
                surface.Width = invalid;
                type.GetMethod("ApplyPetLayout", F)!.Invoke(window, null);
                Check((bool)type.GetField("_pilotStopped", F)!.GetValue(window)!, "invalid arranged width triggers safety stop at WPF/native boundary");
                Check(window.Width == previousWidth, "invalid arranged width never reaches Window.Width setter");
                Pump(60); log.Dispose();
                var rows = File.ReadAllLines(logPath).Select(line => JsonDocument.Parse(line)).ToArray();
                try {
                    var stopped = rows.Single(e => e.RootElement.GetProperty("kind").GetString() == "safety-stop");
                    Check(stopped.RootElement.GetProperty("data").GetProperty("reason").GetString()!.Contains("finite and positive"), "invalid layout receives explicit size diagnostic");
                } finally { foreach (var row in rows) row.Dispose(); }
            } finally { window.Close(); }
        }
    }
    static int SizeInputs()
    {
        var type = typeof(MainWindow);
        var validate = type.GetMethod("PilotLayoutSizeIsValid", F);
        Check(validate != null, "PMv2 exposes a finite-positive layout input guard");
        bool Valid(double w, double h) => (bool)validate!.Invoke(null, new object[] { w, h })!;
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0d, -1d }) {
            Check(!Valid(value, 260), "invalid layout width is refused: " + value);
            Check(!Valid(170, value), "invalid layout height is refused: " + value);
        }
        Check(!Valid(double.MaxValue * 2, 260), "scaled dimension overflow is refused");
        Check(Valid(170, 260) && Valid(255, 390) && Valid(.5, .5), "normal positive finite dimensions remain eligible for existing fit checks");
        Console.WriteLine($"PASS: {checks} layout input checks");
        return 0;
    }
    [STAThread] static int Main(string[] args)
    {
        MainWindow? pet = null; Window? editor = null;
        try {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            if (args.Contains("--make-profile-fixture")) return ProfileAdmissionCases.Generate(root);
            if (args.Contains("--profile-admission")) return ProfileAdmissionCases.Run(root);
            if (args.Contains("--pmv2-sizes")) return SizeInputs();
            if (args.Contains("--pmv2")) return Pmv2(root);
            // A missing WPF fallback or a regression to the character PNG fails here.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var path = Path.Combine(root, "settings.json");
            pet = new MainWindow(new PetSettingsStore(path));
            var image = (Image)pet.FindName("PetImage");
            Check(image.Source is BitmapSource, "asset-free MainWindow has a bitmap drawn by WPF");
            var bitmap = (BitmapSource)image.Source;
            Check(bitmap.IsFrozen && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "frozen finite fallback dimensions");
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Check(pixels.Where((_, i) => i % 4 == 3).Any(a => a > 0), "placeholder has visible pixels");
            Check(pixels[3] == 0, "placeholder keeps transparent outside margin");
            Check(typeof(MainWindow).GetField("_gifPlayer", F)!.GetValue(pet) == null, "missing GIF leaves animation unavailable");
            pet.Show(); Pump(250);
            Check(pet.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0, "fallback window completes WPF layout");
            typeof(MainWindow).GetMethod("OpenWorkbench", F)!.Invoke(pet, null); Pump(200);
            editor = (Window)typeof(MainWindow).GetField("_workbenchWindow", F)!.GetValue(pet)!;
            Check(editor.IsVisible && editor.Owner == pet, "Workbench opens with fallback owner");
            var snapshot = pet.GetSettingsSnapshot();
            Check(new PetSettingsStore(path).Save(snapshot, true).Success && File.Exists(path), "isolated settings can persist");
            editor.Close(); editor = null; Pump(60);
            Check(pet.IsVisible, "Workbench close retains main window");
            Console.WriteLine($"PASS: {checks} asset-free WPF checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.GetBaseException()); return 1; }
        finally { editor?.Close(); pet?.Close(); Application.Current?.Shutdown(); }
    }
}
