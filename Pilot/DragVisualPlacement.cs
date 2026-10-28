using System;
using System.Collections.Generic;
using System.Linq;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// Entry-only full-footprint placement. No viewport, DPI identity or native write.
internal static class DragVisualPlacement
{
    internal static PhysicalRect? Resolve(PhysicalRect old, PhysicalRect natural, IReadOnlyList<PhysicalRect> work)
    {
        if (!old.IsValid || !natural.IsValid || work.Count == 0 || work.Count > 16 ||
            work.Any(w => !w.IsValid) || !CrossScreenGeometry.Covered(old, work)) return null;
        // A covered natural pose keeps its exact old policy, including corridor refusal.
        if (CrossScreenGeometry.Covered(natural, work))
            return CrossScreenGeometry.TranslationSafe(old, natural, work) ? natural : null;

        var reachable = CrossScreenGeometry.Reachable(old, work);
        // Legal-origin cell edges, matching Fit's finite search. Filter the sweep
        // BEFORE choosing the nearest candidate; never teleport to another island.
        var xs = reachable.SelectMany(w => new[] { Math.Ceiling(w.Left), Math.Floor(w.Right - natural.Width) })
            .Append(natural.Left).Distinct();
        var ys = reachable.SelectMany(w => new[] { Math.Ceiling(w.Top), Math.Floor(w.Bottom - natural.Height) })
            .Append(natural.Top).Distinct().ToArray();
        PhysicalRect? best = null;
        var distance = double.PositiveInfinity;
        foreach (var x in xs) foreach (var y in ys)
        {
            var dx = x - natural.Left; var dy = y - natural.Top;
            // Entry may correct local edge overflow, never a large relocation.
            // More complex non-convex cases retain R2A's fallback.
            if (Math.Abs(dx) > natural.Width / 2 || Math.Abs(dy) > natural.Height / 2) continue;
            if (!PhysicalRect.TryFromOriginAndSize(new(x, y), natural.Width, natural.Height, out var candidate) ||
                !CrossScreenGeometry.TranslationSafe(old, candidate, reachable)) continue;
            var squared = dx * dx + dy * dy;
            if (double.IsFinite(squared) && squared < distance) { best = candidate; distance = squared; }
        }
        return best;
    }
}
