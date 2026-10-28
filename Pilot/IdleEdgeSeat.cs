using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// Release-time Idle edge seating, pure geometry.
//
// The dragged.gif pose is wider than the Idle pose, so an Idle admitted from the
// Drag anchor centre can never reach an outer WorkArea edge on its own. This
// resolves one legal, minimal, horizontal-only Idle target against the SAME
// WorkArea that contained the release RECT. A shared monitor seam is never an
// outer wall: the release must be contained by one WorkArea, the outer strip
// beside the target band must be uncovered by every other WorkArea, and the
// translation from the release RECT must be fully covered.
internal readonly record struct IdleEdgeSeatPlacement(PhysicalRect Target, string Side, double CorrectionPx);

internal static class IdleEdgeSeat
{
    // Physical px. Same tolerance the recovery geometry already calls "near".
    internal const double ContactTolerancePx = 2;

    internal static IdleEdgeSeatPlacement? Resolve(PhysicalRect release, PhysicalRect natural, IReadOnlyList<PhysicalRect> work)
    {
        if (!release.IsValid || !natural.IsValid || work.Count == 0 || work.Count > 16) return null;
        foreach (var area in work) if (!area.IsValid) return null;
        if (!CrossScreenGeometry.Covered(release, work)) return null;
        // A RECT straddling two WorkAreas has no single owner and is never seated.
        var index = -1;
        for (var i = 0; i < work.Count; i++)
            if (Contains(work[i], release)) { index = i; break; }
        if (index < 0) return null;
        var owner = work[index];
        var right = Math.Abs(release.Right - owner.Right) <= ContactTolerancePx;
        var left = Math.Abs(release.Left - owner.Left) <= ContactTolerancePx;
        IdleEdgeSeatPlacement? seat = null;
        if (right) seat = Build(release, owner, natural, work, index, true);
        if (left)
        {
            var candidate = Build(release, owner, natural, work, index, false);
            if (candidate is { } other && (seat is null || Math.Abs(other.CorrectionPx) < Math.Abs(seat.Value.CorrectionPx))) seat = other;
        }
        return seat;
    }

    private static IdleEdgeSeatPlacement? Build(PhysicalRect release, PhysicalRect owner, PhysicalRect natural, IReadOnlyList<PhysicalRect> work, int index, bool right)
    {
        var target = right
            ? natural with { Left = owner.Right - natural.Width, Right = owner.Right }
            : natural with { Left = owner.Left, Right = owner.Left + natural.Width };
        if (!target.IsValid || !Contains(owner, target)) return null;           // Idle must fit in the same legal area
        if (!Outer(owner, target, work, index, right)) return null;            // a seam is not an outer wall
        if (!CrossScreenGeometry.TranslationSafe(release, target, work)) return null;
        var correction = target.Left - natural.Left;
        // The Drag pose is wider than the Idle pose, so a manual wall placement can
        // only ever need half of the pose-width difference (plus rounding). A larger
        // requested correction is not a provable placement and is refused instead of
        // teleporting the pet.
        var allowed = Math.Max(0, (release.Width - natural.Width) / 2) + ContactTolerancePx;
        if (!double.IsFinite(correction) || Math.Abs(correction) > allowed || Math.Abs(correction) > release.Width) return null;
        return new(target, right ? "right" : "left", correction);
    }

    private static bool Outer(PhysicalRect owner, PhysicalRect band, IReadOnlyList<PhysicalRect> work, int index, bool right)
    {
        // One-pixel band just outside the wall, at the vertical span the Idle will
        // occupy. Any other WorkArea covering it proves a shared seam or a tiled
        // neighbour, so this edge is not an outer display edge.
        var strip = right
            ? new PhysicalRect(owner.Right, band.Top, owner.Right + 1, band.Bottom)
            : new PhysicalRect(owner.Left - 1, band.Top, owner.Left, band.Bottom);
        for (var i = 0; i < work.Count; i++)
        {
            if (i == index) continue;
            // Any neighbouring WorkArea touching this band, even partially, makes the
            // edge a monitor seam rather than an outer display edge.
            if (Overlaps(work[i], strip)) return false;
        }
        return true;
    }

    private static bool Overlaps(PhysicalRect a, PhysicalRect b) => a.IsValid && b.IsValid &&
        Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left) && Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Top, b.Top);

    private static bool Contains(PhysicalRect area, PhysicalRect rect) => area.IsValid && rect.IsValid &&
        rect.Left >= area.Left && rect.Right <= area.Right && rect.Top >= area.Top && rect.Bottom <= area.Bottom;
}
