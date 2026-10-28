using System;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

// Real-input stationarity proof for the release-time Idle edge seat.
//
// The velocity estimator works on a short (100 ms) window, so a slow hand that
// stops for a moment before the button comes up can leave too few fresh samples
// to estimate anything. This tracker answers a different, simpler question with
// every real drag input event: has the CURSOR stayed inside one small box for at
// least 120 ms?
//
// It is deliberately independent of the velocity estimator and of the HWND
// origin: a clamped window must never look stationary while the hand keeps
// moving, and a real mouse's resting jitter must not look like motion. A slowly
// creeping hand leaves the box and restarts the proof instead of accumulating.
// Invalid or non-monotonic samples are refused instead of being guessed.
internal sealed class CursorDwellTracker
{
    // One DIP of rest jitter is tolerated (1.5 px at 150%, 2 px at 200%). The run
    // box is the diameter, so +/-1 physical px jitter always fits.
    internal const double ToleranceDip = 1;
    internal const double RequiredStationaryMilliseconds = 120;

    private readonly long frequency;
    private readonly double tolerancePx;
    private long lastMotion;
    private long lastSample;
    private double minX, maxX, minY, maxY;
    private bool any;

    internal CursorDwellTracker(long frequency, double tolerancePx = 1.5)
    {
        this.frequency = frequency;
        this.tolerancePx = tolerancePx;
    }

    internal bool Valid => frequency is > 0 and <= 1_000_000_000_000 && double.IsFinite(tolerancePx) && tolerancePx > 0;
    internal bool HasSamples => any;
    internal int Samples { get; private set; }
    internal double Tolerance => tolerancePx;

    internal void Reset()
    {
        any = false; Samples = 0; lastMotion = 0; lastSample = 0;
        minX = maxX = minY = maxY = 0;
    }

    internal bool Add(long timestamp, PhysicalPoint cursor)
    {
        if (!Valid || timestamp < 0 || !cursor.IsFinite || (any && timestamp <= lastSample))
        { Reset(); return false; }
        var box = 2 * tolerancePx;
        var stays = any && Math.Max(maxX, cursor.X) - Math.Min(minX, cursor.X) <= box &&
            Math.Max(maxY, cursor.Y) - Math.Min(minY, cursor.Y) <= box;
        if (!stays)
        {
            // A real movement (or the first sample) starts a fresh stationary run.
            minX = maxX = cursor.X; minY = maxY = cursor.Y; lastMotion = timestamp;
        }
        else
        {
            minX = Math.Min(minX, cursor.X); maxX = Math.Max(maxX, cursor.X);
            minY = Math.Min(minY, cursor.Y); maxY = Math.Max(maxY, cursor.Y);
        }
        any = true; lastSample = timestamp; Samples++;
        return true;
    }

    // Returns the proven stationary span in milliseconds, or null when the real
    // input track cannot prove a continuous rest of the required length. Every
    // accepted sample is a real OS cursor position with a monotonic timestamp, so
    // a release without an intervening sample means the cursor did not move. Never
    // guesses and never substitutes the HWND position for the hand.
    internal double? StationaryMilliseconds(long releaseTimestamp)
    {
        if (!any || !Valid || releaseTimestamp < lastSample) return null;
        var milliseconds = (releaseTimestamp - lastMotion) * 1000d / frequency;
        return double.IsFinite(milliseconds) && milliseconds >= RequiredStationaryMilliseconds ? milliseconds : null;
    }
}
