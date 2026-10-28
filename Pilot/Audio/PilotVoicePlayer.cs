using System;
using System.IO;
using System.Windows.Threading;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Pilot isolated local WAV audio playback engine.
/// Provides non-blocking, generation-guarded, single-voice playback within the WPF Dispatcher lifecycle.
/// </summary>
public sealed class PilotVoicePlayer : IDisposable
{
    /// <summary>
    /// Default voice playback volume (1.0 = 100% full unscaled volume).
    /// </summary>
    public const double DefaultVolume = 1.0;

    private readonly Dispatcher _dispatcher;
    private readonly Func<IVoiceAudioBackend> _backendFactory;
    private readonly PilotLog? _log;

    private IVoiceAudioBackend? _currentBackend;
    private long _currentSessionId;
    private long _activeSessionId;
    private VoicePlaybackState _state = VoicePlaybackState.Idle;
    private double _volume = DefaultVolume;
    private string _currentSafeFileName = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Current observable lifecycle state of the voice engine.
    /// </summary>
    public VoicePlaybackState State => _state;

    /// <summary>
    /// Current active session identifier. Incremented with each accepted play request.
    /// </summary>
    public long ActiveSessionId => _activeSessionId;

    /// <summary>
    /// Current volume level between 0.0 and 1.0.
    /// </summary>
    public double Volume => _volume;

    /// <summary>
    /// Whether the player instance has been disposed.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Sanitized file name of the currently active audio session (does not expose full user directories).
    /// </summary>
    public string SafeFileName => _currentSafeFileName;

    /// <summary>
    /// Event raised when the playback state transitions (e.g. Opening, Playing, Stopped, Ended, Failed).
    /// </summary>
    public event EventHandler<VoicePlaybackEventArgs>? PlaybackStateChanged;

    /// <summary>
    /// Initializes a new instance using the current WPF Dispatcher and default MediaPlayer backend.
    /// </summary>
    public PilotVoicePlayer() : this(null, null) { }

    /// <summary>
    /// Initializes a new instance with optional Dispatcher and backend factory.
    /// </summary>
    public PilotVoicePlayer(Dispatcher? dispatcher, Func<IVoiceAudioBackend>? backendFactory = null)
        : this(dispatcher, backendFactory, null) { }

    /// <summary>
    /// Internal constructor supporting structured PilotLog integration.
    /// </summary>
    internal PilotVoicePlayer(Dispatcher? dispatcher, Func<IVoiceAudioBackend>? backendFactory, PilotLog? log)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _backendFactory = backendFactory ?? (() => new WpfMediaPlayerAudioBackend());
        _log = log;
    }

    /// <summary>
    /// Validates an absolute local file path according to safety rules.
    /// Rejects empty strings, URI schemes, UNC paths, relative paths, and non-WAV extensions.
    /// </summary>
    private static bool ValidatePath(string? path, out string safeFileName, out string? failureReason)
    {
        safeFileName = string.Empty;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            failureReason = "Path cannot be null, empty, or whitespace.";
            return false;
        }

        // Reject network / URL schemes
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("://", StringComparison.Ordinal))
        {
            failureReason = "Network or URI schemes are not permitted. Only local file paths are accepted.";
            return false;
        }

        // Reject UNC paths
        if (path.StartsWith(@"\\") || path.StartsWith("//"))
        {
            failureReason = "Network UNC paths are not permitted.";
            return false;
        }

        // Check if fully qualified absolute path
        if (!Path.IsPathRooted(path) || !Path.IsPathFullyQualified(path))
        {
            failureReason = "Path must be a fully qualified absolute local path.";
            return false;
        }

        // Check extension
        string ext;
        try
        {
            ext = Path.GetExtension(path);
        }
        catch (Exception ex)
        {
            failureReason = $"Invalid path format: {ex.Message}";
            return false;
        }

        if (!string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"Only .wav files are supported. Received '{ext}'.";
            return false;
        }

        try
        {
            safeFileName = Path.GetFileName(path);
        }
        catch
        {
            safeFileName = "audio.wav";
        }

        return true;
    }

    /// <summary>
    /// Submits a request to play a local WAV file.
    /// If an audio session is already active, it is immediately stopped and pre-empted (no overlapping voice).
    /// </summary>
    public VoicePlayResult Play(string absolutePath, double volume = DefaultVolume)
    {
        if (!_dispatcher.CheckAccess())
        {
            return _dispatcher.Invoke(() => Play(absolutePath, volume));
        }

        if (_disposed)
        {
            return VoicePlayResult.Rejected("PilotVoicePlayer is disposed.");
        }

        if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0.0 || volume > 1.0)
        {
            return VoicePlayResult.Rejected($"Volume {volume} out of range [0.0, 1.0].");
        }

        if (!ValidatePath(absolutePath, out string safeFileName, out string? failureReason))
        {
            return VoicePlayResult.Rejected(failureReason!);
        }

        // Pre-empt and clean up any existing active session
        long oldSession = _activeSessionId;
        VoicePlaybackState oldState = _state;
        string oldSafeFile = _currentSafeFileName;

        TeardownCurrentBackend();

        if (oldState == VoicePlaybackState.Opening || oldState == VoicePlaybackState.Playing)
        {
            NotifyStateChanged(new VoicePlaybackEventArgs(oldSession, VoicePlaybackState.Stopped, oldSafeFile));
        }

        // Allocate new session
        long session = ++_currentSessionId;
        _activeSessionId = session;
        _currentSafeFileName = safeFileName;
        _state = VoicePlaybackState.Opening;
        _volume = volume;

        // Check file existence
        if (!File.Exists(absolutePath))
        {
            // Asynchronously dispatch Failed transition so state machine remains non-blocking and observable
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && _activeSessionId == session && _state == VoicePlaybackState.Opening)
                {
                    _state = VoicePlaybackState.Failed;
                    var fnf = new FileNotFoundException("WAV audio file not found on local disk.", safeFileName);
                    NotifyStateChanged(new VoicePlaybackEventArgs(session, VoicePlaybackState.Failed, safeFileName, fnf.Message, fnf));
                }
            });
            return VoicePlayResult.Success(session);
        }

        // Instantiate backend for this session
        try
        {
            var backend = _backendFactory();
            _currentBackend = backend;
            backend.Volume = volume;

            long capturedSession = session;
            backend.MediaOpened += (s, e) => HandleMediaOpened(capturedSession);
            backend.MediaEnded += (s, e) => HandleMediaEnded(capturedSession);
            backend.MediaFailed += (s, e) => HandleMediaFailed(capturedSession, e);

            backend.Open(new Uri(absolutePath, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            _state = VoicePlaybackState.Failed;
            TeardownCurrentBackend();
            NotifyStateChanged(new VoicePlaybackEventArgs(session, VoicePlaybackState.Failed, safeFileName, ex.Message, ex));
            return VoicePlayResult.Success(session);
        }

        return VoicePlayResult.Success(session);
    }

    private void HandleMediaOpened(long session)
    {
        if (_disposed || session != _activeSessionId || _state != VoicePlaybackState.Opening)
            return;

        _state = VoicePlaybackState.Playing;
        try
        {
            _currentBackend?.Play();
        }
        catch (Exception ex)
        {
            HandleMediaFailed(session, new VoiceAudioErrorEventArgs(ex));
            return;
        }

        NotifyStateChanged(new VoicePlaybackEventArgs(session, VoicePlaybackState.Playing, _currentSafeFileName));
    }

    private void HandleMediaEnded(long session)
    {
        if (_disposed || session != _activeSessionId || _state != VoicePlaybackState.Playing)
            return;

        _state = VoicePlaybackState.Ended;
        try { _currentBackend?.Stop(); } catch { }
        NotifyStateChanged(new VoicePlaybackEventArgs(session, VoicePlaybackState.Ended, _currentSafeFileName));
    }

    private void HandleMediaFailed(long session, VoiceAudioErrorEventArgs e)
    {
        if (_disposed || session != _activeSessionId || (_state != VoicePlaybackState.Opening && _state != VoicePlaybackState.Playing))
            return;

        _state = VoicePlaybackState.Failed;
        string file = _currentSafeFileName;
        TeardownCurrentBackend();
        NotifyStateChanged(new VoicePlaybackEventArgs(session, VoicePlaybackState.Failed, file, e.ErrorException?.Message, e.ErrorException));
    }

    /// <summary>
    /// Stops the currently active voice playback session.
    /// Idempotent; calling Stop on an idle, already stopped, failed, or disposed player is completely safe.
    /// </summary>
    public void Stop()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Stop);
            return;
        }

        if (_disposed) return;

        long stoppedSession = _activeSessionId;
        VoicePlaybackState previousState = _state;
        string file = _currentSafeFileName;

        TeardownCurrentBackend();
        _state = VoicePlaybackState.Stopped;

        if (previousState == VoicePlaybackState.Opening || previousState == VoicePlaybackState.Playing)
        {
            NotifyStateChanged(new VoicePlaybackEventArgs(stoppedSession, VoicePlaybackState.Stopped, file));
        }
    }

    /// <summary>
    /// Adjusts the linear playback volume between 0.0 and 1.0.
    /// Applies to current active backend and subsequent playbacks.
    /// </summary>
    public void SetVolume(double volume)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => SetVolume(volume));
            return;
        }

        if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0.0 || volume > 1.0)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be in the range [0.0, 1.0].");

        _volume = volume;
        if (_currentBackend != null)
        {
            try { _currentBackend.Volume = volume; } catch { }
        }
    }

    private void TeardownCurrentBackend()
    {
        if (_currentBackend != null)
        {
            var backend = _currentBackend;
            _currentBackend = null;
            try { backend.Stop(); } catch { }
            try { backend.Close(); } catch { }
            try { backend.Dispose(); } catch { }
        }
    }

    private void NotifyStateChanged(VoicePlaybackEventArgs args)
    {
        try
        {
            _log?.Write("voice-state", new
            {
                session = args.SessionId,
                state = args.State.ToString(),
                file = args.SafeFileName,
                error = args.ErrorMessage
            });
        }
        catch { }

        PlaybackStateChanged?.Invoke(this, args);
    }

    /// <summary>
    /// Releases all native media resources, closes audio sessions, and unhooks event listeners.
    /// Safe and idempotent.
    /// </summary>
    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }

        if (_disposed) return;
        _disposed = true;

        long stoppedSession = _activeSessionId;
        VoicePlaybackState previousState = _state;
        string file = _currentSafeFileName;

        TeardownCurrentBackend();
        _state = VoicePlaybackState.Stopped;
        _activeSessionId = 0;

        if (previousState == VoicePlaybackState.Opening || previousState == VoicePlaybackState.Playing)
        {
            NotifyStateChanged(new VoicePlaybackEventArgs(stoppedSession, VoicePlaybackState.Stopped, file));
        }
    }
}
