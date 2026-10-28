using System;

namespace NecoArcDesktopPet.Settings;

public sealed record InteractionSettings
{
    public TimeSpan FirstClickWindow { get; set; }
    public TimeSpan SecondClickWindow { get; set; }
    public double DragThresholdDip { get; set; }
    public TimeSpan RotationInitialDuration { get; set; }
    public TimeSpan RotationExtensionPerClick { get; set; }
    public TimeSpan RotationMaxDuration { get; set; }
    public double RocketVerticalSpeed { get; set; }
    public TimeSpan RocketHiddenDuration { get; set; }
    public int CaperLoops { get; set; }
}
