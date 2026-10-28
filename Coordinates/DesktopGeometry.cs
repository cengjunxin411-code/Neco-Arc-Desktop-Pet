using System;
using System.Collections.Generic;

namespace NecoArcDesktopPet.Coordinates;

internal static class DesktopGeometry
{
    public static bool TryGetAnchorRange(PhysicalRect workArea, PhysicalFootprint footprint, out AnchorRange range)
    {
        range = default;
        if (!workArea.IsValid || !footprint.IsValid || footprint.Width > workArea.Width ||
            footprint.HeightAboveGround > workArea.Height) return false;
        var candidate = new AnchorRange(Inset(workArea.Left, footprint.LeftExtent, roundUp: true),
            Inset(workArea.Right, -footprint.RightExtent, roundUp: false),
            Inset(workArea.Top, footprint.HeightAboveGround, roundUp: true), workArea.Bottom);
        if (!candidate.IsValid) return false;
        range = candidate;
        return true;
    }

    public static bool TryProject(PhysicalPoint point, AnchorRange range, out PhysicalPoint projected)
    {
        projected = default;
        if (!point.IsFinite || !range.IsValid) return false;
        projected = new PhysicalPoint(Math.Clamp(point.X, range.MinimumX, range.MaximumX),
            Math.Clamp(point.Y, range.MinimumY, range.MaximumY));
        return true;
    }

    // Select by original release point first, then project on that monitor only.
    // Does not move a valid airborne point to the floor, or migrate an oversized body.
    public static PlacementResult Resolve(PhysicalPoint groundAnchor, IReadOnlyList<MonitorSnapshot>? monitors,
        PhysicalFootprint footprint, string? preferredMonitorId = null)
    {
        if (!groundAnchor.IsFinite) return Failed(PlacementFailure.InvalidPoint);
        if (!footprint.IsValid) return Failed(PlacementFailure.InvalidFootprint);
        if (monitors == null) return Failed(PlacementFailure.NoValidMonitors);

        var valid = new List<MonitorSnapshot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var monitor in monitors)
        {
            if (!monitor.IsValid) continue;
            // Conflicting identities must not make selection depend on input order.
            if (!ids.Add(monitor.Id)) return Failed(PlacementFailure.DuplicateMonitorId);
            valid.Add(monitor);
        }
        if (valid.Count == 0) return Failed(PlacementFailure.NoValidMonitors);

        MonitorSnapshot? selected = null;
        var selectedContains = false;
        DistanceKey selectedDistance = default;
        foreach (var monitor in valid)
        {
            var contains = monitor.Bounds.ContainsClosed(groundAnchor);
            var distance = contains ? default : DistanceKey.ToWorkArea(groundAnchor, monitor.WorkArea);
            var choose = selected == null || (contains && !selectedContains);
            if (selected is { } previous && contains == selectedContains)
            {
                var comparison = contains ? 0 : distance.CompareTo(selectedDistance);
                choose = comparison < 0 || (comparison == 0 && Prefer(monitor.Id, previous.Id, preferredMonitorId));
            }
            if (!choose) continue;
            selected = monitor;
            selectedContains = contains;
            selectedDistance = distance;
        }

        var owner = selected!.Value;
        if (footprint.Width > owner.WorkArea.Width || footprint.HeightAboveGround > owner.WorkArea.Height)
            return Failed(PlacementFailure.FootprintDoesNotFit, owner.Id);
        if (!TryGetAnchorRange(owner.WorkArea, footprint, out var range) || !TryProject(groundAnchor, range, out var position))
            return Failed(PlacementFailure.NumericalOverflow, owner.Id);
        return new PlacementResult(PlacementFailure.None, owner.Id, position);
    }

    private static PlacementResult Failed(PlacementFailure failure, string? monitorId = null) => new(failure, monitorId, default);

    private static bool Prefer(string candidate, string current, string? preferred) =>
        candidate == preferred ? true : current != preferred && StringComparer.Ordinal.Compare(candidate, current) < 0;

    private static double Inset(double edge, double amount, bool roundUp)
    {
        var sum = edge + amount;
        if (!double.IsFinite(sum)) return sum;
        // TwoSum recovers the rounding residual. Select the representable endpoint
        // inside the exact body-fit interval, including at very large origins.
        var roundedAmount = sum - edge;
        var roundedEdge = sum - roundedAmount;
        var residual = (edge - roundedEdge) + (amount - roundedAmount);
        return roundUp && residual > 0 ? Math.BitIncrement(sum)
            : !roundUp && residual < 0 ? Math.BitDecrement(sum) : sum;
    }

    // Compare squared Euclidean distance as an exponent/significand pair, rather
    // than forming an overflowing square or an underflowing subnormal square.
    private readonly record struct DistanceKey(int Exponent, double Significand) : IComparable<DistanceKey>
    {
        public int CompareTo(DistanceKey other)
        {
            var comparison = Exponent.CompareTo(other.Exponent);
            return comparison != 0 ? comparison : Significand.CompareTo(other.Significand);
        }

        public static DistanceKey ToWorkArea(PhysicalPoint point, PhysicalRect area)
        {
            static double Gap(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;
            var dx = Gap(point.X, area.Left, area.Right);
            var dy = Gap(point.Y, area.Top, area.Bottom);
            var adjustment = 0;
            if (!CoordinateNumbers.Finite(dx, dy))
            {
                // Opposite finite extremes can overflow subtraction. Halve all
                // coordinates only for this private distance calculation; no world
                // point or projection is transformed. Preserve the common exponent.
                dx = Gap(point.X / 2, area.Left / 2, area.Right / 2);
                dy = Gap(point.Y / 2, area.Top / 2, area.Bottom / 2);
                adjustment = 1;
            }
            var largest = Math.Max(dx, dy);
            if (largest == 0) return new DistanceKey(int.MinValue, 0);
            var exponent = Math.ILogB(largest);
            var x = Math.ScaleB(dx, -exponent);
            var y = Math.ScaleB(dy, -exponent);
            var squared = x * x + y * y;
            var extraExponent = Math.ILogB(squared);
            return new DistanceKey(2 * (exponent + adjustment) + extraExponent, Math.ScaleB(squared, -extraExponent));
        }
    }
}
