using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Threading;
using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet.Animation;

internal sealed class GifFramePlayer : IDisposable
{
    private sealed record GifClip(int Width, int Height, BitmapSource[] Frames, TimeSpan[] Durations);

    private readonly Image _image;
    private readonly Dictionary<string, GifClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _frameClock = new();
    private GifClip _clip;
    private GifPlaybackCursor _cursor;
    private TimeSpan _remaining;
    private bool _disposed;
    private Func<bool>? _finishAtLoopBoundary;

    public event EventHandler? Completed;
    public event EventHandler? ClipChanged;

    public int PixelWidth => _clip.Width;
    public int PixelHeight => _clip.Height;
    public string ResourcePath { get; private set; }
    public long PlaybackGeneration { get; private set; }
    public bool IsComplete => _cursor.IsComplete;

#if PMV2_PILOT
    internal bool PilotPresentationHeld { get; set; }
    internal int PilotFrameIndex => _cursor.FrameIndex;
    internal bool PilotFrameTimerEnabled => _timer.IsEnabled;
    internal event EventHandler? PilotFrameAdvanced;
    internal BitmapSource PilotCurrentFrame => _clip.Frames[_cursor.FrameIndex];
    internal void PilotRestoreCurrentFrame() => _image.Source = _clip.Frames[_cursor.FrameIndex];
    // Cache the union of every decoded/coalesced frame, never a single frame fit.
    private readonly Dictionary<(string Path, int Start, int End), Rect> _pilotVisibleBounds = new();
    internal Rect PilotVisibleUnion => PilotVisibleUnionFor(ResourcePath, _cursor.RangeStart, _cursor.RangeEnd);
    internal Rect PilotVisibleUnionFor(string resourcePath, int start = 0, int end = -1)
    {
            if (!_clips.TryGetValue(resourcePath, out var clip)) return Rect.Empty;
            if (end < 0) end = clip.Frames.Length - 1;
            if (start < 0 || end < start || end >= clip.Frames.Length)
                throw new ArgumentOutOfRangeException(nameof(end));
            var key = (resourcePath.ToUpperInvariant(), start, end);
            if (_pilotVisibleBounds.TryGetValue(key, out var cached)) return cached;
            var union = Rect.Empty;
            for (var i = start; i <= end; i++)
            {
                var frame = clip.Frames[i];
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var stride = bitmap.PixelWidth * 4;
                var bytes = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(bytes, stride, 0);
                int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = 0, bottom = 0;
                for (var y = 0; y < bitmap.PixelHeight; y++)
                    for (var x = 0; x < bitmap.PixelWidth; x++)
                        if (bytes[y * stride + x * 4 + 3] != 0)
                        {
                            left = Math.Min(left, x); top = Math.Min(top, y);
                            right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                        }
                if (right > left && bottom > top) union.Union(new Rect(left, top, right - left, bottom - top));
            }
            _pilotVisibleBounds.Add(key, union);
            return union;
    }
#endif

    public GifFramePlayer(Image image, string resourcePath)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);

        _clip = LoadClip(resourcePath);
        ResourcePath = resourcePath;
        _clips.Add(resourcePath, _clip);
        _cursor = new GifPlaybackCursor(_clip.Frames.Length);
        _remaining = _clip.Durations[0];
        _timer = new DispatcherTimer { Interval = _remaining };
        _timer.Tick += OnTick;
        _image.Source = _clip.Frames[0];
    }

    public bool TrySwitchTo(string resourcePath, bool loop = true)
        => TrySwitchToCore(resourcePath, loop, minimumDuration: null);

    public bool TrySwitchToRange(string resourcePath, int start, int end, bool loop = true)
        => TrySwitchToCore(resourcePath, loop, minimumDuration: null, start: start, end: end);

    // One normal clip admission, then one full pass over the decoded clip, then the
    // last tailFrames frames repeat forever. The tail transition never bumps
    // PlaybackGeneration, never raises ClipChanged or Completed, never changes
    // ResourcePath or the played range (so PilotVisibleUnion stays identical) and
    // reuses the single existing DispatcherTimer. A later switch installs a fresh
    // cursor and therefore cancels the tail loop.
    public bool TrySwitchToHoldingTail(string resourcePath, int tailFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (tailFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tailFrames),
                "Holding-tail playback requires at least one tail frame.");
        }

        return TrySwitchToCore(resourcePath, loop: false, minimumDuration: null, tailFrames: tailFrames);
    }

    public bool TrySwitchToForDuration(string resourcePath, TimeSpan minimumDuration)
        => TrySwitchToCore(resourcePath, loop: false, minimumDuration);

    public bool TrySwitchToLoops(string resourcePath, int repeatCount)
        => TrySwitchToCore(resourcePath, loop: false, minimumDuration: null, repeatCount);

    public bool TrySwitchToUntil(string resourcePath, Func<bool> finishAtLoopBoundary)
    {
        ArgumentNullException.ThrowIfNull(finishAtLoopBoundary);
        if (!TrySwitchTo(resourcePath)) return false;
        _finishAtLoopBoundary = finishAtLoopBoundary;
        return true;
    }

    // A finite minimum duration is rounded up to whole decoded GIF loops.
    // Other callers keep their existing endless-loop / play-once behavior.
    private bool TrySwitchToCore(string resourcePath, bool loop, TimeSpan? minimumDuration, int loops = 1,
        int start = 0, int end = -1, int tailFrames = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (loops < 1) throw new ArgumentOutOfRangeException(nameof(loops));
        if (start < 0 || end < -1 || end != -1 && end < start)
            throw new ArgumentOutOfRangeException(nameof(end));
        if (tailFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(tailFrames));
        if (tailFrames > 0 && (loop || loops != 1))
            throw new ArgumentException(
                "Tail playback replaces completion and requires loop:false with a single pass.", nameof(tailFrames));
        if (minimumDuration is { } minimum && (loop || minimum <= TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(minimumDuration), "Finite duration requires a positive duration and loop:false.");

        if (!_clips.TryGetValue(resourcePath, out var nextClip))
        {
            if (!TryCacheClip(resourcePath)) return false;
            nextClip = _clips[resourcePath];
        }

        if (end < 0) end = nextClip.Frames.Length - 1;
        if (end >= nextClip.Frames.Length) throw new ArgumentOutOfRangeException(nameof(end));
        if (tailFrames > end - start + 1)
            throw new ArgumentOutOfRangeException(nameof(tailFrames), "Tail must stay within the played range.");

        long loopTicks = 0;
        for (var i = start; i <= end; i++) loopTicks = checked(loopTicks + nextClip.Durations[i].Ticks);
        var repeatCount = minimumDuration is { } target
            ? checked((int)(target.Ticks / loopTicks + (target.Ticks % loopTicks == 0 ? 0 : 1)))
            : loops;

        _timer.Stop();
        _frameClock.Reset();
        _finishAtLoopBoundary = null;
        PlaybackGeneration++;
        _clip = nextClip;
        ResourcePath = resourcePath;
        _cursor = new GifPlaybackCursor(_clip.Frames.Length);
        _cursor.Reset(loop, repeatCount, start, end, tailFrames);
        _remaining = _clip.Durations[start];
        _image.Source = _clip.Frames[start];
        ClipChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Validate optional Ambient resources once without switching the current image.
    // The same cache is reused by TrySwitchTo; unsupported/corrupt optional GIFs stay out.
    public bool TryCacheClip(string resourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clips.ContainsKey(resourcePath)) return true;
        try
        {
            _clips.Add(resourcePath, LoadClip(resourcePath));
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or NotSupportedException or
            InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static GifClip LoadClip(string resourcePath)
    {
        if (CharacterProfileRuntime.Current?.TryAnimation(resourcePath, out _) != true)
            throw new FileNotFoundException("GIF has no validated Character Profile calibration (" + CharacterProfileRuntime.Result.Code + ").", resourcePath);
        var assemblyName = typeof(GifFramePlayer).Assembly.GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assemblyName};component/{resourcePath}");
        StreamResourceInfo? resource;
        try
        {
            resource = Application.GetResourceStream(uri);
        }
        catch (IOException exception)
        {
            throw new FileNotFoundException($"找不到桌宠 GIF 资源：{resourcePath}", resourcePath, exception);
        }

        if (resource is null)
        {
            throw new FileNotFoundException($"找不到桌宠 GIF 资源：{resourcePath}", resourcePath);
        }

        using var stream = resource.Stream;
        using var gifData = new MemoryStream();
        stream.CopyTo(gifData);
        if (gifData.Length < 10)
        {
            throw new InvalidDataException($"GIF 文件头不完整：{resourcePath}");
        }

        var header = gifData.GetBuffer();
        var pixelWidth = BitConverter.ToUInt16(header, 6);
        var pixelHeight = BitConverter.ToUInt16(header, 8);
        if (CharacterProfileRuntime.Current?.MatchesCanvas(resourcePath, pixelWidth, pixelHeight) != true)
            throw new InvalidDataException("GIF canvas does not match its Character Profile calibration.");
        gifData.Position = 0;
        var decoder = new GifBitmapDecoder(
            gifData,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException($"GIF 没有可播放的帧：{resourcePath}");
        }

        var frames = new BitmapSource[decoder.Frames.Count];
        var durations = new TimeSpan[decoder.Frames.Count];
        var transparentBase = true;

        for (var i = 0; i < decoder.Frames.Count; i++)
        {
            var frame = decoder.Frames[i];
            frame.Freeze();
            frames[i] = ExpandFrame(frame, resourcePath, pixelWidth, pixelHeight, transparentBase);
            durations[i] = ReadDuration(frame);
            var metadata = frame.Metadata as BitmapMetadata;
            var disposal = Convert.ToInt32(metadata?.GetQuery("/grctlext/Disposal") ?? 0);
            var fullCanvas = frame.PixelWidth == pixelWidth && frame.PixelHeight == pixelHeight &&
                Convert.ToInt32(metadata?.GetQuery("/imgdesc/Left") ?? 0) == 0 &&
                Convert.ToInt32(metadata?.GetQuery("/imgdesc/Top") ?? 0) == 0;
            // Clearing an independent frame leaves an empty canvas; restore-to-previous
            // preserves the prior base. Keep/unspecified disposal may leave pixels behind.
            transparentBase = disposal switch
            {
                2 => transparentBase || fullCanvas,
                3 => transparentBase,
                _ => false
            };
        }

        return new GifClip(pixelWidth, pixelHeight, frames, durations);
    }

    public void Pause()
    {
        if (_disposed || !_timer.IsEnabled)
        {
            return;
        }

        _timer.Stop();
        _remaining = _remaining - _frameClock.Elapsed;
        if (_remaining < TimeSpan.FromMilliseconds(1))
        {
            _remaining = TimeSpan.FromMilliseconds(1);
        }
        _frameClock.Reset();
    }

    public void Resume()
    {
#if PMV2_PILOT
        if (PilotPresentationHeld) return;
#endif
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer.IsEnabled || _cursor.IsComplete)
        {
            return;
        }

        _timer.Interval = _remaining;
        _frameClock.Restart();
        _timer.Start();
    }

    // Drag's admitted entry alone may lengthen the first presentation. No timer
    // or callback is added: OnTick uses the decoded durations again, including
    // frame zero on the next loop. Switching/stopping discards this remaining time.
    internal void SetDragEntryFirstFrameDuration(TimeSpan minimum)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (minimum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (_timer.IsEnabled || _cursor.IsComplete || _cursor.FrameIndex != _cursor.RangeStart)
            throw new InvalidOperationException("Drag entry duration requires its paused initial frame.");
        _remaining = _clip.Durations[_cursor.RangeStart] > minimum ? _clip.Durations[_cursor.RangeStart] : minimum;
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _frameClock.Reset();
        _cursor.Rewind();
        PlaybackGeneration++;
        _remaining = _clip.Durations[_cursor.RangeStart];
        _image.Source = _clip.Frames[_cursor.RangeStart];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _frameClock.Stop();
        _image.Source = null;
        _clips.Clear();
        _disposed = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        _frameClock.Reset();
        if (_cursor.FrameIndex == _cursor.RangeEnd && _finishAtLoopBoundary?.Invoke() == true)
            _cursor.FinishAtLoopEnd();
        if (_cursor.Advance())
        {
            Completed?.Invoke(this, new GifPlaybackCompletedEventArgs(ResourcePath, PlaybackGeneration));
            return;
        }

        _image.Source = _clip.Frames[_cursor.FrameIndex];
#if PMV2_PILOT
        PilotFrameAdvanced?.Invoke(this, EventArgs.Empty);
#endif
        _remaining = _clip.Durations[_cursor.FrameIndex];
        Resume();
    }

    private static BitmapSource ExpandFrame(BitmapFrame frame, string resourcePath, int pixelWidth, int pixelHeight, bool transparentBase)
    {
        var metadata = frame.Metadata as BitmapMetadata;
        var left = Convert.ToInt32(metadata?.GetQuery("/imgdesc/Left") ?? 0);
        var top = Convert.ToInt32(metadata?.GetQuery("/imgdesc/Top") ?? 0);
        if (left == 0 && top == 0 && frame.PixelWidth == pixelWidth && frame.PixelHeight == pixelHeight)
        {
            return frame;
        }

        if (!transparentBase || left < 0 || top < 0 ||
            left + frame.PixelWidth > pixelWidth || top + frame.PixelHeight > pixelHeight)
        {
            throw new NotSupportedException($"GIF 局部帧无法安全合成：{resourcePath}");
        }

        // Only expand independent partial frames onto a known transparent canvas.
        // This covers dragged.gif (restore-to-previous) and landing.gif (clear-to-background).
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
        }

        var canvas = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        canvas.Render(visual);
        canvas.Freeze();
        return canvas;
    }

    private static TimeSpan ReadDuration(BitmapFrame frame)
    {
        if (frame.Metadata is BitmapMetadata metadata)
        {
            try
            {
                var delay = metadata.GetQuery("/grctlext/Delay");
                if (delay != null)
                {
                    var centiseconds = Convert.ToInt32(delay);
                    if (centiseconds > 0)
                    {
                        return TimeSpan.FromMilliseconds(Math.Max(20, centiseconds * 10));
                    }
                }
            }
            catch (NotSupportedException)
            {
                // A missing GIF delay uses the fallback below.
            }
        }

        return TimeSpan.FromMilliseconds(100);
    }
}
