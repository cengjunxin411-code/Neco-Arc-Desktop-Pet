namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Represents the observable lifecycle state of a voice playback session.
/// </summary>
public enum VoicePlaybackState
{
    /// <summary>
    /// Initial or reset state; no active playback requested or running.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// Playback request accepted; opening audio resource asynchronously on the media pipeline.
    /// Actual audio output has not yet started.
    /// </summary>
    Opening = 1,

    /// <summary>
    /// Media opened successfully and audio is actively playing.
    /// </summary>
    Playing = 2,

    /// <summary>
    /// Playback was stopped explicitly by caller or pre-empted by a replacement playback request.
    /// </summary>
    Stopped = 3,

    /// <summary>
    /// Audio played naturally to completion.
    /// </summary>
    Ended = 4,

    /// <summary>
    /// Audio failed to open or encountered an error during playback.
    /// </summary>
    Failed = 5
}
