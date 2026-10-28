using System;

namespace NecoArcDesktopPet.Animation;

internal sealed class GifPlaybackCursor
{
    private readonly int _frameCount;
    private bool _loop = true;
    private int _repeatCount = 1;
    private int _remainingLoops = 1;
    private int _rangeStart;
    private int _rangeEnd;
    private int _tailFrames;

    public int FrameIndex { get; private set; }
    public bool IsComplete { get; private set; }
    // True once the single finite pass ended and the tail frames repeat forever.
    // Tail playback never reports completion and never leaves the original range.
    public bool IsTailLooping { get; private set; }
    public int RangeStart => _rangeStart;
    public int RangeEnd => _rangeEnd;
    public int TailFrames => _tailFrames;

    public GifPlaybackCursor(int frameCount)
    {
        if (frameCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        _frameCount = frameCount;
        _rangeEnd = frameCount - 1;
    }

    public void Reset(bool loop, int repeatCount = 1)
        => Reset(loop, repeatCount, 0, _frameCount - 1);

    public void Reset(bool loop, int repeatCount, int rangeStart, int rangeEnd)
        => Reset(loop, repeatCount, rangeStart, rangeEnd, tailFrames: 0);

    // tailFrames: 0 disables the optional tail loop, so every existing caller keeps
    // its exact behavior. A positive value keeps [rangeStart, rangeEnd] untouched and,
    // after the single finite pass, repeats the last tailFrames frames forever instead
    // of completing. The tail therefore replaces completion: it requires loop:false and
    // repeatCount 1, must fit inside the played range, and never produces an index
    // outside [rangeStart, rangeEnd]. Frame durations stay owned by the player, so a
    // one-frame tail re-presents that frame on its decoded duration, never at 0 ms.
    public void Reset(bool loop, int repeatCount, int rangeStart, int rangeEnd, int tailFrames)
    {
        if (repeatCount < 1) throw new ArgumentOutOfRangeException(nameof(repeatCount));
        if (rangeStart < 0 || rangeEnd < rangeStart || rangeEnd >= _frameCount)
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), "Range must be within the decoded frames.");
        if (tailFrames < 0 || tailFrames > rangeEnd - rangeStart + 1)
            throw new ArgumentOutOfRangeException(nameof(tailFrames), "Tail must stay within the played range.");
        if (tailFrames > 0 && (loop || repeatCount != 1))
            throw new ArgumentException("Tail playback replaces completion and requires loop:false with a single pass.",
                nameof(tailFrames));
        _loop = loop;
        _repeatCount = repeatCount;
        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
        _tailFrames = tailFrames;
        Rewind();
    }

    public void Rewind()
    {
        FrameIndex = _rangeStart;
        IsComplete = false;
        IsTailLooping = false;
        _remainingLoops = _repeatCount;
    }

    public void FinishAtLoopEnd()
    {
        _loop = false;
        _remainingLoops = 1;
    }

    // Returns true after all finite loops have displayed their final frame for its full duration.
    // A configured tail loop returns false forever: its last frame is re-presented, never completed.
    public bool Advance()
    {
        if (IsComplete)
        {
            return false;
        }

        if (FrameIndex < _rangeEnd)
        {
            FrameIndex++;
            return false;
        }

        if (_loop || _remainingLoops > 1)
        {
            if (!_loop) _remainingLoops--;
            FrameIndex = _rangeStart;
            return false;
        }

        if (_tailFrames > 0)
        {
            IsTailLooping = true;
            FrameIndex = _rangeEnd - _tailFrames + 1;
            return false;
        }

        IsComplete = true;
        return true;
    }
}
