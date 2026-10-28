using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

// Human/machine readable evidence for the 0.08 vs 1.0 air-drag comparison.
// Both columns run the same production PhysicsStep integrator with the same
// initial state; only PhysicsTuning.AirDrag differs. This is a test fixture,
// not a second production motion path.
static partial class Program
{
    const double ReportDt = 1d / 120;
    const int ReportSteps = 2400;

    static (double X, double Y, double Vx, double Vy) ClosedForm(
        double x0, double y0, double vx0, double vy0, double t, double k)
    {
        const double g = 1800;
        if (k <= 0)
            return (x0 + vx0 * t, y0 + vy0 * t + .5 * g * t * t, vx0, vy0 + g * t);
        var decay = Math.Exp(-k * t);
        return (x0 + vx0 * (1 - decay) / k,
            y0 + g / k * t + (vy0 - g / k) / k * (1 - decay),
            vx0 * decay, g / k + (vy0 - g / k) * decay);
    }

    static double? FirstHorizontalWallTime(double x0, double width, double vx, double wallRight, double k)
    {
        if (vx == 0) return null;
        var distance = vx > 0 ? wallRight - (x0 + width) : x0;
        if (distance <= 0) return 0;
        if (k <= 0) return distance / Math.Abs(vx);
        var ratio = 1 - k * distance / Math.Abs(vx);
        return ratio <= 0 ? null : -Math.Log(ratio) / k;
    }

    static double? CeilingTime(PhysicalRect start, double vy0, double ceiling, double k)
    {
        double Y(double t) => ClosedForm(start.Left, start.Top, 0, vy0, t, k).Y;
        if (vy0 >= 0) return null;
        if (Y(0) <= ceiling) return 0;
        double previous = 0;
        for (int i = 1; i <= 5000; i++)
        {
            var current = i * .002;
            if (Y(current) > ceiling) { previous = current; continue; }
            double low = previous, high = current;
            for (int j = 0; j < 200; j++)
            {
                var mid = (low + high) / 2;
                if (Y(mid) > ceiling) low = mid; else high = mid;
            }
            return high;
        }
        return null;
    }

    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static void TrajectoryReport()
    {
        var work = new PhysicalRect(0, 0, 1920, 1080);
        var scenarios = new (string Name, PhysicalRect Start, PhysicalVelocity Release)[]
        {
            ("horizontal-1200", new(400, 400, 420, 420), new(1200, 0)),
            ("diagonal-up-900-1200", new(400, 700, 420, 720), new(900, -1200)),
            ("vertical-up-1500", new(900, 800, 920, 820), new(0, -1500)),
            ("horizontal-3000", new(200, 300, 220, 320), new(3000, 0)),
            ("diagonal-down-1200-900", new(200, 300, 220, 320), new(1200, 900)),
            ("high-up-600-5000", new(900, 900, 920, 920), new(600, -5000))
        };
        var lines = new List<string>
        {
            "scenario,tuning,metric,time_s,step,x,y,vx,vy,detail"
        };
        var summary = new List<string>();

        foreach (var scenario in scenarios)
        {
            var rows = new List<(string Tuning, string Metric, string Time, string Step, string X, string Y, string Vx, string Vy, string Detail)>();
            foreach (var (label, k) in new[] { ("0.08", .08), ("1.0", 1.0) })
            {
                var tuning = new PhysicsTuning { AirDrag = k };
                var run = FlyTuned(scenario.Start, work, scenario.Release, tuning, ReportDt, ReportSteps);
                foreach (var t in new[] { .25, .5, 1.0 })
                {
                    var index = (int)Math.Round(t / ReportDt) - 1;
                    if (index < run.Track.Count)
                    {
                        var p = run.Track[index];
                        rows.Add((label, "sample", F(t), (index + 1).ToString(CultureInfo.InvariantCulture),
                            F(p.Left), F(p.Top), F(p.Vx), F(p.Vy), ""));
                    }
                }
                if (scenario.Release.Y < 0)
                {
                    var apexIndex = 0;
                    for (int i = 1; i < run.Track.Count; i++) if (run.Track[i].Top < run.Track[apexIndex].Top) apexIndex = i;
                    rows.Add((label, "apex", F((apexIndex + 1) * ReportDt), (apexIndex + 1).ToString(CultureInfo.InvariantCulture),
                        F(run.Track[apexIndex].Left), F(run.Track[apexIndex].Top), F(run.Track[apexIndex].Vx), F(run.Track[apexIndex].Vy), ""));
                }

                var grounds = run.Impacts.Where(i => i.Impact.Kind == CollisionKind.Ground).ToArray();
                var firstGroundStep = grounds.Length > 0 ? grounds[0].Step : int.MaxValue;
                foreach (var kind in new[] { CollisionKind.LeftWall, CollisionKind.RightWall, CollisionKind.Ceiling })
                {
                    var contacts = run.Impacts.Where(i => i.Impact.Kind == kind).ToArray();
                    if (contacts.Length == 0) continue;
                    var contact = contacts[0];
                    rows.Add((label, kind.ToString(), F((contact.Step + 1) * ReportDt), (contact.Step + 1).ToString(CultureInfo.InvariantCulture),
                        F(run.Track[contact.Step].Left), F(run.Track[contact.Step].Top),
                        F(run.Track[contact.Step].Vx), F(run.Track[contact.Step].Vy), "normal=" + F(contact.Impact.NormalSpeed)));
                    if (contact.Step <= firstGroundStep)
                        rows.Add((label, "airborne-" + kind, F((contact.Step + 1) * ReportDt), (contact.Step + 1).ToString(CultureInfo.InvariantCulture),
                            F(run.Track[contact.Step].Left), F(run.Track[contact.Step].Top),
                            F(run.Track[contact.Step].Vx), F(run.Track[contact.Step].Vy), "normal=" + F(contact.Impact.NormalSpeed)));
                }
                if (grounds.Length > 0)
                {
                    var ground = grounds[0];
                    rows.Add((label, "landing", F((ground.Step + 1) * ReportDt), (ground.Step + 1).ToString(CultureInfo.InvariantCulture),
                        F(run.Track[ground.Step].Left), F(run.Track[ground.Step].Top),
                        F(run.Track[ground.Step].Vx), F(run.Track[ground.Step].Vy), "normal=" + F(ground.Impact.NormalSpeed)));
                }
                var last = run.Track[^1];
                rows.Add((label, "final", F(run.Track.Count * ReportDt), run.Track.Count.ToString(CultureInfo.InvariantCulture),
                    F(last.Left), F(last.Top), F(last.Vx), F(last.Vy), "phase=" + last.Phase + ";bounces=" + last.Bounces));

                // Closed-form checks the report can be audited against.
                var wallTime = FirstHorizontalWallTime(scenario.Start.Left, scenario.Start.Width,
                    scenario.Release.X, work.Right, k);
                rows.Add((label, "analytic-wall-time", wallTime is { } wt ? F(wt) : "none", "", "", "", "", "", ""));
                var ceilingTime = CeilingTime(scenario.Start, scenario.Release.Y, work.Top, k);
                if (ceilingTime is { } ct)
                    rows.Add((label, "analytic-ceiling-time", F(ct), "", "", "", "", "", ""));
                var atSample = ClosedForm(scenario.Start.Left, scenario.Start.Top, scenario.Release.X, scenario.Release.Y, 1.0, k);
                rows.Add((label, "analytic-1s", "1", "", F(atSample.X), F(atSample.Y), F(atSample.Vx), F(atSample.Vy), ""));
            }

            foreach (var row in rows)
                lines.Add($"{scenario.Name},{row.Tuning},{row.Metric},{row.Time},{row.Step},{row.X},{row.Y},{row.Vx},{row.Vy},{row.Detail}");

            // Compact side-by-side table for the implementation report.
            string Cell(string metric, string column)
            {
                var row = rows.FirstOrDefault(r => r.Tuning == column && r.Metric == metric);
                return row.Metric is null ? "-" : $"({row.X},{row.Y}) v=({row.Vx},{row.Vy}) t={row.Time}";
            }
            summary.Add($"| {scenario.Name} | 0.08 | {Cell("sample", "0.08")} |");
            summary.Add($"| {scenario.Name} | 1.0 | {Cell("sample", "1.0")} |");
        }

        var path = Path.Combine("docs", "reports", "Phase-19C2-R1-evidence", "trajectory-comparison.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        Console.WriteLine("TRAJECTORY " + Path.GetFullPath(path));
        foreach (var row in lines.Where(l => l.Contains(",sample,") || l.Contains(",apex,") || l.Contains(",landing,") ||
            l.Contains(",Ceiling,") || l.Contains(",RightWall,") || l.Contains(",LeftWall,") || l.Contains(",final,") ||
            l.Contains(",airborne-")))
            Console.WriteLine(row);
    }
}
