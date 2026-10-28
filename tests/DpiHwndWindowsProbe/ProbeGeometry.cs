using NecoArcDesktopPet.Coordinates;
using System.Text.Json.Serialization;

namespace DpiHwndProbe;

// Test-only mapping and placement. No production position owner, monitor selection or persistence.
internal readonly record struct Affine(double M11, double M12, double M21, double M22, double OffsetX = 0, double OffsetY = 0);
internal sealed record ViewMapping([property: JsonIgnore] nint Hwnd, long Generation, DpiScale Scale)
{
    [JsonPropertyName("Hwnd")] public string HwndHex => $"0x{Hwnd:X}";
}
internal sealed record ProbeLayout(PhysicalPoint Origin, LocalDipVector CanvasDip, LocalDipVector BodyDip,
    LocalDipVector BodyOffsetDip, LocalDipVector AnchorDip);
internal readonly record struct PixelPlacement(int X, int Y, int Width, int Height);

internal static class ProbeGeometry
{
    // Extracted from ProbeWindow.Measure so the actual measurement path can be tested without a HWND.
    internal static double AnchorError(PhysicalPoint actual, PhysicalPoint placementAnchor,
        PhysicalPoint? cursor, PhysicalVector savedGrab)
    {
        return Math.Max(Math.Abs(actual.X - placementAnchor.X), Math.Abs(actual.Y - placementAnchor.Y));
    }
    internal static double TrackingLag(PhysicalPoint actualAnchor, PhysicalPoint cursor, PhysicalVector savedGrab) =>
        Math.Max(Math.Abs(cursor.X - actualAnchor.X - savedGrab.X), Math.Abs(cursor.Y - actualAnchor.Y - savedGrab.Y));
    internal static string CallbackClassification(long? requestedId, long requestedGeneration, long? currentId,
        long currentGeneration, bool disposed) => disposed ? "disposed" : requestedGeneration != currentGeneration ? "stale" :
        requestedId == null || requestedId != currentId ? "superseded" : "observed";
    internal static bool TryMapping(nint hwnd, bool alive, long generation, Affine to, Affine from,
        double dpiX, double dpiY, out ViewMapping? mapping, out string failure)
    {
        mapping = null; failure = "InvalidHwnd";
        if (hwnd == 0 || !alive) return false;
        failure = "InvalidGeneration"; if (generation < 1) return false;
        failure = "UnsupportedMatrix";
        if (!Diagonal(to) || !Diagonal(from)) return false;
        failure = "MatrixMismatch";
        if (!Near(to.M11 * from.M11, 1) || !Near(to.M22 * from.M22, 1)) return false;
        failure = "DpiMismatch";
        if (!Near(to.M11, dpiX / 96) || !Near(to.M22, dpiY / 96)) return false;
        mapping = new(hwnd, generation, new(to.M11, to.M22)); failure = "None"; return true;
    }
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 1e-9;
    private static bool Diagonal(Affine m) => double.IsFinite(m.M11) && double.IsFinite(m.M22) && m.M11 > 0 && m.M22 > 0 &&
        m.M12 == 0 && m.M21 == 0 && m.OffsetX == 0 && m.OffsetY == 0;
    internal static bool TryLayout(PhysicalPoint anchor, double userMultiplier, ViewMapping mapping,
        out ProbeLayout? layout, out string failure)
    {
        layout = null; failure = "InvalidInput";
        if (!anchor.IsFinite || !double.IsFinite(userMultiplier) || userMultiplier <= 0 ||
            mapping.Hwnd == 0 || mapping.Generation < 1 || !mapping.Scale.IsValid) return false;
        // Body 180x120, canvas 240x180, margin 30, ground (120,150), all original physical px.
        // Recompute from originals every time, never multiply last frame by a DPI ratio.
        var canvas = new PhysicalVector(240 * userMultiplier, 180 * userMultiplier);
        var body = new PhysicalVector(180 * userMultiplier, 120 * userMultiplier);
        var margin = new PhysicalVector(30 * userMultiplier, 30 * userMultiplier);
        var ground = new PhysicalVector(120 * userMultiplier, 150 * userMultiplier);
        failure = "NumericalOverflow";
        if (!canvas.IsFinite || canvas.X <= 0 || canvas.Y <= 0 || margin.X <= 0 || margin.Y <= 0 ||
            !anchor.TryTranslate(new(-ground.X, -ground.Y), out var origin) ||
            !PhysicalRect.TryFromOriginAndSize(origin, canvas.X, canvas.Y, out _) ||
            !mapping.Scale.TryToLocalDip(canvas, out var canvasDip) ||
            !mapping.Scale.TryToLocalDip(body, out var bodyDip) ||
            !mapping.Scale.TryToLocalDip(margin, out var marginDip) ||
            !mapping.Scale.TryToLocalDip(ground, out var anchorDip)) return false;
        layout = new(origin, canvasDip, bodyDip, marginDip, anchorDip); failure = "None"; return true;
    }
    internal static bool TryPixels(ProbeLayout layout, DpiScale scale, out PixelPlacement placement)
    {
        placement = default;
        if (!scale.TryToPhysical(layout.CanvasDip, out var size)) return false;
        var x = Math.Round(layout.Origin.X, MidpointRounding.AwayFromZero);
        var y = Math.Round(layout.Origin.Y, MidpointRounding.AwayFromZero);
        var w = Math.Round(size.X, MidpointRounding.AwayFromZero);
        var h = Math.Round(size.Y, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) ||
            x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
            w < 1 || w > int.MaxValue || h < 1 || h > int.MaxValue || x + w > int.MaxValue || y + h > int.MaxValue) return false;
        placement = new((int)x, (int)y, (int)w, (int)h); return true;
    }
    internal static bool IsSafe(PixelPlacement p, IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (p.Width < 1 || p.Height < 1) return false;
        // Coverage of the window by the UNION of workareas permits crossing a shared seam.
        // This is only an offscreen safety gate; it does not select/project an anchor or assign monitor IDs.
        var right = (double)p.X + p.Width; var bottom = (double)p.Y + p.Height;
        var valid = monitors.Where(m => m.IsValid).Select(m => m.WorkArea).ToArray();
        var edges = new SortedSet<double> { p.X, right };
        foreach (var r in valid) { if (r.Left > p.X && r.Left < right) edges.Add(r.Left); if (r.Right > p.X && r.Right < right) edges.Add(r.Right); }
        var xs = edges.ToArray();
        for (int i = 0; i < xs.Length-1; i++) {
            double coveredTo = p.Y;
            foreach (var r in valid.Where(r => r.Left <= xs[i] && r.Right >= xs[i+1]).OrderBy(r => r.Top)) {
                if (r.Bottom <= coveredTo) continue;
                if (r.Top > coveredTo) break;
                coveredTo = r.Bottom;
                if (coveredTo >= bottom) break;
            }
            if (coveredTo < bottom) return false;
        }
        return true;
    }
}

// Immutable association snapshots and a small lock protect diagnostics from native reentry and concurrent observers.
// This class owns evidence only; it never places a window or commits the ProbeSession anchor.
internal enum PlacementStatus { Requested, Accepted, PendingDpiSettle, Rejected, Stale, Disposed, NativeFailed }
internal readonly record struct TimedCursor(PhysicalPoint Point, long StartedTicks, long CompletedTicks);
internal sealed record PlacementAttempt(long PlacementId, long Generation, string Policy, string Source,
    TimedCursor? Cursor, PhysicalVector SavedGrab, PhysicalPoint DesiredAnchor, PixelPlacement? ExpectedRect,
    ViewMapping? Mapping, long RequestedTicks, long CompletedTicks, PlacementStatus Status);
internal sealed record MeasurementTicket(PlacementAttempt? Placement, long Revision, long Generation,
    bool MappingValid, long StartedTicks);
internal sealed record MeasurementResult(long? PlacementId, long Generation, string Classification,
    double? PlacementGeometryErrorPx, double? CursorTrackingLagPx, long GeometryStartedTicks, long GeometryCompletedTicks,
    long? PlacementCompletedTicks, long? CursorStartedTicks, long? CursorCompletedTicks);

internal sealed class PlacementEvidence
{
    private readonly object sync = new();
    private long nextId, revision, latestId;
    private bool disposed;
    private PlacementAttempt? accepted;
    private long geometryCount, geometryFailed, inconclusiveCount, trackingCount;
    private long trackingInconclusiveCount;
    private readonly Dictionary<PlacementStatus,long> states = new();
    private long rectCount, rectFailed, rectInconclusive;
    private double maxRect;
    private double maxGeometry, maxTracking;
    internal PlacementAttempt? Accepted { get { lock (sync) return accepted; } }
    internal PlacementAttempt Request(long generation, string policy, string source, TimedCursor? cursor,
        PhysicalVector grab, PhysicalPoint anchor, PixelPlacement? rect, ViewMapping? mapping, long ticks)
    {
        lock (sync) {
            latestId = ++nextId; revision++;
            return new(latestId,generation,policy,source,cursor,grab,anchor,rect,mapping,ticks,0,
                disposed ? PlacementStatus.Disposed : PlacementStatus.Requested);
        }
    }
    internal PlacementAttempt Complete(PlacementAttempt request, PlacementStatus status, long generation, long ticks)
    {
        lock (sync) {
            if (disposed) status = PlacementStatus.Disposed;
            else if (request.PlacementId != latestId) status = PlacementStatus.Stale;
            else if (status == PlacementStatus.Accepted && request.Generation != generation) status = PlacementStatus.PendingDpiSettle;
            else if (status == PlacementStatus.Accepted && (request.Mapping == null || request.Mapping.Generation != generation ||
                !request.Mapping.Scale.IsValid || request.ExpectedRect == null || !request.DesiredAnchor.IsFinite || ticks < request.RequestedTicks))
                status = PlacementStatus.Rejected;
            var result = request with { Status = status, CompletedTicks = ticks };
            states[status] = states.GetValueOrDefault(status)+1;
            revision++;
            if (status == PlacementStatus.Accepted) accepted = result;
            return result;
        }
    }
    internal void Cancel() { lock (sync) { accepted = null; latestId = 0; revision++; } }
    internal void Dispose() { lock (sync) { disposed = true; accepted = null; revision++; } }
    internal MeasurementTicket BeginMeasurement(PlacementAttempt? placement, long generation, bool mappingValid, long ticks)
    { lock (sync) return new(placement,revision,generation,mappingValid,ticks); }
    internal MeasurementResult FinishMeasurement(MeasurementTicket ticket, long generation, PhysicalPoint actualAnchor,
        double rectError, double bodyError, TimedCursor? cursor, long ticks, bool includeAnchor = true, bool trackingRequested = false)
    {
        lock (sync) {
            var p = ticket.Placement;
            string classification = disposed ? "disposed" : p == null ? "inconclusive" :
                p.Status != PlacementStatus.Accepted ? p.Status.ToString() :
                p.Generation != generation || ticket.Generation != generation ? "stale" :
                p.PlacementId != accepted?.PlacementId || p.PlacementId != latestId ? "superseded" :
                ticket.Revision != revision ? "inconclusive" : !ticket.MappingValid ? "invalidMapping" :
                ticks < ticket.StartedTicks || ticket.StartedTicks < p.CompletedTicks || !actualAnchor.IsFinite ||
                !double.IsFinite(rectError) || rectError < 0 || !double.IsFinite(bodyError) || bodyError < 0 ? "inconclusive" : "accepted";
            double? geometry = null;
            if (classification == "accepted") {
                geometry = Math.Max(Math.Max(rectError,bodyError), includeAnchor ?
                    ProbeGeometry.AnchorError(actualAnchor,p!.DesiredAnchor,null,p.SavedGrab) : 0);
                geometryCount++; maxGeometry = Math.Max(maxGeometry,geometry.Value);
                if (geometry > 2) geometryFailed++;
            } else inconclusiveCount++;
            // Tracking is a temporal observation and has no geometry tolerance/verdict. Invalid/reversed times are not fabricated as zero.
            double? lag = null;
            if (classification == "accepted" && actualAnchor.IsFinite && cursor is { } c && c.Point.IsFinite && c.StartedTicks >= ticket.StartedTicks &&
                c.CompletedTicks >= c.StartedTicks && ticks >= c.CompletedTicks && p != null && p.SavedGrab.IsFinite) {
                lag = ProbeGeometry.TrackingLag(actualAnchor,c.Point,p.SavedGrab);
                if (double.IsFinite(lag.Value)) { trackingCount++; maxTracking = Math.Max(maxTracking,lag.Value); } else lag = null;
            }
            if ((trackingRequested || cursor != null) && lag == null) trackingInconclusiveCount++;
            return new(p?.PlacementId,generation,classification,geometry,lag,ticket.StartedTicks,ticks,
                p?.CompletedTicks,cursor?.StartedTicks,cursor?.CompletedTicks);
        }
    }
    internal void ObserveNativeRect(PlacementAttempt? placement, long generation, double? error)
    {
        lock (sync) {
            if (disposed || placement?.Status != PlacementStatus.Accepted || placement.Generation != generation ||
                placement.PlacementId != accepted?.PlacementId || placement.PlacementId != latestId || error is not { } delta ||
                !double.IsFinite(delta) || delta < 0) { rectInconclusive++; return; }
            rectCount++; maxRect = Math.Max(maxRect,delta); if (delta > 2) rectFailed++;
        }
    }
    internal object Summary { get { lock (sync) return new { requested = nextId, geometryCount, geometryFailed,
        inconclusiveCount, maxPlacementGeometryErrorPx = geometryCount == 0 ? (double?)null : maxGeometry,
        trackingCount, trackingInconclusiveCount, trackingFailed = "N/A: temporal observation has no geometry threshold",
        maxCursorTrackingLagPx = trackingCount == 0 ? (double?)null : maxTracking, states = new Dictionary<PlacementStatus,long>(states),
        rectCount, rectFailed, rectInconclusive, maxNativeRectErrorPx = rectCount == 0 ? (double?)null : maxRect }; } }
}

internal sealed class ProbeSession
{
    internal PhysicalPoint Anchor { get; private set; }
    internal double UserMultiplier { get; }
    internal long Generation { get; private set; } = 1;
    internal nint Hwnd { get; private set; }
    internal ViewMapping? Mapping { get; private set; }
    internal bool Dragging { get; private set; }
    internal PhysicalRect? Suggested { get; private set; }
    internal GrabMoveOutcome LastMoveOutcome { get; private set; }
    private PhysicalVector grab;
    private long gestureRevision;
    private bool closed;
    private readonly bool guardGestureCompletion;
    internal ProbeSession(PhysicalPoint anchor, double multiplier, nint hwnd,bool guardGestureCompletion=false)
    {
        if (!anchor.IsFinite || !double.IsFinite(multiplier) || multiplier <= 0 || hwnd == 0) throw new ArgumentException("Invalid probe session.");
        Anchor = anchor; UserMultiplier = multiplier; Hwnd = hwnd;this.guardGestureCompletion=guardGestureCompletion;
    }
    internal long Invalidate(PhysicalRect? suggested = null)
    {
        Mapping = null; Suggested = suggested is { IsValid: true } ? suggested : null;
        return Generation = checked(Generation + 1);
    }
    internal void SuspendMapping() { Mapping = null; }
    internal bool Accept(ViewMapping mapping)
    {
        if (closed || mapping.Hwnd != Hwnd || mapping.Generation != Generation || !mapping.Scale.IsValid) return false;
        Mapping = mapping; return true;
    }
    internal bool BeginGrab(PhysicalPoint cursor)
    {
        if (closed || Mapping == null || !PhysicalVector.TryBetween(Anchor, cursor, out var offset)) return false;
        gestureRevision++;grab = offset; Dragging = true; return true;
    }
    internal bool MoveGrab(PhysicalPoint cursor, Func<PhysicalPoint,bool>? acceptPlacement = null)
    {
        if (closed) { LastMoveOutcome = GrabMoveOutcome.Closed; return false; }
        if (!Dragging) { LastMoveOutcome = GrabMoveOutcome.NotDragging; return false; }
        if (Mapping == null) { LastMoveOutcome = GrabMoveOutcome.MappingUnavailable; return false; }
        if (!cursor.TryTranslate(new(-grab.X, -grab.Y), out var target) ||
            !ProbeGeometry.TryLayout(target, UserMultiplier, Mapping, out _, out _)) {
            LastMoveOutcome = GrabMoveOutcome.InvalidGeometry; return false;
        }
        var owner=gestureRevision;
        if (acceptPlacement != null && !acceptPlacement(target)) { LastMoveOutcome = GrabMoveOutcome.PlacementDeclined; return false; }
        if(guardGestureCompletion && (closed || !Dragging || owner!=gestureRevision)) { LastMoveOutcome=closed ? GrabMoveOutcome.Closed : GrabMoveOutcome.NotDragging;return false; }
        Anchor = target; LastMoveOutcome = GrabMoveOutcome.Accepted; return true;
    }
    internal void CancelGrab() { gestureRevision++;Dragging = false; grab = default; }
    internal void Close() { closed = true; CancelGrab(); Invalidate(); Hwnd = 0; }
    internal void Rebind(nint hwnd) { if (closed || hwnd == 0) throw new InvalidOperationException("Cannot rebind."); CancelGrab(); Invalidate(); Hwnd = hwnd; }
}

internal enum GrabMoveOutcome { Accepted, Closed, NotDragging, MappingUnavailable, InvalidGeometry, PlacementDeclined }

// Read-only sampling budget shared with the verification executable. Never accepts mapping or places HWND.
// Six event checkpoints, twice each, within 500ms and >=16ms apart; one reserved terminal settle sample.
internal sealed class MappingObservationBudget
{
    private readonly bool enabled;
    private readonly long frequency;
    private readonly long[] lastTicks = new long[6];
    private readonly int[] counts = new int[6];
    private long generation, openedTicks;
    private bool active, terminalTaken;
    private int taken;
    private long totalTaken, suppressed;
    internal MappingObservationBudget(bool enabled, long frequency)
    { this.enabled=enabled; this.frequency=frequency>0 ? frequency : throw new ArgumentOutOfRangeException(nameof(frequency)); }
    internal void Begin(long currentGeneration,long ticks)
    {
        if (!enabled) return;
        generation=currentGeneration; openedTicks=ticks; active=true; terminalTaken=false; taken=0;
        Array.Clear(lastTicks); Array.Clear(counts);
    }
    internal bool TryTake(long requestedGeneration,long currentGeneration,int kind,long ticks,bool closed)
    {
        if (!enabled) return false;
        if (!active || closed || requestedGeneration!=generation || currentGeneration!=generation ||
            kind<0 || kind>6 || ticks<openedTicks) { suppressed++; return false; }
        if (kind==6) {
            if (terminalTaken) { suppressed++; return false; }
            terminalTaken=true;
        } else {
            if ((ticks-openedTicks)/(double)frequency>0.5 || taken>=12 || counts[kind]>=2 ||
                counts[kind]>0 && (ticks-lastTicks[kind])/(double)frequency<0.016) { suppressed++; return false; }
            lastTicks[kind]=ticks; counts[kind]++; taken++;
        }
        totalTaken++; return true;
    }
    internal void End(long owner) { if (generation==owner) active=false; }
    internal object Summary => new { enabled,totalTaken,suppressed, maximumSamplesPerGeneration=13,
        observationWindowMs=500,minimumSameCheckpointMs=16, behavior="read-only; no scheduling or recovery" };
}

// Optional UI-thread-only counters: fixed arrays, no per-input history, no hot-path I/O.
// Dispatcher durations include nested work and waiting; they are not additive CPU time or DWM frames.
internal sealed class DragStallTrace
{
    internal bool Enabled { get; }
    internal bool HasActiveDispatcher { get { foreach(var operation in operations) if(operation != null) return true; return false; } }
    private readonly long[] moves = new long[6], priorityCounts = new long[11], priorityTicks = new long[11];
    private readonly object?[] operations = new object?[16];
    private readonly int[] priorities = new int[16];
    private readonly long[] operationStarts = new long[16];
    private long nativeMouseMoves, wpfMouseMoves, cursorFailures, lostCapture, releases, renders, layouts, pendingMoves;
    private long nativeCalls, nativeTicks, maxNativeTicks, handlerTicks, maxHandlerTicks, dispatcherOverflow, dispatcherUnmatched;
    private int nativeDepth, maxNativeDepth;
    private long mappingInvalidTicks, mappingRecoveredTicks, firstWpfTicks, lastWpfTicks;
    private TimedCursor? latestCursor;
    private bool lastCaptured, lastDragging, lastMappingValid;
    internal DragStallTrace(bool enabled) { Enabled = enabled; }
    internal void NativeMouse() { if (Enabled) nativeMouseMoves++; }
    internal void Input(long ticks, bool captured, bool dragging, bool mappingValid)
    {
        if (!Enabled) return;
        wpfMouseMoves++; if (firstWpfTicks == 0) firstWpfTicks=ticks; lastWpfTicks=ticks;
        lastCaptured=captured; lastDragging=dragging; lastMappingValid=mappingValid;
    }
    internal void Cursor(TimedCursor? cursor) { if (Enabled) { if (cursor == null) cursorFailures++; else latestCursor=cursor; } }
    internal void Move(GrabMoveOutcome outcome) { if (Enabled) moves[(int)outcome]++; }
    internal void PendingMove() { if (Enabled) pendingMoves++; }
    internal void Handler(long ticks) { if (Enabled) { handlerTicks+=ticks; maxHandlerTicks=Math.Max(maxHandlerTicks,ticks); } }
    internal void NativeEnter() { if (Enabled) { nativeCalls++; maxNativeDepth=Math.Max(maxNativeDepth,++nativeDepth); } }
    internal void NativeExit(long ticks) { if (Enabled) { nativeDepth--;nativeTicks+=ticks;maxNativeTicks=Math.Max(maxNativeTicks,ticks); } }
    internal void Mapping(bool valid, long ticks) { if (Enabled) { if (valid) mappingRecoveredTicks=ticks; else mappingInvalidTicks=ticks; } }
    internal void Render() { if (Enabled) renders++; }
    internal void Layout() { if (Enabled) layouts++; }
    internal void CaptureLost() { if (Enabled) lostCapture++; }
    internal void Release() { if (Enabled) releases++; }
    internal void DispatcherStart(object operation, int priority, long ticks)
    {
        if (!Enabled) return;
        for(var i=0;i<operations.Length;i++) if(operations[i]==null) {
            operations[i]=operation;priorities[i]=priority;operationStarts[i]=ticks;return;
        }
        dispatcherOverflow++;
    }
    internal void DispatcherEnd(object operation, long ticks)
    {
        if (!Enabled) return;
        for(var i=0;i<operations.Length;i++) if(ReferenceEquals(operations[i],operation)) {
            var p=priorities[i]; if(p>=0 && p<priorityCounts.Length) { priorityCounts[p]++;priorityTicks[p]=Math.Max(priorityTicks[p],ticks-operationStarts[i]); }
            operations[i]=null;return;
        }
        dispatcherUnmatched++;
    }
    internal object Summary => new {
        Enabled,nativeMouseMoves,wpfMouseMoves,cursorFailures,lostCapture,releases,renders,layouts,pendingMoves,
        moves=Enum.GetValues<GrabMoveOutcome>().ToDictionary(x => x.ToString(),x => moves[(int)x]),
        nativeCalls,nativeTicks,maxNativeTicks,nativeDepth,maxNativeDepth,handlerTicks,maxHandlerTicks,
        mappingInvalidTicks,mappingRecoveredTicks,firstWpfTicks,lastWpfTicks,latestCursor,lastCaptured,lastDragging,lastMappingValid,
        dispatcher=Enumerable.Range(0,11).Select(p => new { priority=p,count=priorityCounts[p],maxInclusiveTicks=priorityTicks[p] }).ToArray(),
        dispatcherOverflow,dispatcherUnmatched,activeDispatcherSlots=operations.Count(x => x!=null),
        storage="fixed 6 outcomes / 11 priorities / 16 operation slots; no input list", presentation="not DWM frames" };
}
