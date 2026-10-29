using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;

internal static class Program
{
    private const string Pound = "NecoPetMaterials/click/pound.gif";
    private const string Flick = "NecoPetMaterials/click/flick.gif";
    private const string Punch = "NecoPetMaterials/click/quick_punch.gif";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static object Field(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o)!;
    private static void Tick(GifFramePlayer player) => typeof(GifFramePlayer).GetMethod("OnTick", Private)!.Invoke(player, new object?[] { null, EventArgs.Empty });
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--cursor-only")) return CursorOnly();
            _ = new Application();
            var image = new Image();
            using var player = new GifFramePlayer(image, Flick);
            var completed = 0;
            player.Completed += (_, _) => completed++;
            // Exercise the real action definitions: only pound's complete-loop budget changes.
            foreach (var (path, loops) in new[] { (Pound, 13), (Flick, 1), (Punch, 15) })
            {
                var definition = ClickDefinitions.SingleActions.Single(action => action.ResourcePath == path);
                Check(player.TrySwitchToLoops(path, definition.RepeatCount), "load " + path);
                using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/{typeof(Program).Assembly.GetName().Name};component/{path}"))!.Stream;
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // Existing player convention: missing/zero delay is 100ms, positive delays have a 20ms floor.
                var durations = decoder.Frames.Select(f => Convert.ToInt32(((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")))
                    .Select(delay => TimeSpan.FromMilliseconds(delay > 0 ? Math.Max(20, delay * 10) : 100)).ToArray();
                var frames = (BitmapSource[])Field(player, "_clip").GetType().GetProperty("Frames")!.GetValue(Field(player, "_clip"))!;
                var cursor = (GifPlaybackCursor)Field(player, "_cursor");
                var before = completed;
                var total = TimeSpan.Zero;
                for (var step = 0; step < durations.Length * loops; step++)
                {
                    var index = step % durations.Length;
                    Check(!player.IsComplete && cursor.FrameIndex == index && ReferenceEquals(image.Source, frames[index]), "original frame order " + path);
                    var scheduled = (TimeSpan)Field(player, "_remaining");
                    var expected = durations[index];
                    Check(scheduled == expected, $"{path} step {step}: expected {expected.TotalMilliseconds}ms, got {scheduled.TotalMilliseconds}ms");
                    total += scheduled;
                    Tick(player);
                }
                Check(total == TimeSpan.FromTicks(durations.Sum(d => d.Ticks) * loops), "exact total duration " + path);
                if (path == Pound) Check(total == TimeSpan.FromMilliseconds(2600), "pound is 13 full loops / 2600ms without a final hold");
                Check(player.IsComplete && completed == before + 1, "completion once " + path);
                Tick(player);
                Check(completed == before + 1, "no duplicate completion " + path);
            }

            // Drive to the final frame, then exercise the existing Dispatcher timer.
            var poundLoops = ClickDefinitions.SingleActions.Single(action => action.ResourcePath == Pound).RepeatCount;
            player.TrySwitchToLoops(Pound, poundLoops);
            var frameCount = ((TimeSpan[])Field(player, "_clip").GetType().GetProperty("Durations")!.GetValue(Field(player, "_clip"))!).Length;
            for (var i = 0; i < frameCount * poundLoops - 1; i++) Tick(player);
            var lastFrame = image.Source;
            var beforeHold = completed;
            player.Pause();
            var remaining = (TimeSpan)Field(player, "_remaining");
            Check(remaining.TotalMilliseconds is > 0 and <= 100, "final frame retains its original 100ms duration");
            Pump(150);
            Check(completed == beforeHold && !player.IsComplete && ReferenceEquals(image.Source, lastFrame), "pause preserves final frame and pending completion");
            var clock = Stopwatch.StartNew();
            player.Resume();
            while (completed == beforeHold && clock.ElapsedMilliseconds < 2500) Pump(10);
            Check(completed == beforeHold + 1 && clock.Elapsed >= remaining - TimeSpan.FromMilliseconds(30), "resume completes after remaining frame time");

            // Preemption must replace the playback; the old generation cannot complete later.
            player.TrySwitchToLoops(Pound, poundLoops);
            for (var i = 0; i < frameCount * poundLoops - 1; i++) Tick(player);
            var oldGeneration = player.PlaybackGeneration;
            player.TrySwitchTo(Flick);
            player.Resume();
            var beforeSwitch = completed;
            Pump(1300);
            Check(player.ResourcePath == Flick && player.PlaybackGeneration > oldGeneration && completed == beforeSwitch, "switch cancels pound without stale completion");
            player.TrySwitchToLoops(Pound, poundLoops);
            for (var i = 0; i < frameCount * poundLoops - 1; i++) Tick(player);
            player.Stop();
            Check(((GifPlaybackCursor)Field(player, "_cursor")).FrameIndex == 0 && (TimeSpan)Field(player, "_remaining") == TimeSpan.FromMilliseconds(100), "Stop rewinds to the original first frame duration");
            Console.WriteLine($"PASS checks={_checks}; pound 13 loops / 2600ms, original frame durations, other clicks, pause/resume, preemption, Stop");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    // A synthetic 2 x 100ms cycle checks the production loop budget without
    // embedding media. This does not decode or certify any supplied GIF timing.
    private static int CursorOnly()
    {
        foreach (var (path, ticks) in new[] { (Pound, 26), (Flick, 2), (Punch, 30) })
        {
            var action = ClickDefinitions.SingleActions.Single(a => a.ResourcePath == path);
            var cursor = new GifPlaybackCursor(2);
            cursor.Reset(false, action.RepeatCount);
            for (var i = 0; i < ticks - 1; i++)
                Check(!cursor.Advance() && !cursor.IsComplete, path + " completes too early at synthetic frame " + i);
            Check(cursor.Advance() && cursor.IsComplete, path + " completes at the expected synthetic frame boundary");
            Check(!cursor.Advance(), path + " completion is reported once");
            cursor.Rewind();
            for (var i = 0; i < ticks - 1; i++) Check(!cursor.Advance(), path + " rewind retains repeat budget");
            Check(cursor.Advance(), path + " rewind completes after all loops");
            cursor.Reset(false, 1);
            Check(!cursor.Advance() && cursor.Advance(), path + " replacement clears old repeat budget");
        }
        Console.WriteLine($"PASS checks={_checks}; source-only cursor: pound synthetic 2600ms, flick/punch unchanged, completion/rewind/interruption; real GIF timing not tested");
        return 0;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
