using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NecoArcDesktopPet.Coordinates;

internal static class Program
{
    private static int checks, failures;
    private static readonly TopologyDiagnostic Evidence = new("Fake", "Verified test physical pixels");
    private static MonitorSnapshot Screen(string id = "A", PhysicalRect? bounds = null, PhysicalRect? work = null) =>
        new(id, bounds ?? new(0, 0, 1920, 1080), work ?? bounds ?? new(0, 0, 1920, 1080));
    private static MonitorQueryResult Query(params MonitorSnapshot[] monitors) =>
        MonitorQueryResult.Create(monitors, MonitorCoordinateSpace.PhysicalPixels, Evidence);
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--smoke") >= 0) return NativeCases.Smoke();
        var cases = new List<(string, Action)> {
            ("immutable publication", Immutable), ("literal signed topology and taskbars", Literal),
            ("strict complete set validation", Validation), ("order and content revisions", Revisions),
            ("disconnect failure recovery", Recovery), ("dirty differs from publication", Dirty),
            ("structured provider failures", Errors), ("reentry and subscriber exceptions", Reentry),
            ("concurrent refresh and disposal", Concurrent), ("invalidation during capture", Race),
            ("dispose subscriptions and in-event disposal", Disposal), ("revision overflow", Overflow),
            ("callbacks do not block invalidation or disposal", CallbackConcurrency),
            ("diagnostic observer concurrent unsubscription", DiagnosticConcurrency)
        };
        cases.AddRange(NativeCases.Cases(Check));
        foreach (var (name, run) in cases) {
            try { run(); Console.WriteLine("PASS: " + name); }
            catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + ": " + e); }
        }
        Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks} checks, {cases.Count} cases, {failures} failed cases");
        return failures == 0 ? 0 : 1;
    }

    private static void Immutable()
    {
        var input = new[] { Screen() }; var query = Query(input); input[0] = Screen("mutated");
        Check(query.Monitors[0].Id == "A", "query owns input copy");
        using var source = new FakeChanges(); var provider = new FakeProvider(query);
        using var service = new MonitorTopologyService(provider, source);
        var result = service.Refresh(); Check(result.Success, "first publication");
        var snapshot = result.State.LastKnownGood!;
        Check(snapshot.Version == 1 && snapshot.Monitors[0].Id == "A", "positive immutable revision");
        Check(snapshot.Monitors is not MonitorSnapshot[], "cannot cast output to array");
        try { ((IList<MonitorSnapshot>)snapshot.Monitors)[0] = Screen("bad"); Check(false, "output writable"); }
        catch (NotSupportedException) { Check(true, "output rejects write"); }
        provider.Current = Query(Screen("B")); service.Refresh();
        Check(snapshot.Monitors[0].Id == "A" && snapshot.Version == 1, "old snapshot stable");
    }

    private static void Literal()
    {
        var screens = new[] {
            Screen("left", new(-1600, 0, 0, 900), new(-1560, 0, 0, 900)),
            Screen("up", new(0, -1000, 1920, 0), new(0, -970, 1920, 0)),
            Screen("right", new(2100, 0, 3100, 1000), new(2100, 0, 3060, 1000)),
            Screen("bottom", new(0, 1100, 1920, 2180), new(0, 1100, 1920, 2130)), Screen()
        };
        var query = Query(screens); Check(query.Success, "negative/gap/shared edges valid");
        foreach (var screen in screens) Check(System.Linq.Enumerable.Contains(query.Monitors, screen), "literal geometry preserved");
        var extreme = Screen("extreme", new(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue));
        Check(Query(extreme).Monitors[0] == extreme, "signed Int32 edges exactly preserved");
    }

    private static void Validation()
    {
        Check(Query().Failure == TopologyFailure.NoMonitors, "empty not success");
        Check(Query(Screen(), Screen()).Failure == TopologyFailure.DuplicateMonitorId, "duplicate rejected");
        foreach (var id in new[] { "", " ", "\t" }) Check(Query(Screen(id)).Failure == TopologyFailure.InvalidMonitorId, "blank id");
        foreach (var screen in new[] {
            Screen("bad", new(0, 0, 0, 100)), Screen("bad", new(double.NaN, 0, 100, 100)),
            Screen("bad", work: new(-1, 0, 1920, 1080)), Screen("bad", work: new(0, 0, 1920, 1081)),
            Screen("bad", work: new(0, 0, 1920, 0)), Screen("bad", new(100, 0, 0, 100)) }) {
            var result = Query(Screen(), screen);
            Check(result.Failure == TopologyFailure.InvalidGeometry && result.Monitors.Count == 0, "whole set rejected");
        }
        Check(MonitorQueryResult.Create(new[] { Screen() }, MonitorCoordinateSpace.Unverified, Evidence).Failure ==
            TopologyFailure.UnverifiedCoordinateSpace, "units not inferred from integer-looking values");
    }

    private static void Revisions()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen("B"), Screen()));
        using var service = new MonitorTopologyService(provider, changes); int events = 0;
        service.TopologyChanged += (_, e) => { events++; Check(ReferenceEquals(e.Snapshot, service.State.LastKnownGood), "atomic event state"); };
        Check(service.Refresh().State.LastKnownGood!.Monitors[0].Id == "A", "ordinal ordering");
        provider.Current = Query(Screen(), Screen("B"));
        Check(service.Refresh().Status == TopologyRefreshStatus.Unchanged && events == 1, "order no revision");
        provider.Current = Query(Screen(work: new(0, 0, 1920, 1040)), Screen("B"));
        Check(service.Refresh().State.LastKnownGood!.Version == 2 && events == 2, "work area changes revision once");
        Check(service.Refresh().State.LastKnownGood!.Version == 2 && events == 2, "repeat unchanged");
        provider.Current = Query(Screen(bounds: new(0, 0, 2560, 1440)), Screen("B"));
        Check(service.Refresh().State.LastKnownGood!.Version == 3, "same id new bounds");
    }

    private static void Recovery()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes);
        service.Refresh(); provider.Current = Query(Screen(), Screen("B"));
        Check(service.Refresh().State.LastKnownGood!.Version == 2, "add screen");
        provider.Current = Query(Screen()); Check(service.Refresh().State.LastKnownGood!.Version == 3, "remove screen");
        provider.Current = Query(); var failure = service.Refresh();
        Check(!failure.Success && failure.State.IsStale && failure.State.IsDirty, "0 screens stale");
        Check(failure.State.LastKnownGood!.Version == 3 && failure.State.LastKnownGood.Monitors.Count == 1, "last good diagnostic only");
        provider.Current = Query(Screen()); var recovery = service.Refresh();
        Check(recovery.Status == TopologyRefreshStatus.Unchanged && !recovery.State.IsStale && recovery.State.LastKnownGood!.Version == 3, "same content recovery");
        provider.Current = Query(Screen("C")); Check(service.Refresh().State.LastKnownGood!.Version == 4, "different reconnect");
    }

    private static void Dirty()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes); int requested = 0, published = 0;
        service.RefreshRequested += (_, _) => requested++; service.TopologyChanged += (_, _) => published++;
        service.Refresh(); changes.Fire();
        Check(requested == 1 && published == 1 && provider.Calls == 1, "invalidation does not query");
        Check(service.State.IsDirty && service.State.IsStale && service.State.LastKnownGood!.Version == 1, "dirty marks last good stale");
        Check(service.Refresh().Status == TopologyRefreshStatus.Unchanged && !service.State.IsDirty, "authoritative refresh clears dirty");
        changes.Fire(); provider.Current = Query(Screen("B")); service.Refresh();
        Check(requested == 2 && published == 2, "separate event streams");
    }

    private static void Errors()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes); int events = 0;
        service.TopologyChanged += (_, _) => events++; service.Refresh();
        foreach (var code in new[] { TopologyFailure.UnverifiedCoordinateSpace, TopologyFailure.NativeApiError, TopologyFailure.UnstableTopology }) {
            provider.Current = MonitorQueryResult.Failed(code, new("GetMonitorInfoW", "test", 5));
            var r = service.Refresh(); Check(r.Failure == code && r.Diagnostic.NativeError == 5 && r.State.IsStale, "structured failure");
        }
        provider.OnQuery = () => throw new InvalidOperationException("fake exception");
        var error = service.Refresh(); Check(error.Failure == TopologyFailure.ProviderException &&
            error.Diagnostic.ExceptionType == typeof(InvalidOperationException).FullName, "exception details");
        Check(events == 1 && service.State.LastKnownGood!.Version == 1, "failures never publish");
    }

    private static void Reentry()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes); int later = 0, errors = 0;
        service.SubscriberFailed += (_, d) => { errors++; Check(d.ExceptionType != null, "subscriber diagnostic"); };
        service.TopologyChanged += (_, _) => throw new InvalidOperationException("subscriber");
        service.TopologyChanged += (_, _) => { later++; Check(service.Refresh().Failure == TopologyFailure.RefreshInProgress, "event reentry refused"); };
        provider.OnQuery = () => { Check(service.Refresh().Failure == TopologyFailure.RefreshInProgress, "provider reentry refused"); return provider.Current; };
        Check(service.Refresh().Success && later == 1 && errors == 1, "subscriber exception isolated");
        service.RefreshRequested += (_, _) => throw new Exception("dirty subscriber");
        service.RefreshRequested += (_, _) => later++;
        changes.Fire(); Check(later == 2 && errors == 2, "invalidation subscriber isolation");
    }

    private static void Concurrent()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes);
        provider.OnQuery = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("barrier timeout"); return provider.Current; };
        var task = Task.Run(service.Refresh); Check(entered.Wait(TimeSpan.FromSeconds(10)), "query entered");
        try { Check(service.Refresh().Failure == TopologyFailure.RefreshInProgress, "concurrent refused"); service.Dispose(); }
        finally { release.Set(); }
        var result = task.GetAwaiter().GetResult();
        Check(result.Failure == TopologyFailure.Disposed && service.State.LastKnownGood == null, "dispose forbids late publish");
    }

    private static void Race()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes); service.Refresh();
        provider.OnQuery = () => { changes.Fire(); return Query(Screen("B")); };
        Check(service.Refresh().Failure == TopologyFailure.RefreshInvalidated && service.State.LastKnownGood!.Version == 1, "signal during query rejects capture");
        provider.OnQuery = null; provider.Current = Query(Screen("B"));
        Check(service.Refresh().State.LastKnownGood!.Version == 2, "explicit retry recovers");
    }

    private static void Disposal()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes); int requested = 0, later = 0;
        service.RefreshRequested += (_, _) => requested++;
        service.TopologyChanged += (_, _) => service.Dispose(); service.TopologyChanged += (_, _) => later++;
        service.Refresh(); changes.Fire();
        Check(changes.Subscribers == 0 && requested == 0 && later == 0, "dispose removes subscription and stops callbacks");
        Check(service.State.IsDisposed && service.State.IsStale, "disposed state diagnostic");
        Check(service.Refresh().Failure == TopologyFailure.Disposed && provider.Calls == 1, "disposed query never calls provider");
        service.Dispose(); Check(changes.Subscribers == 0, "idempotent disposal");
    }

    private static void Overflow()
    {
        Check(MonitorTopologyService.TryNextVersion(0, out var next) && next == 1, "initial revision");
        Check(MonitorTopologyService.TryNextVersion(long.MaxValue - 1, out next) && next == long.MaxValue, "last revision");
        Check(!MonitorTopologyService.TryNextVersion(long.MaxValue, out _) && !MonitorTopologyService.TryNextVersion(-1, out _), "no wrap or invalid seed");
    }

    private static void CallbackConcurrency()
    {
        foreach (bool dispose in new[] { false, true })
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var changes = new FakeChanges(); using var service = new MonitorTopologyService(new FakeProvider(Query(Screen())), changes);
            service.TopologyChanged += (_, _) => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("subscriber barrier timeout"); };
            var refresh = Task.Run(service.Refresh);
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "subscriber entered");
            var concurrent = Task.Run(() => { if (dispose) service.Dispose(); else changes.Fire(); });
            bool completed;
            try { completed = concurrent.Wait(TimeSpan.FromSeconds(1)); }
            finally { release.Set(); }
            refresh.GetAwaiter().GetResult(); concurrent.GetAwaiter().GetResult();
            Check(completed, dispose ? "Dispose must not wait for externally blocked subscriber" : "invalidation must not wait for externally blocked subscriber");
            Check(service.State.IsStale && (dispose ? service.State.IsDisposed : service.State.IsDirty), "concurrent lifecycle signal retained");
        }
    }

    private static void DiagnosticConcurrency()
    {
        using var changes = new FakeChanges(); var provider = new FakeProvider(Query(Screen()));
        using var service = new MonitorTopologyService(provider, changes);
        service.TopologyChanged += (_, _) => throw new Exception("observer failure");
        EventHandler<TopologyDiagnostic> observer = (_, _) => { };
        using var start = new ManualResetEventSlim();
        var toggler = Task.Run(() => { start.Wait(); for (var i = 0; i < 100000; i++) { service.SubscriberFailed += observer; service.SubscriberFailed -= observer; } });
        start.Set();
        try
        {
            for (var i = 0; i < 10000; i++)
            {
                provider.Current = Query(Screen(i % 2 == 0 ? "A" : "B"));
                if (!service.Refresh().Success) throw new Exception("observer race broke refresh");
            }
        }
        finally { toggler.GetAwaiter().GetResult(); }
        Check(true, "10,000 publications survive concurrent diagnostic unsubscription");
    }
}

internal sealed class FakeProvider(MonitorQueryResult initial) : IMonitorProvider
{
    public MonitorQueryResult Current = initial;
    public Func<MonitorQueryResult>? OnQuery;
    public int Calls;
    public MonitorQueryResult Query() { Calls++; return OnQuery?.Invoke() ?? Current; }
}
internal sealed class FakeChanges : IDisplayChangeSource
{
    public event EventHandler? Invalidated;
    public int Subscribers => Invalidated?.GetInvocationList().Length ?? 0;
    public void Fire() => Invalidated?.Invoke(this, EventArgs.Empty);
    public void Dispose() => Invalidated = null;
}
