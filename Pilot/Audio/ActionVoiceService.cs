using System;
using System.IO;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>Generalized B.1 one-shot/session adapter; borrows the startup owner's player.</summary>
public sealed class ActionVoiceService : IDisposable
{
    private readonly PilotVoicePlayer _player;
    private readonly Func<int, int> _selector;
    private readonly Func<string, string, string> _resolve;
    private readonly PilotLog? _log;
    private long _lastActionId, _actionId, _voiceSessionId;
    private bool _disposed;

    public ActionVoiceService(PilotVoicePlayer player, Func<int, int>? selector = null, string? explicitRoot = null)
        : this(player, null, selector, explicitRoot) { }

    internal ActionVoiceService(PilotVoicePlayer player, PilotLog? log,
        Func<int, int>? selector = null, string? explicitRoot = null, bool flatFlickRoot = false)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log;
        _selector = selector ?? (count => Random.Shared.Next(count));
        string root = explicitRoot ?? ActionVoiceCatalog.DevelopmentRoot;
        _resolve = flatFlickRoot ? (_, file) => Path.Combine(root, file)
            : (action, file) => Path.Combine(root, action, file);
    }

    public PilotVoicePlayer Player => _player;
    internal Func<bool>? EnabledProvider { get; set; }
    internal Func<double>? VolumeProvider { get; set; }

    public bool TryStart(long actionId, string resourcePath)
    {
        if (_disposed || actionId <= _lastActionId ||
            !ActionVoiceCatalog.TryGet(resourcePath, out var action, out var voices)) return false;
        _lastActionId = actionId; // even a missing/failed asset consumes this action's sole attempt
        try
        {
            if (EnabledProvider?.Invoke() == false) return false;
            int index = _selector(voices.Count);
            if (index < 0 || index >= voices.Count) return false;
            var result = _player.Play(_resolve(action, voices[index]),
                VolumeProvider?.Invoke() ?? PilotVoicePlayer.DefaultVolume);
            if (!result.Accepted) return false;
            _actionId = actionId;
            _voiceSessionId = result.SessionId;
            Fact("action-voice-start", new { action, actionId, session = result.SessionId, file = voices[index] });
            return true;
        }
        catch (Exception e) { Fact("action-voice-failed", new { action, actionId, error = e.Message }); return false; }
    }

    public void End(long actionId, bool completed)
    {
        if (_disposed || actionId != _actionId) return;
        _actionId = 0;
        long session = _voiceSessionId;
        try
        {
            // Retain naturally finishing tails for explicit Rest/Sleep/safety silence.
            if (!completed) Stop();
            Fact("action-voice-release", new { actionId, session, completed });
        }
        catch (Exception e) { Fact("action-voice-failed", new { actionId, error = e.Message }); }
    }

    public void Stop()
    {
        long session = _voiceSessionId;
        _actionId = _voiceSessionId = 0;
        if (session != 0 && session == _player.ActiveSessionId) _player.Stop();
    }

    private void Fact(string kind, object detail) { try { _log?.Write(kind, detail); } catch { } }
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }
}
