using DpiHwndProbe;
using System.Reflection;

internal static partial class Program
{
    private static void AdmissionTests()
    {
        // Run the same entry protocol consumed by Program, not a test-only admission implementation.
        object Entry(params string[] args)
        {
            var method = typeof(RecoveryAdmission).GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic);
            Check(method != null, "explicit experimental admission protocol missing");
            return method!.Invoke(null, new object[] { args })!;
        }
        T Get<T>(object entry, string property) => (T)entry.GetType().GetProperty(property)!.GetValue(entry)!;
        var trial = new[] { "--manual", "--policy", "constant", "--recovery-mode", "candidate", "--experimental-candidate-trial" };
        Case("H visual opt-in requires the complete Candidate Constant manual entry", () => {
            var visual=trial.Concat(new[]{"--experimental-visual-constant"}).ToArray();
            Check(Get<int>(Entry(visual),"ExitCode")==0,"visual experiment must admit complete explicit entry");
            Check(Get<bool>(Entry(visual),"VisualConstant") && !Get<bool>(Entry(trial),"VisualConstant"),"visual mode is explicit and default off");
            foreach(var invalid in new[]{
                new[]{"--manual","--experimental-visual-constant"},
                new[]{"--sample","--experimental-visual-constant"},
                new[]{"--recovery-self-test","--experimental-visual-constant"},
                visual.Concat(new[]{"--experimental-visual-constant"}).ToArray(),
                visual.Concat(new[]{"--ordinary"}).ToArray(),
                visual.Select(x=>x=="constant" ? "baseline" : x).ToArray(),
                visual.Select(x=>x=="candidate" ? "legacy" : x).ToArray(),
                visual.Where(x=>x!="--experimental-candidate-trial").ToArray()
            }) Check(Get<int>(Entry(invalid),"ExitCode")!=0,"unsafe visual entry must reject");
        });
        Case("H physical layout uses each current 192 144 reverse scale without scaling world anchor",()=> {
            foreach(var scale in new[]{2.0,1.5,2.0,1.5}) {
                var anchor=new NecoArcDesktopPet.Coordinates.PhysicalPoint(-1200,700);
                Check(ProbeGeometry.TryLayout(anchor,1,Map(scale,scale),out var layout,out _),"current live scale layout");
                Near(layout!.CanvasDip.X*scale,240,"complete Canvas width px");Near(layout.CanvasDip.Y*scale,180,"complete Canvas height px");
                Near(layout.BodyDip.X*scale,180,"Body width px");Near(layout.BodyDip.Y*scale,120,"Body height px");
                Near(layout.AnchorDip.X*scale,120,"local anchor X px");Near(layout.AnchorDip.Y*scale,150,"local anchor Y px");
                Near(layout.Origin.X+layout.AnchorDip.X*scale,anchor.X,"world X is not globally scaled");
                Near(layout.Origin.Y+layout.AnchorDip.Y*scale,anchor.Y,"world Y is not globally scaled");
            }
        });
        Case("SHARED ENTRY explicit four-part experimental opt-in is admitted without unlocking ordinary Candidate", () => {
            var e = Entry(trial);
            Check(Get<int>(e, "ExitCode") == 0 && Get<bool>(e, "ExperimentalOptIn"), "complete opt-in must reach runtime guards");
            Check(Get<string>(e, "RecoveryModeRequested") == "candidate" && Get<bool>(e, "Manual"), "manual Candidate effective input preserved");
            Check(!RecoveryAdmission.UserTrialAllowed, "ordinary Candidate remains locked");
            Check(Get<int>(Entry("--manual", "--recovery-mode", "candidate"), "ExitCode") == 3, "Candidate alone must remain blocked");
            Check(Get<int>(Entry("--recovery-mode", "candidate"), "ExitCode") == 3, "original mode-only Candidate exit 3 preserved");
        });
        Case("SHARED ENTRY default and explicit Legacy preserve manual/sample policies", () => {
            foreach (var args in new[] { new[] { "--manual" }, new[] { "--sample", "--recovery-mode", "legacy", "--policy", "baseline" } }) {
                var e = Entry(args);
                Check(Get<int>(e, "ExitCode") == 0 && !Get<bool>(e, "ExperimentalOptIn") && Get<string>(e, "RecoveryModeRequested") == "legacy", "Legacy unchanged");
            }
        });
        Case("SHARED ENTRY missing consent components and conflicting commands decline before WPF", () => {
            foreach (var args in new[] {
                new[] { "--experimental-candidate-trial", "--manual" },
                trial.Where(x => x != "--manual").ToArray(),
                new[] { "--manual", "--recovery-mode", "candidate", "--experimental-candidate-trial" },
                new[] { "--manual", "--policy", "baseline", "--recovery-mode", "candidate", "--experimental-candidate-trial" },
                new[] { "--manual", "--policy", "bogus" },
                new[] { "--manual", "--sample" },
                new[] { "--manual", "--recovery-mode", "candidate", "--recovery-mode", "legacy" },
                new[] { "--manual", "--policy" },
                new[] { "--manual", "--policy", "--trace-stall" },
                new[] { "--manual", "--unknown-switch" },
                new[] { "--manual", "--target-exe", "x" },
                new[] { "--recovery-self-test", "--manual" },
                trial.Concat(new[] { "--sample" }).ToArray(),
                trial.Concat(new[] { "--ordinary" }).ToArray(),
                trial.Concat(new[] { "--inspect" }).ToArray(),
                trial.Concat(new[] { "--manifest", "x.exe" }).ToArray(),
                trial.Concat(new[] { "--recovery-self-test" }).ToArray(),
                trial.Concat(new[] { "--experimental-candidate-trial" }).ToArray()
            }) {
                var e = Entry(args);
                Check(Get<int>(e, "ExitCode") != 0 && !string.IsNullOrWhiteSpace(Get<string>(e, "Reason")), "invalid combination must explain refusal: " + string.Join(' ', args));
            }
        });
        Case("SHARED ENTRY rejection retains trailing new log path without accepting duplicate override", () => {
            foreach(var args in new[]{trial.Concat(new[]{"--recovery-mode","legacy","--log","new.jsonl"}).ToArray(),trial.Concat(new[]{"--unknown","--log","new.jsonl"}).ToArray()}) {
                var e=Entry(args);
                Check(Get<int>(e,"ExitCode")!=0 && Get<string>(e,"LogPath")=="new.jsonl","refusal must still reach requested evidence log");
                Check(Get<string>(e,"RecoveryModeRequested")=="candidate","duplicate option cannot override first requested mode");
            }
        });
    }
}
