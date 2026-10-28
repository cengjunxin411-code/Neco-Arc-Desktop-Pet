using System;
using System.Collections.Generic;
using System.Linq;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal sealed class PhysicsBounds
{
    // Bounded immutable topology; overlapping interiors are deliberately unsupported.
    // Input order is not used to choose a support (IDs/geometry break ties).
    private readonly (string Id, PhysicalRect Area)[] work;
    private PhysicsBounds((string, PhysicalRect)[] work) => this.work = work;
    internal bool IsValid => work.Length > 0;
    private static PhysicsBounds Invalid() => new([]);

    internal static bool SafeRect(PhysicalRect rect) => rect.IsValid &&
        Math.Abs(rect.Left) <= 1e9 && Math.Abs(rect.Right) <= 1e9 &&
        Math.Abs(rect.Top) <= 1e9 && Math.Abs(rect.Bottom) <= 1e9 && rect.Width >= 1e-6 && rect.Height >= 1e-6;

    internal static PhysicsBounds FromMonitors(IReadOnlyList<MonitorSnapshot>? monitors)
    {
        if (monitors is null || monitors.Count is < 1 or > 16) return Invalid();
        var entries = new (string, PhysicalRect)[monitors.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < monitors.Count; i++) {
            var m = monitors[i];
            if (!m.IsValid || !SafeRect(m.Bounds) || !SafeRect(m.WorkArea) || !ids.Add(m.Id)) return Invalid();
            entries[i] = (m.Id, m.WorkArea);
        }
        return Create(entries);
    }
    internal static PhysicsBounds FromWorkAreas(IReadOnlyList<PhysicalRect>? areas)
    {
        if (areas is null || areas.Count is < 1 or > 16) return Invalid();
        var entries = new (string, PhysicalRect)[areas.Count];
        for (int i = 0; i < areas.Count; i++) entries[i] = ($"area-{i}", areas[i]);
        return Create(entries);
    }
    private static PhysicsBounds Create((string Id, PhysicalRect Area)[] entries)
    {
        for (int i = 0; i < entries.Length; i++) {
            if (!SafeRect(entries[i].Area)) return Invalid();
            for (int j = 0; j < i; j++) {
                var a = entries[i].Area; var b = entries[j].Area;
                if (Math.Min(a.Right,b.Right) > Math.Max(a.Left,b.Left) &&
                    Math.Min(a.Bottom,b.Bottom) > Math.Max(a.Top,b.Top)) return Invalid();
            }
        }
        return new(entries);
    }
    internal bool Covered(PhysicalRect rect)
    {
        if (!IsValid || !SafeRect(rect)) return false;
        // Every open X slab must have complete merged Y coverage. Corner/center
        // checks cannot prove this (e.g. a rectangle enclosing an interior hole).
        var cuts = work.SelectMany(w => new[] {w.Area.Left,w.Area.Right})
            .Where(x => x > rect.Left && x < rect.Right).Append(rect.Left).Append(rect.Right).Distinct().OrderBy(x => x).ToArray();
        for (int i = 1; i < cuts.Length; i++) {
            var end = rect.Top;
            foreach (var w in work.Select(x => x.Area).Where(w => w.Left <= cuts[i-1] && w.Right >= cuts[i]).OrderBy(w => w.Top)) {
                if (w.Top > end) break;
                end = Math.Max(end,w.Bottom);
                if (end >= rect.Bottom) break;
            }
            if (end < rect.Bottom) return false;
        }
        return true;
    }
    private static PhysicalRect Translate(PhysicalRect r, double d, bool x) => x
        ? new(r.Left+d,r.Top,r.Right+d,r.Bottom) : new(r.Left,r.Top+d,r.Right,r.Bottom+d);
    private bool Corridor(PhysicalRect a, PhysicalRect b) => Covered(new(
        Math.Min(a.Left,b.Left),Math.Min(a.Top,b.Top),Math.Max(a.Right,b.Right),Math.Max(a.Bottom,b.Bottom)));

    // Axis-aligned sweeps have an exact rectangular union. Coverage of that whole
    // corridor proves the path, even if the final rectangle lies beyond a hole.
    // Failure occurs at an exposed work edge; enumerate <=16 exact contact edges,
    // rather than advancing by pixels or binary-searching an approximate contact.
    internal AxisSweep Sweep(PhysicalRect rect, double delta, bool horizontal)
    {
        if (!Covered(rect) || !double.IsFinite(delta)) return new(rect, true);
        if (delta == 0) return new(rect, false);
        var target = Translate(rect,delta,horizontal);
        if (SafeRect(target) && Corridor(rect,target)) return new(target,false);
        double startEdge = horizontal ? (delta > 0 ? rect.Right : rect.Left) : (delta > 0 ? rect.Bottom : rect.Top);
        double best = 0;
        foreach (var entry in work) {
            var w = entry.Area;
            double edge = horizontal ? (delta > 0 ? w.Right : w.Left) : (delta > 0 ? w.Bottom : w.Top);
            var d = edge - startEdge;
            if (delta > 0 ? d < 0 || d > delta : d > 0 || d < delta) continue;
            if (Math.Abs(d) > Math.Abs(best) && Corridor(rect,Translate(rect,d,horizontal))) best = d;
        }
        return new(Translate(rect,best,horizontal),true);
    }

    // The first *exposed* blocking floor takes precedence over overlap length.
    // Otherwise a wide overlap on a lower display would sink into the higher one.
    // Internal horizontal seams are not floors. Hysteresis applies only to equal
    // floor heights; losing all overlap immediately relinquishes old support.
    internal GroundSupport? Ground(PhysicalRect rect, string? previousId = null, double hysteresis = 8)
    {
        if (!Covered(rect) || !PhysicsTuning.In(hysteresis,0,10000)) return null;
        var candidates = new List<GroundSupport>();
        foreach (var (id,w) in work) {
            var overlap = Math.Min(rect.Right,w.Right)-Math.Max(rect.Left,w.Left);
            if (overlap <= 0 || w.Bottom < rect.Bottom) continue;
            var atFloor = new PhysicalRect(rect.Left,w.Bottom-rect.Height,rect.Right,w.Bottom);
            if (!Covered(atFloor) || Covered(atFloor with {Bottom=w.Bottom+.0001})) continue;
            candidates.Add(new(id,w.Bottom,overlap));
        }
        if (candidates.Count == 0) return null;
        var floor = candidates.Min(x => x.Floor);
        var same = candidates.Where(x => x.Floor == floor).OrderByDescending(x => x.Overlap).ThenBy(x => x.Id,StringComparer.Ordinal).ToArray();
        var best = same[0];
        foreach (var old in same) if (old.Id == previousId && best.Overlap-old.Overlap <= hysteresis) return old;
        return best;
    }

    // Size must already be an integer footprint chosen by an adapter. No fitting,
    // size/DPI adaptation or silent relocation is performed if rounding is unsafe.
    internal bool TryRoundedTarget(PhysicalRect rect, out PhysicalRect target)
    {
        target = default;
        if (!Covered(rect) || Math.Abs(rect.Width-Math.Round(rect.Width)) > 1e-7 ||
            Math.Abs(rect.Height-Math.Round(rect.Height)) > 1e-7) return false;
        var left = Math.Round(rect.Left); var top = Math.Round(rect.Top);
        var candidate = new PhysicalRect(left,top,left+Math.Round(rect.Width),top+Math.Round(rect.Height));
        if (!Covered(candidate) || !Corridor(rect,candidate)) return false;
        target = candidate;
        return true;
    }
}
