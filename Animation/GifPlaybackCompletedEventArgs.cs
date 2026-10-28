using System;

namespace NecoArcDesktopPet.Animation;

// Capture the completed playback, so delayed notifications cannot finish a newer clip.
internal sealed class GifPlaybackCompletedEventArgs(string resourcePath, long generation) : EventArgs
{
    public string ResourcePath { get; } = resourcePath;
    public long Generation { get; } = generation;
}
