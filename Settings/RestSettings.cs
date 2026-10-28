using System;

namespace NecoArcDesktopPet.Settings;

public sealed record RestSettings
{
    public bool Enabled { get; set; }
    public TimeSpan EnterAfter { get; set; }
    public bool AmbientEnabled { get; set; }
    public TimeSpan AmbientMinDelay { get; set; }
    public TimeSpan AmbientMaxDelay { get; set; }
    public int AmbientMinLoops { get; set; }
    public int AmbientMaxLoops { get; set; }
}
