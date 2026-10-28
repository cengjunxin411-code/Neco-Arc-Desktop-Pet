using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet.Product;

internal static class ProductEntry
{
    // Product capabilities are independent of saved behavior preferences and of
    // Pilot's opt-in parser. No experimental or probe switches are accepted here.
    internal static PilotOptions Parse(string[] args) => args.Length == 0
        ? new(true, true, null, null, null, SeamHandoff: true, Rocket: true,
            Attachment: true, RestSleep: true, RandomIdle: true,
            AutonomousMovement: true, Physics: true)
        : new(false, false, null, null, "Product does not accept command-line switches.");

    [STAThread] public static int Main(string[] args)
    {
        var notice = new ProductFailureNotice();
        using var log = ProductDiagnostics.FromEnvironment();
        ProductSingleInstance? instance = null;
        try
        {
            instance = ProductSingleInstance.TryAcquire();
            if (instance == null) return 0;
            var options = Parse(args);
            string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
            var host = Environment.ProcessPath!;
            var dll = typeof(ProductEntry).Assembly.Location;
            if (log.IsEnabled)
            {
                try { log.Write("build-identity", new { host, hostSha256 = Hash(host), dll, dllSha256 = Hash(dll), pid = Environment.ProcessId }); }
                catch (Exception) { /* Optional diagnostic hashing must not block startup. */ }
            }
            var expected = Path.Combine(AppContext.BaseDirectory, "NecoArcDesktopPet.Product.exe");
            if (!string.Equals(Path.GetFullPath(host), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            {
                log.Write("entry-refused", new { reason = "Actual isolated Product apphost required before WPF" }, "BLOCKED");
                notice.Show(ProductFailure.Entry);
                return 2;
            }
            if (!PilotNative.ProcessV2 || !PilotNative.ThreadV2)
            {
                log.Write("entry-refused", new { reason = "Actual isolated Product apphost/process/thread PMv2 required before WPF", processV2 = PilotNative.ProcessV2, threadV2 = PilotNative.ThreadV2 }, "BLOCKED");
                notice.Show(ProductFailure.Dpi);
                return 2;
            }
            if (!options.Valid) { log.Write("entry-refused", new { reason = options.Error }, "BLOCKED"); notice.Show(ProductFailure.Entry); return 3; }
            log.Write("before-wpf", new { processV2 = PilotNative.ProcessV2, threadV2 = PilotNative.ThreadV2,
                mode = "Product", crossScreen = options.CrossScreen, seamHandoff = options.SeamHandoff,
                rocket = options.Rocket, attachment = options.Attachment, restSleep = options.RestSleep,
                randomIdle = options.RandomIdle, autonomousMovement = options.AutonomousMovement, physics = options.Physics });
            var code = Launch(log, options);
            if (code != 0) notice.Show(PilotApplication.MissingIdleGif ? ProductFailure.MissingIdleGif : ProductFailure.Runtime);
            return code;
        }
        catch (Exception e)
        {
            log.Write("fatal-startup", e.ToString(), "FAIL");
            notice.Show(ProductFailure.Startup);
            return 1;
        }
        finally { instance?.Dispose(); }
    }

    // Keep the native apphost/DPI gate ahead of WPF/JIT type loading.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Launch(PilotLog log, PilotOptions options) => PilotApplication.Run(log, options);
}
