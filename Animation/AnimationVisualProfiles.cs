using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet.Animation;

// Only PNG/neutral WPF presentation can use Neutral. GIF admission requires an
// explicit calibration and matching source canvas before any frame is cached.
internal static class AnimationVisualProfiles
{
    private static readonly AnimationVisualProfile Neutral = new(1, 0, 0);
    public static double CanonicalCanvasHeight => CharacterProfileRuntime.Current?.CanonicalCanvas.Height ?? 1;
    public static AnimationVisualProfile For(string resourcePath) =>
        CharacterProfileRuntime.Current?.TryAnimation(resourcePath, out var animation) == true ? animation.Visual : Neutral;
}
