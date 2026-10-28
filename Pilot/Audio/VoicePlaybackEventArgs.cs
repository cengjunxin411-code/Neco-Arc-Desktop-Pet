using System;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Event arguments delivered when playback state transitions occur.
/// Path information is sanitized to prevent user profile leakage.
/// </summary>
public sealed class VoicePlaybackEventArgs : EventArgs
{
    public long SessionId { get; }
    public VoicePlaybackState State { get; }
    public string SafeFileName { get; }
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }

    public VoicePlaybackEventArgs(
        long sessionId,
        VoicePlaybackState state,
        string safeFileName,
        string? errorMessage = null,
        Exception? exception = null)
    {
        SessionId = sessionId;
        State = state;
        SafeFileName = safeFileName ?? string.Empty;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public override string ToString() =>
        $"Session={SessionId}, State={State}, File={SafeFileName}" +
        (ErrorMessage != null ? $", Error={ErrorMessage}" : string.Empty);
}
