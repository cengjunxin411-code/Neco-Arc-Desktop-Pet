using System;

namespace NecoArcDesktopPet.Coordinates;

internal sealed class MonitorTopologyService : IDisposable
{
    private readonly IMonitorProvider _provider;
    private readonly IDisplayChangeSource _changes;
    private readonly object _gate = new();
    private MonitorTopologyState _state = new(null, true, true, TopologyFailure.None, null, false);
    private bool _refreshing;
    private object _invalidationToken = new();

    // Caller owns the source's lifetime; service owns only its subscription.
    public MonitorTopologyService(IMonitorProvider provider, IDisplayChangeSource changes)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _changes.Invalidated += OnInvalidated;
    }
    public MonitorTopologyState State { get { lock (_gate) return _state; } }
    public event EventHandler? RefreshRequested;
    public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
    public event EventHandler<TopologyDiagnostic>? SubscriberFailed;

    // Synchronous; callbacks run on the refresh caller or invalidation caller.
    // Concurrent/reentrant refresh fails promptly and does not overwrite the active query's state.
    // Invalidation callbacks may overlap publication callbacks. No external code runs under _gate.
    public MonitorRefreshResult Refresh()
    {
        object token;
        lock (_gate)
        {
            if (_state.IsDisposed) return Rejected(TopologyFailure.Disposed);
            if (_refreshing) return Rejected(TopologyFailure.RefreshInProgress);
            _refreshing = true;
            token = _invalidationToken;
        }
        try
        {
            MonitorQueryResult query;
            try { query = _provider.Query() ?? throw new InvalidOperationException("Provider returned null."); }
            catch (Exception e) { query = MonitorQueryResult.Failed(TopologyFailure.ProviderException,
                TopologyDiagnostic.FromException("Provider.Query", e)); }

            {
                MonitorRefreshResult result;
                lock (_gate)
                {
                    if (_state.IsDisposed) return Rejected(TopologyFailure.Disposed);
                    if (!ReferenceEquals(token, _invalidationToken))
                        query = MonitorQueryResult.Failed(TopologyFailure.RefreshInvalidated,
                            new("Refresh", "Invalidation arrived during collection; explicitly refresh again."));
                    if (!query.Success)
                    {
                        _state = _state with { IsDirty = true, IsStale = true, Failure = query.Failure, Diagnostic = query.Diagnostic };
                        return new(TopologyRefreshStatus.Failed, query.Failure, _state, query.Diagnostic);
                    }
                    var last = _state.LastKnownGood;
                    bool changed = last == null || !MonitorQueryResult.SameContent(last.Monitors, query.Monitors);
                    if (changed)
                    {
                        if (!TryNextVersion(last?.Version ?? 0, out var next))
                        {
                            var diagnostic = new TopologyDiagnostic("Publish", "Revision exhausted; restart service, never wrap.");
                            _state = _state with { IsDirty = true, IsStale = true, Failure = TopologyFailure.VersionExhausted, Diagnostic = diagnostic };
                            return new(TopologyRefreshStatus.Failed, _state.Failure, _state, diagnostic);
                        }
                        last = new MonitorTopologySnapshot(next, query);
                    }
                    _state = new(last, false, false, TopologyFailure.None, query.Diagnostic, false);
                    result = new(changed ? TopologyRefreshStatus.Changed : TopologyRefreshStatus.Unchanged,
                        TopologyFailure.None, _state, query.Diagnostic);
                }
                if (result.Status == TopologyRefreshStatus.Changed)
                    Notify(TopologyChanged, new TopologyChangedEventArgs(result.State.LastKnownGood!));
                // Result describes the atomic publication. Events may subsequently dirty/dispose State.
                return result;
            }
        }
        finally { lock (_gate) _refreshing = false; }
    }

    private MonitorRefreshResult Rejected(TopologyFailure failure) => new(TopologyRefreshStatus.Failed,
        failure, _state, new("Refresh", failure.ToString()));

    internal static bool TryNextVersion(long current, out long next)
    {
        next = 0;
        if (current < 0 || current == long.MaxValue) return false;
        next = current + 1;
        return true;
    }

    private void OnInvalidated(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_state.IsDisposed) return;
            _invalidationToken = new object(); // No counter overflow/ABA.
            _state = _state with { IsDirty = true, IsStale = true };
        }
        Notify(RefreshRequested, EventArgs.Empty);
    }

    private void Notify(EventHandler? handlers, EventArgs args)
    {
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            if (State.IsDisposed) break;
            try { handler(this, args); }
            catch (Exception e) { ReportSubscriber(e); }
        }
    }
    private void Notify<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers == null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            if (State.IsDisposed) break;
            try { handler(this, args); }
            catch (Exception e) { ReportSubscriber(e); }
        }
    }
    private void ReportSubscriber(Exception exception)
    {
        var diagnostic = TopologyDiagnostic.FromException("Subscriber", exception);
        var handlers = SubscriberFailed;
        if (handlers == null) { System.Diagnostics.Trace.WriteLine(diagnostic); return; }
        foreach (EventHandler<TopologyDiagnostic> handler in handlers.GetInvocationList())
        {
            if (State.IsDisposed) break;
            try { handler(this, diagnostic); }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine(TopologyDiagnostic.FromException("SubscriberFailed", e)); }
        }
    }

    public void Dispose()
    {
        // Does not join in-flight subscribers: waiting could deadlock a caller-owned dispatcher.
        // Already-started callbacks may finish; each pending invocation checks disposed state.
        lock (_gate)
        {
            if (_state.IsDisposed) return;
            _state = _state with { IsDisposed = true, IsDirty = true, IsStale = true, Failure = TopologyFailure.Disposed };
        }
        _changes.Invalidated -= OnInvalidated;
        RefreshRequested = null;
        TopologyChanged = null;
        SubscriberFailed = null;
    }
}
