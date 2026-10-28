using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Coordinates;

internal static class Program
{
    private static int checks, failures;
    private static readonly PhysicalFootprint Body = new(20, 30, 80);
    private static MonitorSnapshot Screen(string id, PhysicalRect bounds, PhysicalRect? work = null) => new(id, bounds, work ?? bounds);
    private static readonly MonitorSnapshot MainScreen = Screen("main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));

    private static int Main()
    {
        var cases = new (string Name, Action Run)[]
        {
            ("closed ground and bottom taskbar", Ground), ("left/top/right taskbars", Taskbars),
            ("Bounds ownership precedes distance", BoundsFirst), ("negative X and different floors", Horizontal),
            ("negative Y and vertical shared edge", Vertical), ("gaps and off-screen projection", Gaps),
            ("Euclidean corner distance", Corners), ("deterministic shared edges", SharedEdges),
            ("invalid snapshots and duplicate IDs", Snapshots), ("logical footprint and exact fit", Ranges),
            ("oversized and invalid footprints", Footprints), ("invalid numbers and rectangles", InvalidNumbers),
            ("overflow-safe and subnormal distance", ExtremeDistances), ("DPI vectors and unscaled origins", Conversions),
            ("conversion failures", ConversionFailures), ("point, vector and velocity arithmetic", Arithmetic),
            ("conservative nonzero footprint at large origins", FootprintPrecision)
        };
        foreach (var test in cases)
        {
            try { test.Run(); Console.WriteLine("PASS: " + test.Name); }
            catch (Exception e) { failures++; Console.Error.WriteLine("FAIL: " + test.Name + ": " + e.Message); }
        }
        Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks} checks, {cases.Length} cases, {failures} failed cases; pure net10.0 geometry only");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static void Near(double actual, double expected) => Check(double.IsFinite(actual) &&
        Math.Abs(actual - expected) <= 1e-11 * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");
    private static void Place(PhysicalPoint point, IReadOnlyList<MonitorSnapshot> screens, PhysicalFootprint body,
        string id, double x, double y, string? preferred = null)
    {
        var r = DesktopGeometry.Resolve(point, screens, body, preferred);
        Check(r.Success && r.Failure == PlacementFailure.None, "placement failed: " + r.Failure);
        Check(r.MonitorId == id, $"expected {id}, got {r.MonitorId}"); Near(r.GroundAnchor.X, x); Near(r.GroundAnchor.Y, y);
    }
    private static void Fail(PhysicalPoint point, IReadOnlyList<MonitorSnapshot>? screens, PhysicalFootprint body, PlacementFailure failure)
    {
        var r = DesktopGeometry.Resolve(point, screens, body);
        Check(!r.Success && r.Failure == failure, $"expected {failure}, got {r.Failure}");
        Check(double.IsFinite(r.GroundAnchor.X) && double.IsFinite(r.GroundAnchor.Y), "failure output stays finite");
    }
    private static void Ground()
    {
        Place(new(100, 1040), new[] { MainScreen }, Body, "main", 100, 1040);
        Place(new(100, 1070), new[] { MainScreen }, Body, "main", 100, 1040);
        Place(new(1920, 1080), new[] { MainScreen }, Body, "main", 1890, 1040);
        Check(MainScreen.WorkArea.ContainsClosed(new(1920, 1040)), "ground/right corner included");
        Check(!MainScreen.WorkArea.ContainsClosed(new(100, 1040.001)), "below ground excluded");
    }
    private static void Taskbars()
    {
        var lt = Screen("reserved", new(0, 0, 1920, 1080), new(40, 30, 1920, 1080));
        Place(new(10, 20), new[] { lt }, Body, "reserved", 60, 110);
        Place(new(100, 0), new[] { lt }, default, "reserved", 100, 30);
        var right = Screen("right", new(0, 0, 2000, 1080), new(0, 0, 1940, 1080));
        Place(new(1990, 500), new[] { right }, Body, "right", 1910, 500);
    }
    private static void BoundsFirst()
    {
        var a = Screen("A", new(0, 0, 1000, 1000), new(0, 0, 1000, 500));
        var b = Screen("B", new(1100, 0, 2000, 1000));
        // B's work area is closer, but the release remains in A's reserved region.
        Place(new(995, 995), new[] { b, a }, default, "A", 995, 500, "B");
    }
    private static void Horizontal()
    {
        var left = Screen("left", new(-1600, 0, 0, 900), new(-1600, 0, 0, 860));
        Place(new(-800, 860), new[] { MainScreen, left }, Body, "left", -800, 860);
        Place(new(500, 1040), new[] { left, MainScreen }, Body, "main", 500, 1040);
        Place(new(-1600, 900), new[] { left, MainScreen }, Body, "left", -1580, 860);
    }
    private static void Vertical()
    {
        var upper = Screen("upper", new(-400, -1000, 1200, 0), new(-400, -1000, 1200, -40));
        Place(new(100, -100), new[] { upper, MainScreen }, Body, "upper", 100, -100);
        Place(new(1000, -40), new[] { upper, MainScreen }, Body, "upper", 1000, -40);
        Place(new(100, 0), new[] { upper, MainScreen }, Body, "upper", 100, -40, "upper");
        Place(new(100, 0), new[] { upper, MainScreen }, Body, "main", 100, 80, "main");
    }
    private static void Gaps()
    {
        var a = Screen("A", new(0, 0, 100, 100)); var b = Screen("B", new(200, 0, 300, 100));
        Place(new(150, 50), new[] { b, a }, default, "A", 100, 50);
        Place(new(150, 50), new[] { a, b }, default, "B", 200, 50, "B");
        Place(new(180, 50), new[] { a, b }, default, "B", 200, 50, "A");
        Place(new(500, 500), new[] { a, b }, default, "B", 300, 100);
        Place(new(150, 50), new[] { Screen("A", a.Bounds, new(0, 0, 80, 100)), b }, default, "B", 200, 50);
        Place(new(150, 50), new[] { a, b }, Body, "A", 70, 80);
    }
    private static void Corners()
    {
        Place(new(200, 200), new[] { Screen("vertical", new(190, -100, 210, 40)), Screen("diagonal", new(0, 0, 100, 100)) },
            default, "diagonal", 100, 100, "vertical");
    }
    private static void SharedEdges()
    {
        var a = Screen("A", new(0, 0, 100, 100)); var b = Screen("B", new(100, 0, 200, 100));
        foreach (var order in new[] { new[] { a, b }, new[] { b, a } })
        {
            Place(new(100, 50), order, default, "A", 100, 50);
            Place(new(100, 50), order, default, "B", 100, 50, "B");
            Place(new(100, 50), order, default, "A", 100, 50, "disconnected");
            Place(new(100, 100), order, Body, "B", 120, 100, "B");
        }
        var c = Screen("C", new(100, 100, 200, 200));
        Place(new(100, 100), new[] { c, b, a }, default, "A", 100, 100);
        Place(new(100, 100), new[] { c, b, a }, default, "C", 100, 100, "C");
        Place(new(50, 50), new[] { Screen("a", a.Bounds), a }, default, "A", 50, 50);
    }
    private static void Snapshots()
    {
        Fail(default, null, default, PlacementFailure.NoValidMonitors);
        Fail(default, Array.Empty<MonitorSnapshot>(), default, PlacementFailure.NoValidMonitors);
        var bad = new[] { default(MonitorSnapshot), Screen("", new(0, 0, 100, 100)), Screen(" ", new(0, 0, 100, 100)),
            Screen("zero", new(0, 0, 0, 100)), Screen("outside", new(0, 0, 100, 100), new(-1, 0, 100, 100)),
            Screen("nan", new(double.NaN, 0, 100, 100)), Screen("empty-work", new(0, 0, 100, 100), new(0, 0, 100, 0)) };
        Fail(new(10, 10), bad, default, PlacementFailure.NoValidMonitors);
        var mixed = new List<MonitorSnapshot>(bad) { MainScreen };
        Place(new(100, 1040), mixed, Body, "main", 100, 1040, "outside");
        Fail(new(10, 10), new[] { MainScreen, MainScreen }, default, PlacementFailure.DuplicateMonitorId);
        Fail(new(10, 10), new[] { MainScreen, Screen("main", new(-100, 0, 0, 100)) }, default, PlacementFailure.DuplicateMonitorId);
    }
    private static void Ranges()
    {
        Check(DesktopGeometry.TryGetAnchorRange(new(100, 200, 300, 400), new(10, 30, 40), out var r), "valid body interval");
        Check(r == new AnchorRange(110, 270, 240, 400), "literal asymmetric footprint interval");
        Check(DesktopGeometry.TryProject(new(105, 230), r, out var p) && p == new PhysicalPoint(110, 240), "low corner projection");
        Check(DesktopGeometry.TryProject(new(999, 999), r, out p) && p == new PhysicalPoint(270, 400), "high corner projection");
        Check(DesktopGeometry.TryProject(new(200, 300), r, out p) && p == new PhysicalPoint(200, 300), "do not add airborne free fall");
        var exact = Screen("exact", new(10, 20, 110, 120));
        Place(new(-200, -200), new[] { exact }, new(25, 75, 100), "exact", 35, 120);
        Check(DesktopGeometry.TryGetAnchorRange(exact.WorkArea, new(25, 75, 100), out r) && r == new AnchorRange(35, 35, 120, 120), "exact fit allows single-point interval");
        Place(new(90, 100), new[] { Screen("body", new(0, 0, 100, 100)) }, new(10, 10, 20), "body", 90, 100);
    }
    private static void Footprints()
    {
        var small = Screen("small", new(0, 0, 100, 100)); var large = Screen("large", new(200, 0, 1200, 1000));
        Fail(new(50, 50), new[] { small, large }, new(51, 50, 80), PlacementFailure.FootprintDoesNotFit);
        Fail(new(50, 50), new[] { small }, new(10, 10, 101), PlacementFailure.FootprintDoesNotFit);
        foreach (var f in new[] { new PhysicalFootprint(-1, 1, 1), new(1, -1, 1), new(1, 1, -1), new(double.NaN, 1, 1),
            new(1, double.PositiveInfinity, 1), new(1, 1, double.NegativeInfinity), new(double.MaxValue, double.MaxValue, 1) })
            Fail(new(50, 50), new[] { small }, f, PlacementFailure.InvalidFootprint);
        Check(!DesktopGeometry.TryGetAnchorRange(small.WorkArea, new(51, 50, 1), out var r) && r == default, "oversized range fails safely");
    }
    private static void InvalidNumbers()
    {
        foreach (var n in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Fail(new(n, 10), new[] { MainScreen }, Body, PlacementFailure.InvalidPoint);
            Fail(new(10, n), new[] { MainScreen }, Body, PlacementFailure.InvalidPoint);
            Check(!DesktopGeometry.TryProject(new(n, 0), new(0, 100, 0, 100), out var p) && p == default, "invalid projection resets point");
        }
        foreach (var r in new[] { new PhysicalRect(100, 0, 0, 100), new(0, 100, 100, 0), new(0, 0, 0, 100), new(0, 0, 100, 0),
            new(double.NaN, 0, 100, 100), new(0, 0, double.PositiveInfinity, 100), new(-double.MaxValue, 0, double.MaxValue, 100) })
        {
            Check(!r.IsValid && !r.ContainsClosed(default), "invalid rect has no containment");
            Check(!DesktopGeometry.TryGetAnchorRange(r, default, out var range) && range == default, "invalid rect has no interval");
        }
        foreach (var r in new[] { new AnchorRange(10, 0, 0, 100), new(0, 100, 10, 0), new(double.NaN, 0, 0, 100),
            new(0, double.PositiveInfinity, 0, 100), new(-double.MaxValue, double.MaxValue, 0, 100) })
            Check(!DesktopGeometry.TryProject(default, r, out var p) && p == default, "invalid interval cannot be clamped");
        Check(PhysicalRect.TryFromOriginAndSize(new(-100, -200), 50, 80, out var rect) && rect == new PhysicalRect(-100, -200, -50, -120), "retain negative origin");
        foreach (var size in new[] { -1d, 0d, double.NaN, double.PositiveInfinity })
        {
            Check(!PhysicalRect.TryFromOriginAndSize(default, size, 1, out var r) && r == default, "invalid width");
            Check(!PhysicalRect.TryFromOriginAndSize(default, 1, size, out r) && r == default, "invalid height");
        }
        Check(!PhysicalRect.TryFromOriginAndSize(new(double.MaxValue, 0), double.MaxValue, 1, out rect) && rect == default, "origin overflow");
        Check(!PhysicalRect.TryFromOriginAndSize(new(double.MaxValue, 0), 1, 1, out rect), "unrepresentable positive span");
        Check(!default(PlacementResult).Success, "default result is not success");
    }
    private static void ExtremeDistances()
    {
        var near = Screen("Z-near", new(1e200, 0, 1.1e200, 1e200)); var far = Screen("A-far", new(2e200, 0, 2.1e200, 1e200));
        Place(default, new[] { far, near }, default, "Z-near", 1e200, 0, "A-far");
        near = Screen("Z-near", new(0, 0, 1e307, 1e307)); far = Screen("A-far", new(1e308, 0, 1.1e308, 1e307));
        Place(new(-double.MaxValue, 0), new[] { far, near }, default, "Z-near", 0, 0, "A-far");
        near = Screen("Z-near", new(1.1e308, 1.1e308, 1.2e308, 1.2e308)); far = Screen("A-far", new(1.3e308, 1.3e308, 1.4e308, 1.4e308));
        Place(default, new[] { far, near }, default, "Z-near", 1.1e308, 1.1e308, "A-far");
        near = Screen("Z-near", new(0, 0, 1, 1)); far = Screen("A-far", new(1e308, 1e308, 1.1e308, 1.1e308));
        Place(new(-double.MaxValue, -double.MaxValue), new[] { far, near }, default, "Z-near", 0, 0, "A-far");
        near = Screen("Z-near", new(double.Epsilon, 0, double.Epsilon * 2, 1)); far = Screen("A-far", new(double.Epsilon * 2, 0, double.Epsilon * 3, 1));
        var tiny = DesktopGeometry.Resolve(default, new[] { far, near }, default, "A-far");
        Check(tiny.Success && tiny.MonitorId == "Z-near" && tiny.GroundAnchor.X == double.Epsilon, "subnormal distances must not become a tie");
    }
    private static void Conversions()
    {
        foreach (var sample in new (double Scale, double X, double Y)[] { (1, 12.4, -7.2), (1.25, 15.5, -9), (1.5, 18.6, -10.8) })
        {
            var s = new DpiScale(sample.Scale, sample.Scale);
            Check(s.TryToPhysical(new(12.4, -7.2), out var px), "DIP to physical vector"); Near(px.X, sample.X); Near(px.Y, sample.Y);
            Check(s.TryToLocalDip(px, out var dip), "physical vector to DIP"); Near(dip.X, 12.4); Near(dip.Y, -7.2);
            foreach (var v in new[] { -321.125, -.1, 0, .333333333333, 901.75 })
            {
                Check(s.TryToPhysical(new(v, -v), out px) && s.TryToLocalDip(px, out dip), "fractional round trip"); Near(dip.X, v); Near(dip.Y, -v);
            }
        }
        Check(new DpiScale(1.25, 1.5).TryToPhysical(new(4, -2), out var p) && p == new PhysicalVector(5, -3), "axis scales explicit");
        Check(new DpiScale(1.5, 1.5).TryToPhysical(new(10, -20), out p), "scale displacement only");
        Check(new PhysicalPoint(-1920, -1080).TryTranslate(p, out var point) && point == new PhysicalPoint(-1905, -1110), "world origin added unchanged");
    }
    private static void ConversionFailures()
    {
        foreach (var n in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(!new DpiScale(n, 1).TryToPhysical(new(1, 1), out var p) && p == default, "invalid X scale");
            Check(!new DpiScale(1, n).TryToLocalDip(new(1, 1), out var d) && d == default, "invalid Y scale");
        }
        var s = new DpiScale(1.5, 1.5);
        Check(!s.TryToPhysical(new(double.NaN, 1), out var px) && px == default, "invalid local vector");
        Check(!s.TryToLocalDip(new(1, double.PositiveInfinity), out var dip) && dip == default, "invalid physical vector");
        Check(!s.TryToPhysical(new(double.MaxValue, 0), out px) && px == default, "multiplication overflow");
        Check(!new DpiScale(double.Epsilon, 1).TryToLocalDip(new(double.MaxValue, 0), out dip) && dip == default, "division overflow");
        Check(!new DpiScale(.5, 1).TryToPhysical(new(double.Epsilon, 0), out px) && px == default, "nonzero conversion underflow");
        Check(!new DpiScale(2, 1).TryToLocalDip(new(double.Epsilon, 0), out dip) && dip == default, "inverse underflow");
        Check(s.TryToPhysical(default, out px) && px == default, "zero vector valid");
    }
    private static void Arithmetic()
    {
        Check(PhysicalVector.TryBetween(new(-1920, -1080), new(-1905, -1110), out var v) && v == new PhysicalVector(15, -30), "world difference gives displacement");
        Check(!PhysicalVector.TryBetween(new(-double.MaxValue, 0), new(double.MaxValue, 0), out v) && v == default, "point subtraction overflow");
        Check(!PhysicalVector.TryBetween(new(double.NaN, 0), default, out v), "invalid from point");
        Check(!PhysicalVector.TryBetween(default, new(0, double.PositiveInfinity), out v), "invalid to point");
        Check(!new PhysicalPoint(double.MaxValue, 0).TryTranslate(new(double.MaxValue, 0), out var p) && p == default, "translation overflow");
        Check(!new PhysicalPoint(double.NaN, 0).TryTranslate(default, out p), "invalid translated origin");
        Check(!default(PhysicalPoint).TryTranslate(new(0, double.PositiveInfinity), out p), "invalid translation vector");
        Check(new PhysicalVelocity(100, -20).TryGetDisplacement(1.5, out v) && v == new PhysicalVector(150, -30), "velocity px/s times seconds");
        Check(new PhysicalVelocity(100, -20).TryGetDisplacement(0, out v) && v == default, "zero elapsed seconds");
        foreach (var n in new[] { -1d, double.NaN, double.PositiveInfinity })
            Check(!new PhysicalVelocity(100, -20).TryGetDisplacement(n, out v) && v == default, "invalid elapsed seconds");
        Check(!new PhysicalVelocity(double.PositiveInfinity, 0).TryGetDisplacement(1, out v), "invalid velocity");
        Check(!new PhysicalVelocity(double.MaxValue, 0).TryGetDisplacement(2, out v) && v == default, "velocity overflow");
        Check(!new PhysicalVelocity(double.Epsilon, 0).TryGetDisplacement(.5, out v), "velocity underflow");
    }

    private static void FootprintPrecision()
    {
        // The representable step here is 2 px. Rounding inward must retain the
        // real nonzero occupancy, not silently permit an anchor on a raw edge.
        foreach (var origin in new[] { 1e16, -1e16 })
        {
            var screen = Screen("large-origin", new(origin, origin, origin + 4, origin + 4));
            Check(DesktopGeometry.TryGetAnchorRange(screen.WorkArea, new(1, 1, 1), out var range), "inward interval exists");
            Check(range == new AnchorRange(origin + 2, origin + 2, origin + 2, origin + 4), "positive insets never rounded away");
            Place(new(origin, origin), new[] { screen }, new(1, 1, 1), "large-origin", origin + 2, origin + 2);
            var noRepresentableAnchor = Screen("unrepresentable", new(origin, 0, origin + 2, 10));
            Fail(new(origin, 5), new[] { noRepresentableAnchor }, new(1, 1, 1), PlacementFailure.NumericalOverflow);
            Check(!DesktopGeometry.TryGetAnchorRange(noRepresentableAnchor.WorkArea, new(1, 1, 1), out range) && range == default,
                "no representable anchor fails without an invalid clamp");
        }
    }
}
