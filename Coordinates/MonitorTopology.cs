using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NecoArcDesktopPet.Coordinates;

internal enum TopologyFailure
{
    None, NoMonitors, InvalidMonitorId, DuplicateMonitorId, InvalidGeometry,
    UnverifiedCoordinateSpace, NativeApiError, ProviderException, UnsupportedPlatform,
    UnstableTopology, RefreshInProgress, RefreshInvalidated, VersionExhausted, Disposed
}

internal enum MonitorCoordinateSpace { Unverified, PhysicalPixels }

internal sealed record TopologyDiagnostic(string Stage, string Message, int? NativeError = null,
    string? ExceptionType = null, int? HResult = null, string? DpiContext = null)
{
    public static TopologyDiagnostic FromException(string stage, Exception exception) =>
        new(stage, exception.Message, ExceptionType: exception.GetType().FullName, HResult: exception.HResult);
}

internal interface IMonitorProvider { MonitorQueryResult Query(); }
internal interface IDisplayChangeSource : IDisposable { event EventHandler? Invalidated; }

// Success is a complete validated set. Failure never contains usable coordinates.
internal sealed class MonitorQueryResult
{
    private MonitorQueryResult(TopologyFailure failure, IReadOnlyList<MonitorSnapshot> monitors, TopologyDiagnostic diagnostic)
    { Failure = failure; Monitors = monitors; Diagnostic = diagnostic; }

    public TopologyFailure Failure { get; }
    public bool Success => Failure == TopologyFailure.None;
    public IReadOnlyList<MonitorSnapshot> Monitors { get; }
    public TopologyDiagnostic Diagnostic { get; }

    public static MonitorQueryResult Failed(TopologyFailure failure, TopologyDiagnostic diagnostic)
    {
        if (failure == TopologyFailure.None) throw new ArgumentOutOfRangeException(nameof(failure));
        return new(failure, Array.AsReadOnly(Array.Empty<MonitorSnapshot>()), diagnostic);
    }

    public static MonitorQueryResult Create(IEnumerable<MonitorSnapshot> monitors, MonitorCoordinateSpace space,
        TopologyDiagnostic diagnostic)
    {
        if (space != MonitorCoordinateSpace.PhysicalPixels)
            return Failed(TopologyFailure.UnverifiedCoordinateSpace, diagnostic);
        var copy = monitors.ToArray();
        if (copy.Length == 0) return Failed(TopologyFailure.NoMonitors, diagnostic);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var monitor in copy)
        {
            if (string.IsNullOrWhiteSpace(monitor.Id)) return Failed(TopologyFailure.InvalidMonitorId, diagnostic);
            if (!ids.Add(monitor.Id)) return Failed(TopologyFailure.DuplicateMonitorId, diagnostic);
            if (!monitor.IsValid) return Failed(TopologyFailure.InvalidGeometry, diagnostic);
        }
        Array.Sort(copy, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        return new(TopologyFailure.None, Array.AsReadOnly(copy), diagnostic);
    }

    internal static bool SameContent(IReadOnlyList<MonitorSnapshot> a, IReadOnlyList<MonitorSnapshot> b) =>
        a.Count == b.Count && a.SequenceEqual(b);
}

internal sealed class MonitorTopologySnapshot
{
    internal MonitorTopologySnapshot(long version, MonitorQueryResult query)
    {
        if (version < 1 || !query.Success) throw new ArgumentException("A snapshot requires a positive revision and valid query.");
        Version = version;
        Monitors = new ReadOnlyCollection<MonitorSnapshot>(query.Monitors.ToArray());
    }
    public long Version { get; }
    public IReadOnlyList<MonitorSnapshot> Monitors { get; }
}

// LastKnownGood is diagnostic when dirty, failed or disposed; never current successful data.
internal sealed record MonitorTopologyState(MonitorTopologySnapshot? LastKnownGood, bool IsDirty,
    bool IsStale, TopologyFailure Failure, TopologyDiagnostic? Diagnostic, bool IsDisposed);
internal enum TopologyRefreshStatus { Changed, Unchanged, Failed }
internal sealed record MonitorRefreshResult(TopologyRefreshStatus Status, TopologyFailure Failure,
    MonitorTopologyState State, TopologyDiagnostic Diagnostic)
{
    public bool Success => Status != TopologyRefreshStatus.Failed;
}

internal sealed class TopologyChangedEventArgs(MonitorTopologySnapshot snapshot) : EventArgs
{ public MonitorTopologySnapshot Snapshot { get; } = snapshot; }
