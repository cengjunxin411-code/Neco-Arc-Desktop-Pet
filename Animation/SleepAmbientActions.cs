using System;

namespace NecoArcDesktopPet.Animation;

internal static class SleepAmbientActions
{
    public const string StretchPath = "NecoPetMaterials/sleep/stretch.gif";
    public static bool Contains(string path) => string.Equals(path, StretchPath, StringComparison.OrdinalIgnoreCase);
    public static string[] Available(GifFramePlayer? player) => player?.TryCacheClip(StretchPath) == true
        ? new[] { StretchPath } : Array.Empty<string>();
}
