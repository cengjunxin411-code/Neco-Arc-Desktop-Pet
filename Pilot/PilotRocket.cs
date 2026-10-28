using System;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// Pure world-position model for the Pilot double-click Rocket. Mirrors the ordinary
// RocketSession timing (vertical DIP/s converted to physical px/s, bounded hidden
// duration) but replaces the ordinary "leave the virtual screen" with the Pilot's
// single-monitor safety semantics: the visible native ascent is bounded by the
// starting monitor's WorkArea top, and the "fly off" is represented by the caller
// translating/clipping the image locally after that edge, until the full canvas exits.
internal sealed class PilotRocketSession
{
    private readonly TimeSpan _startedAt;
    private readonly double _speedPhysical;   // physical px/s upward
    private readonly TimeSpan _hiddenDuration;
    private readonly double _topBound;        // starting monitor WorkArea.Top
    private readonly double _exitExtent;
    private TimeSpan? _hiddenAt;
    public PhysicalRect Original { get; }
    public double Top { get; private set; }   // current native rect top, physical px
    public double VisualOffsetPhysical { get; private set; }
    public bool IsHidden => _hiddenAt != null;
    public bool IsComplete { get; private set; }

    public PilotRocketSession(PhysicalRect original, double topBound, double speedPhysical, TimeSpan hiddenDuration, TimeSpan now, double exitExtentPhysical)
    {
        Original = original;
        Top = original.Top;
        _topBound = topBound;
        _speedPhysical = speedPhysical;
        _hiddenDuration = hiddenDuration;
        _startedAt = now;
        _exitExtent = exitExtentPhysical;
    }

    public void Advance(TimeSpan now)
    {
        if (IsComplete) return;
        if (_hiddenAt is { } hidden)
        {
            IsComplete = now - hidden >= _hiddenDuration;
            return;
        }
        var virtualTop=Original.Top-_speedPhysical*Math.Max(0,(now-_startedAt).TotalSeconds);
        Top=Math.Max(_topBound,virtualTop);
        VisualOffsetPhysical=Math.Min(0,virtualTop-_topBound);
        if (-VisualOffsetPhysical >= _exitExtent)
        {
            Top = _topBound;
            _hiddenAt = now;
        }
    }
}

internal static class PilotRocketGeometry
{
    // The ordinary RocketVerticalSpeed is DIP/s. Only this local speed vector is
    // converted through the live DPI; the absolute physical RECT is never rescaled.
    internal static double SpeedPhysical(double speedDip, double dpiY) =>
        double.IsFinite(speedDip) && speedDip >= 0 && double.IsFinite(dpiY) && dpiY > 0 ? speedDip * dpiY : 0;

    // The visible ascent keeps the trusted footprint and X, moving only its top
    // toward the starting monitor's WorkArea top.
    internal static PhysicalRect AtTop(PhysicalRect original, double top) =>
        new(original.Left, top, original.Right, top + original.Height);
}
