using System;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Event arguments delivered when underlying media backend encounters an error.
/// Decouples internal WPF constructor limitations from testable audio backends.
/// </summary>
public sealed class VoiceAudioErrorEventArgs : EventArgs
{
    public Exception ErrorException { get; }

    public VoiceAudioErrorEventArgs(Exception? errorException)
    {
        ErrorException = errorException ?? new InvalidOperationException("Unknown audio playback failure.");
    }
}

/// <summary>
/// Minimal abstraction boundary for the underlying audio playback engine.
/// Allows WPF MediaPlayer in production and deterministic simulation in tests.
/// </summary>
public interface IVoiceAudioBackend : IDisposable
{
    /// <summary>
    /// Linear playback volume between 0.0 and 1.0.
    /// </summary>
    double Volume { get; set; }

    /// <summary>
    /// Asynchronously opens the specified media resource.
    /// </summary>
    void Open(Uri uri);

    /// <summary>
    /// Begins playback of the opened media.
    /// </summary>
    void Play();

    /// <summary>
    /// Stops playback and resets position.
    /// </summary>
    void Stop();

    /// <summary>
    /// Closes the media resource and releases native session handles.
    /// </summary>
    void Close();

    /// <summary>
    /// Raised when the media has finished opening and is ready to play.
    /// </summary>
    event EventHandler? MediaOpened;

    /// <summary>
    /// Raised when media playback has naturally finished.
    /// </summary>
    event EventHandler? MediaEnded;

    /// <summary>
    /// Raised when opening or playback fails.
    /// </summary>
    event EventHandler<VoiceAudioErrorEventArgs>? MediaFailed;
}
