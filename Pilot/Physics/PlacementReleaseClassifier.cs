using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal enum PlacementReleaseKind { Unavailable, Stationary, Slow, Throw }

// Bounded placement-intent classifier for the release-time Idle edge seat.
//
// It runs the same ReleaseVelocityEstimator algorithm and tuning the physics
// release uses, but on the real drag CURSOR input points and independently of
// the physics lease, so the seat decision does not depend on the physics mode.
// It never creates a session, a velocity or a displacement; it only classifies
// one release.
//
// Threshold: 400 DIP/s, never relaxed. A deliberate placement drag is normally
// far below it (a 3 DIP step per 12 ms input is 250 DIP/s), while a fling is an
// order of magnitude faster. The value is in DIP/s so the boundary is DPI
// independent; callers convert it with the live X scale.
//
// The estimator's own window is 100 ms. If the hand stops just before MouseUp,
// that window can be starved even though the real input is a slow placement.
// This classifier therefore keeps its own bounded tail of recent real samples and,
// ONLY when the estimator reports InsufficientSamples, applies the SAME 400 DIP/s
// threshold to the median recent sample speed over at most 250 ms. A failed/absent
// tail stays Unavailable; InvalidInput/InvalidTuning are never overridden.
internal sealed class PlacementReleaseClassifier
{
    internal const double MaximumSpeedDipPerSecond = 400;
    internal const double TailWindowSeconds = .25;
    internal const double TailMinimumSpanSeconds = .015;
    internal const int TailMaximumSamples = 24;
    private readonly ReleaseVelocityEstimator estimator;
    private readonly double maximumSpeedDip;
    private readonly long frequency;
    private readonly List<(long Tick, PhysicalPoint Point)> tail = new();
    internal PlacementReleaseClassifier(long frequency, double maximumSpeedDipPerSecond,
        ReleaseVelocityTuning? tuning = null)
    {
        estimator = new(frequency, tuning);
        maximumSpeedDip = maximumSpeedDipPerSecond;
        this.frequency = frequency;
    }
    internal int BufferedSamples => estimator.BufferedSamples;
    internal int TailSamples => tail.Count;
    // One read-only observation per Estimate, for the bounded release diagnostics.
    internal ReleaseDiagnostic? LastDiagnostic => estimator.LastDiagnostic;
    internal double? LastTailSpeedPx { get; private set; }
    internal string LastBranch { get; private set; } = "none";

    internal bool Add(long timestamp, PhysicalPoint position)
    {
        LastTailSpeedPx = null;
        if (!estimator.Add(timestamp, position)) { tail.Clear(); return false; }
        tail.Add((timestamp, position));
        while (tail.Count > TailMaximumSamples) tail.RemoveAt(0);
        return true;
    }

    internal void Reset()
    {
        estimator.Reset();
        tail.Clear();
        LastTailSpeedPx = null;
    }

    internal PlacementReleaseKind Classify(long timestamp, double dpiX, out double? speedPx, out ReleaseFailure failure)
    {
        speedPx = null; failure = ReleaseFailure.None; LastBranch = "none"; LastTailSpeedPx = null;
        if (!double.IsFinite(dpiX) || dpiX <= 0 || !double.IsFinite(maximumSpeedDip) || maximumSpeedDip <= 0)
        { failure = ReleaseFailure.InvalidTuning; return PlacementReleaseKind.Unavailable; }
        var estimate = estimator.Estimate(timestamp);
        failure = estimate.Reason;
        if (estimate.Reason == ReleaseFailure.Stationary) { LastBranch = "estimator-stationary"; return PlacementReleaseKind.Stationary; }
        if (estimate.Reason == ReleaseFailure.None)
        {
            var v = estimate.Velocity;
            var speed = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (!double.IsFinite(speed)) return PlacementReleaseKind.Unavailable;
            speedPx = speed;
            LastBranch = "estimator";
            return speed <= maximumSpeedDip * dpiX ? PlacementReleaseKind.Slow : PlacementReleaseKind.Throw;
        }
        // The short window is starved (never invalid): apply the same threshold to
        // the bounded tail of real recent input. A fast recent approach stays a throw.
        if (estimate.Reason != ReleaseFailure.InsufficientSamples) return PlacementReleaseKind.Unavailable;
        var limit = maximumSpeedDip * dpiX;
        // The median protects against one bad frame, so it could also dilute a real
        // final flick. The last real input pair is therefore a one-way veto: a fast
        // final move is a throw whatever the rest of the tail looks like.
        if (LastPairSpeed(timestamp) is { } lastPair && lastPair > limit)
        {
            LastTailSpeedPx = lastPair; speedPx = lastPair; LastBranch = "tail-last-pair";
            return PlacementReleaseKind.Throw;
        }
        if (TailSpeed(timestamp) is not { } tailSpeed) return PlacementReleaseKind.Unavailable;
        LastTailSpeedPx = tailSpeed; speedPx = tailSpeed; LastBranch = "tail";
        return tailSpeed <= limit ? PlacementReleaseKind.Slow : PlacementReleaseKind.Throw;
    }

    // Speed of the last real input pair inside the tail window, or null when the
    // last sample is not part of a usable pair. Used only as a throw veto.
    private double? LastPairSpeed(long releaseTimestamp)
    {
        if (tail.Count < 2) return null;
        var last = tail[^1];
        var previous = tail[^2];
        if (last.Tick > releaseTimestamp || previous.Tick > releaseTimestamp) return null;
        var dt = (last.Tick - previous.Tick) / (double)frequency;
        if (dt < TailMinimumSpanSeconds / 2) return null;
        var dx = last.Point.X - previous.Point.X;
        var dy = last.Point.Y - previous.Point.Y;
        var speed = Math.Sqrt(dx * dx + dy * dy) / dt;
        return double.IsFinite(speed) ? speed : null;
    }

    // Median pair speed over the recent real samples. Robust to one bad frame and
    // measured only on monotonic timestamps inside a bounded window.
    private double? TailSpeed(long releaseTimestamp)
    {
        var active = new List<(long Tick, PhysicalPoint Point)>();
        foreach (var sample in tail)
            if ((releaseTimestamp - sample.Tick) / (double)frequency <= TailWindowSeconds + 1e-12 &&
                sample.Tick <= releaseTimestamp) active.Add(sample);
        if (active.Count < 3 || (active[^1].Tick - active[0].Tick) / (double)frequency < TailMinimumSpanSeconds) return null;
        var speeds = new List<double>();
        for (var i = 0; i < active.Count; i++)
            for (var j = i + 1; j < active.Count; j++)
            {
                var dt = (active[j].Tick - active[i].Tick) / (double)frequency;
                if (dt < TailMinimumSpanSeconds) continue;
                var dx = active[j].Point.X - active[i].Point.X;
                var dy = active[j].Point.Y - active[i].Point.Y;
                var speed = Math.Sqrt(dx * dx + dy * dy) / dt;
                if (double.IsFinite(speed)) speeds.Add(speed);
            }
        if (speeds.Count == 0) return null;
        speeds.Sort();
        var middle = speeds.Count / 2;
        return speeds.Count % 2 == 1 ? speeds[middle] : (speeds[middle - 1] + speeds[middle]) / 2;
    }
}
