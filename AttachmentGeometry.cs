// Portions derived from YoneDesktopPet, maintained upstream by DreamingBetter.
// https://github.com/DreamingBetter/YoneDesktopPet
// Audit reference: 3c410d002e4c6b8ed2cbcb7689dc2b531a48653c; original import not uniquely established.
// Upstream MIT and original notices: LICENSE and THIRD_PARTY_NOTICES.md.
// Detection/scoring and part of alignment extract and modify upstream attachment rules.
// Later transforms/contact mapping also depend on character metadata; see docs/ASSETS.md.

using System;
using System.Windows;
using System.Windows.Media;
using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet;

internal enum AttachmentSide { Top, Left, Right }
internal readonly record struct AttachmentCandidate(AttachmentSide Side, double Score, double OffsetX, double OffsetY);
internal readonly record struct AttachmentContacts(Point Top, Point Left, Point Right);

// Small geometry helpers only: no HWND enumeration, timers or attachment lifecycle.
internal static class AttachmentGeometry
{
    // Detection thresholds only. The optional maximum tightens both historical thresholds
    // and can never widen them; omitting it (null) reproduces every pre-existing release
    // decision exactly. Values are WPF DIP in the pet-local frame.
    public static AttachmentCandidate? Detect(Rect pet, Rect target, Rect workArea, double height, double topVisualHeight,
        double? maximumSnapDistanceDip = null)
    {
        AttachmentCandidate? best = null;
        var centerX = pet.Left + pet.Width / 2;
        var centerY = pet.Top + pet.Height / 2;
        var topDistance = Math.Abs(pet.Bottom - target.Top);
        // Min() can only tighten. A NaN or nonpositive cap rejects every candidate
        // (fail closed); the settings validator never produces such a value.
        var topLimit = AttachmentContactProfile.TopSnapDistance(height);
        if (maximumSnapDistanceDip is double topMaximum) topLimit = Math.Min(topLimit, topMaximum);
        if (centerX >= target.Left - pet.Width * AttachmentContactProfile.TopHorizontalAllowance &&
            centerX <= target.Right + pet.Width * AttachmentContactProfile.TopHorizontalAllowance &&
            // A short horizontal release pose must leave room for the standing Attach visual.
            target.Top + height * AttachmentContactProfile.TopContactOffsetFactor - topVisualHeight >= workArea.Top &&
            topDistance <= topLimit)
            best = new(AttachmentSide.Top, topDistance, centerX - target.Left, 0);

        if (centerY < target.Top - pet.Height * AttachmentContactProfile.SideVerticalAllowance ||
            centerY > target.Bottom + pet.Height * AttachmentContactProfile.SideVerticalAllowance)
            return best;

        void Consider(AttachmentSide side, double distance, bool hasRoom)
        {
            if (!hasRoom || !SideAttachmentDetectionProfile.IsWithinSnapDistance(distance, height, maximumSnapDistanceDip)) return;
            var score = distance + AttachmentContactProfile.SideScorePenalty;
            if (best == null || score < best.Value.Score)
                best = new(side, score, 0, centerY - target.Top);
        }
        Consider(AttachmentSide.Left, Math.Abs(pet.Right - target.Left),
            target.Left - pet.Width * AttachmentContactProfile.RoomBesideFactor >= workArea.Left);
        Consider(AttachmentSide.Right, Math.Abs(pet.Left - target.Right),
            target.Right + pet.Width * AttachmentContactProfile.RoomBesideFactor <= workArea.Right);
        return best;
    }

    public static double CandidateScore(AttachmentSide side, double geometricScore, int zOrder) =>
        side == AttachmentSide.Top
            ? geometricScore + zOrder * AttachmentContactProfile.ZOrderScoreFactor
            : geometricScore;

    // WPF applies RenderTransform around RenderTransformOrigin, then Canvas placement.
    public static Point TransformPoint(Point point, Size imageSize, Point imageOrigin,
        Matrix transform, Point renderOrigin)
    {
        var pivot = new Point(imageSize.Width * renderOrigin.X, imageSize.Height * renderOrigin.Y);
        var transformed = transform.Transform(point - (Vector)pivot);
        return transformed + (Vector)pivot + (Vector)imageOrigin;
    }

    // Explicit source contacts only. Missing geometry has no canonical/PNG fallback.
    public static bool TryContact(CharacterProfile? profile, AttachmentSide side, double height,
        Size imageSize, Point imageOrigin, Matrix transform, Point renderOrigin, string? path, out Point contact)
    {
        contact = default;
        if (profile == null || !profile.HasContact(path, side) || !profile.TryAnimation(path, out var animation) ||
            !double.IsFinite(height) || height <= 0 || !double.IsFinite(imageSize.Width) || !double.IsFinite(imageSize.Height) ||
            imageSize.Width <= 0 || imageSize.Height <= 0) return false;
        var unit = height / profile.CanonicalCanvas.Height;
        Point Map(Point normalized) => TransformPoint(new(normalized.X * animation.Canvas.Width * unit,
            normalized.Y * animation.Canvas.Height * unit), imageSize, imageOrigin, transform, renderOrigin);
        bool Finite(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
        if (side == AttachmentSide.Top)
        {
            var top = Map(animation.Contacts.Top!.Value);
            if (!Finite(top)) return false;
            contact = top;
            return true;
        }
        var left = Map(animation.Contacts.Left!.Value);
        var right = Map(animation.Contacts.Right!.Value);
        if (!Finite(left) || !Finite(right)) return false;
        contact = side == AttachmentSide.Left ? (left.X >= right.X ? left : right) : (left.X >= right.X ? right : left);
        return true;
    }

    public static bool TrySideOverlayContact(CharacterProfile? profile, string? path, AttachmentSide side,
        double height, Rect transformedUnion, out Point contact)
    {
        contact = default;
        if (profile == null || side == AttachmentSide.Top || !profile.TryAnimation(path, out var animation) ||
            animation.SideOverlay is not {} correction || transformedUnion.IsEmpty ||
            !double.IsFinite(height) || height <= 0) return false;
        var unit = height / profile.CanonicalCanvas.Height;
        var x = side == AttachmentSide.Left ? transformedUnion.Right - correction.WallInset * unit :
            transformedUnion.Left + correction.WallInset * unit;
        var y = transformedUnion.Bottom - correction.BottomToContact * unit;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !transformedUnion.Contains(new Point(x,y))) return false;
        contact = new(x,y);
        return true;
    }

    public static Point Align(AttachmentSide side, Rect target, AttachmentContacts contacts,
        double offsetX, double offsetY, double height, double logicalWidth)
    {
        if (side == AttachmentSide.Top)
        {
            var inset = Math.Min(target.Width / 2, logicalWidth * AttachmentContactProfile.TopHorizontalInsetFactor);
            var x = Math.Clamp(target.Left + offsetX, target.Left + inset, target.Right - inset);
            return new Point(x - contacts.Top.X,
                target.Top + height * AttachmentContactProfile.TopContactOffsetFactor - contacts.Top.Y);
        }
        var margin = Math.Min(target.Height / 2, height * AttachmentContactProfile.SideVerticalInsetFactor);
        var y = Math.Clamp(target.Top + offsetY, target.Top + margin, target.Bottom - margin);
        var contact = side == AttachmentSide.Left ? contacts.Left : contacts.Right;
        return new Point((side == AttachmentSide.Left ? target.Left : target.Right) - contact.X, y - contact.Y);
    }
}
