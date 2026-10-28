using System;

namespace NecoArcDesktopPet.Settings;

public sealed record NormalBehaviorSettings
{
    public bool RandomIdleEnabled { get; set; }
    public TimeSpan RandomIdleMinDelay { get; set; }
    public TimeSpan RandomIdleMaxDelay { get; set; }
}
