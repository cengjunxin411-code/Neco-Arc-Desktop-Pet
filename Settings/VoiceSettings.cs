namespace NecoArcDesktopPet.Settings;

// All builds retain this preference; Pilot/Product interprets it at playback boundaries.
public sealed record VoiceSettings
{
    public const int MinimumVolumePercent = 0;
    public const int MaximumVolumePercent = 100;
    public const int DefaultVolumePercent = 80;

    public bool Enabled { get; set; } = true;
    public int VolumePercent { get; set; } = DefaultVolumePercent;
    public double ToLinearVolume() => VolumePercent / 100d;
}
