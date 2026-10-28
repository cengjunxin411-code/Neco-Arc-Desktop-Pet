using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

internal enum SleepWakeStage { Startle, Grouchy }

internal sealed class SleepWakeSession(long playbackGeneration)
{
    public const string StartlePath = "NecoPetMaterials/sleep/startle_awake.gif";
    public const string GrouchyPath = "NecoPetMaterials/sleep/grouchy.gif";
    // Audited 3 x 100ms per loop: seven complete loops present the expression for 2.1s.
    public static int GrouchyLoops => PetSettingsDefaults.Create().Sleep.GrouchyLoops;
    public SleepWakeStage Stage { get; private set; } = SleepWakeStage.Startle;
    public long PlaybackGeneration { get; private set; } = playbackGeneration;
    public bool EscalateToGrouchy { get; private set; }
    public string ResourcePath => Stage == SleepWakeStage.Startle ? StartlePath : GrouchyPath;
    public void RegisterClick()
    {
        if (Stage == SleepWakeStage.Startle) EscalateToGrouchy = true;
    }
    public void BeginGrouchy(long generation)
    {
        Stage = SleepWakeStage.Grouchy;
        PlaybackGeneration = generation;
    }
}
