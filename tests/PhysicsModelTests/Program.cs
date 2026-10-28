using System.Reflection;
using NecoArcDesktopPet.Coordinates;

internal static partial class Program
{
    static int passed, failed;
    static double maximumBallisticPositionError, maximumBallisticVelocityError;
    static readonly Dictionary<string, (int Pass, int Fail)> Categories = new();
    static void Case(string category, string name, Action test)
    {
        Categories.TryGetValue(category, out var count);
        try { test(); passed++; Categories[category] = (count.Pass + 1, count.Fail); }
        catch (Exception ex) { failed++; Categories[category] = (count.Pass, count.Fail + 1); Console.WriteLine($"FAIL [{category}] {name}: {ex.Message}"); }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Near(double actual, double expected, double tolerance = 1e-7)
        => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"expected {expected:R}, got {actual:R}, tolerance {tolerance}");
    static int Main()
    {
        // RED bootstrap deliberately checks the real source-linked assembly, with no mock core.
        Case("Dependency", "independent pure core exists", () => Check(
            Assembly.GetExecutingAssembly().GetType("NecoArcDesktopPet.Pilot.Physics.PhysicsStep") != null,
            "PhysicsStep is missing"));
        RunCases();
        foreach (var item in Categories.OrderBy(x => x.Key)) Console.WriteLine($"{item.Key}: PASS {item.Value.Pass}, FAIL {item.Value.Fail}");
        Console.WriteLine($"Ballistic maximum absolute error: position={maximumBallisticPositionError:R} px, velocity={maximumBallisticVelocityError:R} px/s");
        Console.WriteLine($"TOTAL: PASS {passed}, FAIL {failed}");
        return failed == 0 ? 0 : 1;
    }
    static partial void RunCases();
}
