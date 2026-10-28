using System.Reflection;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Pilot.Physics;
using NecoArcDesktopPet.Settings;

internal static partial class Program
{
    static int passed, failed;
    static readonly string Root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("NECO_TEST_OUTPUT_ROOT") ?? Path.Combine("docs", "reports", "Phase-19B1-fixtures"), Guid.NewGuid().ToString("N")));
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Case(string name, Action test) {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); }
    }
    static int Main() {
        Directory.CreateDirectory(Root);
        CliCases(); SettingsCases(); GateCases();
        Console.WriteLine($"TOTAL: PASS {passed}, FAIL {failed}; fixtures={Root}");
        return failed == 0 ? 0 : 1;
    }
    static partial void GateCases();
    static void CliCases() {
        var flags = new[]{"--experimental-cross-screen", "--experimental-seam-handoff", "--experimental-rocket",
            "--experimental-attachment", "--experimental-rest-sleep", "--experimental-random-idle", "--experimental-autonomous-movement"};
        Case("CLI empty remains valid and physics disabled", () => {
            var options = PilotOptions.Parse([]);
            Check(options.Valid && !(bool)(typeof(PilotOptions).GetProperty("Physics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(options) ?? false), "default disabled");
        });
        Case("CLI physics opt-in accepted", () => Check(PilotOptions.Parse(["--experimental-physics"]).Valid, "new flag rejected"));
        // Exhaust every old combination with and without the new flag; seam dependency stays exact.
        for (int mask = 0; mask < 128; mask++) foreach (bool physics in new[]{false,true}) {
            var args = flags.Where((_,i) => (mask & (1 << i)) != 0).Concat(physics ? new[]{"--experimental-physics"} : []).ToArray();
            bool expected = (mask & 2) == 0 || (mask & 1) != 0;
            Case($"CLI mask={mask} physics={physics}", () => {
                var options=PilotOptions.Parse(args);Check(options.Valid==expected,"combination validity changed");
                if (!expected) return;
                var names=new[]{"CrossScreen","SeamHandoff","Rocket","Attachment","RestSleep","RandomIdle","AutonomousMovement","Physics"};
                for(int i=0;i<names.Length;i++) Check((bool)typeof(PilotOptions).GetProperty(names[i])!.GetValue(options)! ==
                    (i==7?physics:(mask&(1<<i))!=0),"flag value lost: "+names[i]);
            });
        }
        foreach (var flag in flags.Append("--experimental-physics"))
            Case("CLI duplicate " + flag, () => Check(!PilotOptions.Parse(["--experimental-cross-screen",flag,flag]).Valid, "duplicate accepted"));
        foreach (var args in new[]{new[]{"--unknown"},new[]{"--experimental-physics=true"},new[]{"--experimental-physics","Gravity"},
            new[]{"--duration-seconds"},new[]{"--duration-seconds","0"},new[]{"--duration-seconds","31"},new[]{"--log","--experimental-physics"}})
            Case("CLI rejects " + string.Join(" ",args), () => Check(!PilotOptions.Parse(args).Valid, "bad args accepted"));
        Case("CLI duration/log coexist", () => Check(PilotOptions.Parse(["--experimental-physics","--duration-seconds","30","--log","trial.jsonl"]).Valid,"valid options refused"));
    }
    static PetSettingsStore Store(string name = "pmv2-settings.json") => new(Path.Combine(Root, Guid.NewGuid().ToString("N"),name));
    static void Write(PetSettingsStore store, string json) { Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath!)!); File.WriteAllText(store.FilePath!,json); }
    static object? Mode(PetSettingsLoadResult loaded) => loaded.GetType().GetProperty("PhysicsMode", BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(loaded);
    static PetSettingsSaveResult Save(PetSettingsStore store, PhysicsMode mode, double? height = 260d) {
        var method=typeof(PetSettingsStore).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).SingleOrDefault(x=>x.Name=="Save"&&x.GetParameters().Length==4);
        Check(method!=null,"Pilot mode save boundary missing");
        return (PetSettingsSaveResult)method!.Invoke(store,[PetSettingsDefaults.Create(),false,height,mode])!;
    }
    static void SettingsCases() {
        Case("old v1 missing field loads Off without rewrite", () => {
            var store=Store(); Write(store,"{\"SchemaVersion\":1,\"Topmost\":false}"); var before=File.ReadAllBytes(store.FilePath!);
            var loaded=store.Load(); Check(loaded.Message==null&&!loaded.Topmost&&Equals(Mode(loaded),PhysicsMode.Off),"old mode default missing");
            Check(before.SequenceEqual(File.ReadAllBytes(store.FilePath!))&&!File.Exists(store.FilePath+".bak"),"load rewrote old JSON");
        });
        foreach(var mode in Enum.GetValues<PhysicsMode>()) Case("mode roundtrip and backup " + mode, () => {
            var store=Store(); Check(Save(store,mode).Success,"save failed");
            Check(Equals(Mode(new PetSettingsStore(store.FilePath).Load()),mode),"mode lost on reload");
            Check(new PetSettingsStore(store.FilePath).Save(PetSettingsDefaults.Create(),true).Success,"ordinary snapshot call failed");
            Check(Equals(Mode(store.Load()),mode),"snapshot save lost Pilot mode");
            // Existing backups are user-restored, never automatically substituted on corrupt Load.
            File.Copy(store.FilePath+".bak",store.FilePath!,true);
            Check(Equals(Mode(store.Load()),mode),"restored .bak lost mode");
        });
        foreach(var value in new[]{"\"gravity\"","\"Gravity, PlanarInertia\"","1","null","true","\"Unknown\"","\" Gravity\""})
            Case("invalid Pilot mode " + value, () => {
                var store=Store();Write(store,"{\"SchemaVersion\":1,\"PhysicsMode\":"+value+"}");
                var loaded=store.Load();Check(loaded.Message!=null&&Equals(Mode(loaded),PhysicsMode.Off),"bad mode not rejected safely");
            });
        Case("duplicate Pilot known mode rejected",()=>{
            var store=Store();Write(store,"{\"SchemaVersion\":1,\"PhysicsMode\":\"Off\",\"PhysicsMode\":\"Gravity\"}");
            Check(store.Load().Message!=null,"duplicate mode accepted");
        });
        Case("ordinary store keeps unknown-key contract and omits mode",()=>{
            var store=Store("settings.json");Write(store,"{\"SchemaVersion\":1,\"PhysicsMode\":42,\"PhysicsMode\":false}");
            Check(store.Load().Message==null,"unknown ordinary key behavior changed");
            var save=store.Save(PetSettingsDefaults.Create(),false);
            Check(save.Success,"ordinary save failed: " + save.Message + " " + save.Failure?.Details);
            Check(!File.ReadAllText(store.FilePath!).Contains("PhysicsMode"),"ordinary JSON polluted");
            Check(!Save(store,PhysicsMode.Gravity,null).Success,"Pilot mode accepted by ordinary store without size field");
        });
        Case("corrupt primary keeps valid backup and archives on explicit save",()=>{
            var store=Store();Check(Save(store,PhysicsMode.PlanarInertia).Success&&Save(store,PhysicsMode.Gravity).Success,"setup save");
            var backup=File.ReadAllBytes(store.FilePath+".bak");Write(store,"{\"SchemaVersion\":1,\"PhysicsMode\":99}");
            Check(Equals(Mode(store.Load()),PhysicsMode.Off),"corrupt load activated mode");
            Check(Save(store,PhysicsMode.Off).Success,"repair save");
            Check(backup.SequenceEqual(File.ReadAllBytes(store.FilePath+".bak")),"corrupt data rotated over good backup");
            Check(Directory.GetFiles(Path.GetDirectoryName(store.FilePath!)!,"*.corrupt-*.json").Length==1,"corrupt original not archived");
        });
    }
}
