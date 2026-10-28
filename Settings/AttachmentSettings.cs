using System;

namespace NecoArcDesktopPet.Settings;

// User preference for the experimental external attachment feature. Only the PMV2
// Pilot consumes it; the ordinary build still serializes it as the seventh partition
// so an ordinary save can never drop the preference, but it never reads the values
// and TryAttachToNearbyWindow keeps its historical detection behavior.
public sealed record AttachmentSettings
{
    // The cap may only tighten the historical detection thresholds. 140 DIP is the
    // largest built-in side threshold, so the default is a strict no-op.
    public const double MinimumSnapDistanceDip = 24;
    public const double MaximumSnapDistanceDip = 140;
    public const double DefaultSnapDistanceDip = 140;

    public bool Enabled { get; set; }
    public double SnapDistanceDip { get; set; }
}
