using System;
using System.Windows.Media;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Production audio backend backed by WPF's System.Windows.Media.MediaPlayer.
/// Respects WPF Dispatcher thread affinity.
/// </summary>
public sealed class WpfMediaPlayerAudioBackend : IVoiceAudioBackend
{
    private readonly MediaPlayer _player = new();
    private bool _disposed;

    public WpfMediaPlayerAudioBackend()
    {
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0.0, 1.0);
    }

    public void Open(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Open(uri);
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Play();
    }

    public void Stop()
    {
        if (_disposed) return;
        _player.Stop();
    }

    public void Close()
    {
        if (_disposed) return;
        _player.Close();
    }

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<VoiceAudioErrorEventArgs>? MediaFailed;

    private void OnMediaOpened(object? sender, EventArgs e) => MediaOpened?.Invoke(this, e);
    private void OnMediaEnded(object? sender, EventArgs e) => MediaEnded?.Invoke(this, e);
    private void OnMediaFailed(object? sender, ExceptionEventArgs e) =>
        MediaFailed?.Invoke(this, new VoiceAudioErrorEventArgs(e.ErrorException));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        try { _player.Close(); } catch { }
    }
}
