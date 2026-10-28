using System;

namespace NecoArcDesktopPet.Settings;

public static class PetSettingsDefaults
{
    // The only source of accepted user-adjustable defaults; each call is independent.
    public static PetSettings Create() => new()
    {
        Normal = new() { RandomIdleEnabled = true, RandomIdleMinDelay = TimeSpan.FromSeconds(8), RandomIdleMaxDelay = TimeSpan.FromSeconds(14) },
        Rest = new() { Enabled = true, EnterAfter = TimeSpan.FromSeconds(120), AmbientEnabled = true,
            AmbientMinDelay = TimeSpan.FromSeconds(8), AmbientMaxDelay = TimeSpan.FromSeconds(14), AmbientMinLoops = 3, AmbientMaxLoops = 5 },
        Sleep = new() { Enabled = true, EnterAfterRest = TimeSpan.FromSeconds(180), AmbientEnabled = true,
            AmbientMinDelay = TimeSpan.FromSeconds(8), AmbientMaxDelay = TimeSpan.FromSeconds(14), GrouchyLoops = 7 },
        Movement = new() { Enabled = true, SelectionWeight = .35,
            Lope = Profile(90, 150, 600, 45), TrotRelaxed = Profile(130, 180, 500, 25),
            RunExcited = Profile(220, 300, 900, 20), SprintPanic = Profile(300, 450, 1200, 10) },
        Interaction = new() { FirstClickWindow = TimeSpan.FromMilliseconds(200), SecondClickWindow = TimeSpan.FromMilliseconds(200),
            DragThresholdDip = 4, RotationInitialDuration = TimeSpan.FromSeconds(1.5), RotationExtensionPerClick = TimeSpan.FromSeconds(.75),
            RotationMaxDuration = TimeSpan.FromSeconds(5), RocketVerticalSpeed = 800, RocketHiddenDuration = TimeSpan.FromMilliseconds(350), CaperLoops = 5 },
        Diagnostics = new() { MultiClickDiagnosticsEnabled = false },
        Attachment = new() { Enabled = true, SnapDistanceDip = AttachmentSettings.DefaultSnapDistanceDip },
        Voice = new() { Enabled = true, VolumePercent = VoiceSettings.DefaultVolumePercent }
    };

    private static MovementProfileSettings Profile(double speed, double min, double max, int weight) =>
        new() { Speed = speed, MinDistance = min, MaxDistance = max, Weight = weight };

    // Only default startup imports env. Explicit runtime settings bypass this import.
    public static PetSettings CreateForStartup()
    {
        var settings = Create();
        settings.Diagnostics.MultiClickDiagnosticsEnabled = Environment.GetEnvironmentVariable("NECO_MULTI_CLICK_DIAGNOSTICS") == "1";
        settings.Diagnostics.MultiClickDiagnosticsPath = Environment.GetEnvironmentVariable("NECO_MULTI_CLICK_DIAGNOSTICS_PATH");
        return settings;
    }
}
