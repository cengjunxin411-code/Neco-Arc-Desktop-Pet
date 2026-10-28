using System;

namespace NecoArcDesktopPet.Settings;

public sealed record SleepSettings
{
    public bool Enabled { get; set; }
    public TimeSpan EnterAfterRest { get; set; }
    public bool AmbientEnabled { get; set; }
    public TimeSpan AmbientMinDelay { get; set; }
    public TimeSpan AmbientMaxDelay { get; set; }
    public int GrouchyLoops { get; set; }
}
