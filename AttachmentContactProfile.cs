// Portions derived from YoneDesktopPet, maintained upstream by DreamingBetter.
// https://github.com/DreamingBetter/YoneDesktopPet
// Audit reference: 3c410d002e4c6b8ed2cbcb7689dc2b531a48653c; original import not uniquely established.
// Upstream MIT and original notices: LICENSE and THIRD_PARTY_NOTICES.md.
// Upstream-derived detection/scoring and later strategy tuning remain here.
// Explicit character contacts now come only from a validated local Character Profile.

using System;
using System.Windows;
using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet;

internal static class AttachmentContactProfile
{
    public static double CanonicalHeight => CharacterProfileRuntime.Current?.CanonicalCanvas.Height ?? 1;
    public const double TopContactOffsetFactor = 0.005;
    public const double TopSnapMinimum = 52;
    public const double TopSnapFactor = 0.30;
    public const double TopSnapMaximum = 90;
    public const double SideSnapMinimum = 72;
    public const double SideSnapFactor = 0.40;
    public const double SideSnapMaximum = 120;
    public const double TopHorizontalAllowance = 0.24;
    public const double SideVerticalAllowance = 0.25;
    public const double RoomBesideFactor = 0.58;
    public const double TopHorizontalInsetFactor = 0.32;
    public const double SideVerticalInsetFactor = 0.16;
    public const double SideScorePenalty = 12;
    public const double ZOrderScoreFactor = 0.08;

    public static double TopSnapDistance(double height) =>
        Math.Clamp(height * TopSnapFactor, TopSnapMinimum, TopSnapMaximum);
    public static double SideSnapDistance(double height) =>
        Math.Clamp(height * SideSnapFactor, SideSnapMinimum, SideSnapMaximum);

    public static bool HasContact(string? path, AttachmentSide side) =>
        CharacterProfileRuntime.Current?.HasContact(path, side) == true;

    public static bool TryDetectionSourceBounds(string? path, Size sourceSize, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (CharacterProfileRuntime.Current is not {} profile || !profile.MatchesCanvas(path, sourceSize.Width, sourceSize.Height) ||
            !profile.TryAnimation(path, out var animation) || animation.DetectionBounds is not {} declared) return false;
        bounds = declared;
        return true;
    }
}
