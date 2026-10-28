using System;
using System.Collections.Frozen;
using System.Windows;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet.CharacterProfiles;

internal readonly record struct ProfileContacts(Point? Top, Point? Left, Point? Right);
// Distances in canonical source units AFTER the overlay's transformed alpha union.
// They are multiplied by height/canonicalHeight, not by the clip visual scale.
internal readonly record struct SideOverlayCalibration(double WallInset, double BottomToContact);
internal sealed record AnimationCalibration(Size Canvas, AnimationVisualProfile Visual,
    ProfileContacts Contacts, Rect? DetectionBounds, SideOverlayCalibration? SideOverlay,
    Point? HeadAnchor, Point? BodyAnchor);

internal sealed record CharacterProfile(string Id, Size CanonicalCanvas, double LogicalAspect,
    FrozenDictionary<string, AnimationCalibration> Animations)
{
    public bool TryAnimation(string? key, out AnimationCalibration animation)
    {
        animation = null!;
        return key != null && Animations.TryGetValue(key.Replace('\\', '/'), out animation!);
    }

    public bool MatchesCanvas(string? key, double width, double height) =>
        TryAnimation(key, out var animation) && animation.Canvas.Width == width && animation.Canvas.Height == height;

    // Side contact ordering is resolved after mirroring; both sides must be known.
    public bool HasContact(string? key, AttachmentSide side) => TryAnimation(key, out var animation) &&
        (side == AttachmentSide.Top ? animation.Contacts.Top != null :
            animation.Contacts.Left != null && animation.Contacts.Right != null);

    public bool CanOverlay(string? key, AttachmentSide side) =>
        side == AttachmentSide.Top ? HasContact(key, side) :
            TryAnimation(key, out var animation) && animation.SideOverlay != null;
}

internal sealed record CharacterProfileLoadResult(CharacterProfile? Profile, string Code, string Message)
{
    public bool Success => Profile != null;
}
