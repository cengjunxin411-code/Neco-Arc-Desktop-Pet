using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Coordinates.Windows;

internal static class NativeCases
{
    private static NativeMonitorSample Sample(string id = "\\\\.\\DISPLAY1", NativeMonitorRect? bounds = null,
        NativeMonitorRect? work = null, uint? fields = null, uint orientation = 0)
    {
        var b = bounds ?? new(0, 0, 1920, 1080);
        return new(id, b, work ?? b, fields ?? WindowsMonitorProvider.RequiredModeFields,
            b.Left, b.Top, (uint)((long)b.Right - b.Left), (uint)((long)b.Bottom - b.Top), orientation);
    }
    private static NativeMonitorCapture Capture(int awareness = 2, params NativeMonitorSample[] samples) =>
        new(TopologyFailure.None, new("FakeNative", "samples"), new(awareness, "fake", 2, 0), samples);

    public static IEnumerable<(string, Action)> Cases(Action<bool, string> check)
    {
        yield return ("native awareness fail closed", () => {
            foreach (int awareness in new[] { -1, 0, 1, 99 }) {
                var api = new FakeNative(Capture(awareness, Sample()));
                var r = new WindowsMonitorProvider(api).Query();
                check(r.Failure == TopologyFailure.UnverifiedCoordinateSpace && r.Monitors.Count == 0, "unknown/unaware/system rejected");
                check(api.Calls == 1 && r.Diagnostic.DpiContext != null, "no retries on uncertainty");
            }
            var failedProcess = Capture(2, Sample()) with { Dpi = new(2, "fake", -1, unchecked((int)0x80070005)) };
            check(new WindowsMonitorProvider(new FakeNative(failedProcess)).Query().Success, "thread authoritative, process query diagnostic");
        });
        yield return ("native mode cross-check", () => {
            foreach (var sample in new[] { Sample(fields: 0), Sample() with { ModeX = 1 },
                Sample() with { ModeWidth = 1919 }, Sample() with { ModeHeight = 0 } }) {
                var r = new WindowsMonitorProvider(new FakeNative(Capture(2, sample))).Query();
                check(r.Failure == TopologyFailure.UnverifiedCoordinateSpace && r.Diagnostic.Stage == "ModeVerification", "mode mismatch/missing rejected");
            }
        });
        yield return ("native RECT edges rotation and IDs", () => {
            var left = Sample("\\\\.\\display2", new(-1920, -1080, 0, 0), new(-1900, -1050, 0, -40));
            var rotated = Sample(bounds: new(0, 0, 1080, 1920), orientation: 1);
            var r = new WindowsMonitorProvider(new FakeNative(Capture(2, left, rotated))).Query();
            check(r.Success && r.Monitors[1].Id == "\\\\.\\DISPLAY2", "logical name uppercase ordinal");
            check(r.Monitors[1].Bounds == new PhysicalRect(-1920, -1080, 0, 0), "edges unchanged without -1");
            check(r.Monitors[1].WorkArea.Bottom == -40 && r.Monitors[0].Bounds.Right == 1080, "rotation uses current reported mode");
            var huge = Sample(bounds: new(int.MinValue, -10, int.MaxValue, 10));
            var h = new WindowsMonitorProvider(new FakeNative(Capture(2, huge))).Query();
            check(h.Success && h.Monitors[0].Bounds.Width == 4294967295d, "unsigned mode width no signed truncation");
        });
        yield return ("native complete-set failure", () => {
            foreach (var pair in new[] { (Capture(2), TopologyFailure.NoMonitors),
                (Capture(2, Sample(), Sample("\\\\.\\display1")), TopologyFailure.DuplicateMonitorId),
                (Capture(2, Sample(), Sample(" ")), TopologyFailure.InvalidMonitorId),
                (Capture(2, Sample(), Sample("other", work: new(-1, 0, 1920, 1080))), TopologyFailure.InvalidGeometry) }) {
                var r = new WindowsMonitorProvider(new FakeNative(pair.Item1)).Query();
                check(r.Failure == pair.Item2 && r.Monitors.Count == 0, "never partial success");
            }
            var broken = Capture(2, Sample()) with { Failure = TopologyFailure.NativeApiError,
                Diagnostic = new("GetMonitorInfoW", "partial failure", 5) };
            var failure = new WindowsMonitorProvider(new FakeNative(broken)).Query();
            check(failure.Failure == TopologyFailure.NativeApiError && failure.Monitors.Count == 0 && failure.Diagnostic.NativeError == 5, "partial native enumeration rejected");
        });
        yield return ("native bounded consistency retries", () => {
            var a = Capture(2, Sample()); var b = Capture(2, Sample() with { WorkArea = new(0, 0, 1920, 1040) });
            var api = new FakeNative(a, b, b); var r = new WindowsMonitorProvider(api).Query();
            check(r.Success && api.Calls == 3 && r.Monitors[0].WorkArea.Bottom == 1040, "stable adjacent captures after retry");
            api = new FakeNative(a, b, a, b); r = new WindowsMonitorProvider(api).Query();
            check(r.Failure == TopologyFailure.UnstableTopology && api.Calls == 4, "bounded exhaustion");
            var reversed = new FakeNative(Capture(2, Sample(), Sample("B")), Capture(2, Sample("B"), Sample()));
            check(new WindowsMonitorProvider(reversed).Query().Success && reversed.Calls == 2, "native enumeration order ignored");
        });
        yield return ("native exceptions and retained snapshots", () => {
            var api = new FakeNative(Capture(2, Sample())) { Exception = new InvalidOperationException("native fake") };
            var provider = new WindowsMonitorProvider(api); var r = provider.Query();
            check(r.Failure == TopologyFailure.ProviderException && r.Diagnostic.HResult != null, "capture exception structured");
            api.Exception = null; using var changes = new FakeChanges(); using var service = new MonitorTopologyService(provider, changes);
            service.Refresh(); api.Exception = new Exception("failed"); r = provider.Query(); var failure = service.Refresh();
            check(!failure.Success && failure.State.IsStale && failure.State.LastKnownGood!.Version == 1, "native failure keeps stale last known");
            api.Exception = null; check(service.Refresh().Status == TopologyRefreshStatus.Unchanged, "native recovery unchanged revision");
        });
    }

    public static int Smoke()
    {
        var raw = new WindowsMonitorApi().Capture();
        Console.WriteLine($"RAW: failure={raw.Failure}; DPI={raw.Dpi}; sample count={raw.Samples.Count}");
        foreach (var sample in raw.Samples) Console.WriteLine(sample);
        var r = new WindowsMonitorProvider().Query();
        Console.WriteLine($"QUERY: {(r.Success ? "VERIFIED_PHYSICAL_PIXELS" : "UNVERIFIED/FAILED")}; failure={r.Failure}; published count={r.Monitors.Count}");
        Console.WriteLine(r.Diagnostic);
        Console.WriteLine("Events: UNVERIFIED real hotplug/taskbar changes; this command never changes DPI or system settings.");
        return r.Success ? 0 : 2;
    }
}
internal sealed class FakeNative(params NativeMonitorCapture[] captures) : IWindowsMonitorApi
{
    public int Calls;
    public Exception? Exception;
    public NativeMonitorCapture Capture()
    {
        Calls++;
        if (Exception != null) throw Exception;
        return captures[Math.Min(Calls - 1, captures.Length - 1)];
    }
}
