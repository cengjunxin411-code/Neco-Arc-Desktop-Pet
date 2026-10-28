namespace NecoArcDesktopPet.Animation;

internal enum AngryStage { Caper, Angry1 }

// A session exists only after a user extension has reached the Rotation cap.
internal sealed class AngrySession(long playbackGeneration)
{
    public const string CaperPath = "NecoPetMaterials/angry/caper.gif";
    public const string Angry1Path = "NecoPetMaterials/angry/angry1.gif";
    public AngryStage Stage { get; private set; } = AngryStage.Caper;
    public bool TriggeredByRotationCap => true;
    public bool EscalateToAngry1 { get; private set; }
    public long PlaybackGeneration { get; private set; } = playbackGeneration;

    public void RegisterClick()
    {
        if (Stage == AngryStage.Caper) EscalateToAngry1 = true;
    }

    public void BeginAngry1(long generation)
    {
        Stage = AngryStage.Angry1;
        PlaybackGeneration = generation;
    }
}
