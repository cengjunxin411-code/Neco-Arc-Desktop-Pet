using System.IO;
using NecoArcDesktopPet.Settings;
using NecoArcDesktopPet.Pilot.Audio;
static class Program {
 static int passed, failed;
 static void Check(bool condition, string message) { if(!condition)throw new Exception(message); }
 static void Case(string name, Action test) {try {test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
 static int Main(string[] args) {
  var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
  var keys=new[]{"NECO_SOURCE_ONLY_SETTINGS_DIR","NECO_PILOT_VOICE_DIR","NECO_VOICE_ROOT"};
  var old=keys.ToDictionary(k=>k, Environment.GetEnvironmentVariable); var cwd=Environment.CurrentDirectory;
  try {
   Case("explicit settings sandbox",()=>{
    Environment.SetEnvironmentVariable(keys[0],root);
    var store=new PetSettingsStore();Check(store.FilePath==Path.Combine(root,"settings.json"),"default store must use the explicit sandbox");
   });
   Case("relative override fails closed",()=>{
    Environment.SetEnvironmentVariable(keys[0],"relative-path");Check(new PetSettingsStore().FilePath==null,"relative root must not access a profile");
   });
   Case("UNC override fails closed",()=>{
    Environment.SetEnvironmentVariable(keys[0],@"\\server\share");Check(new PetSettingsStore().FilePath==null,"UNC root must not access a profile");
   });
   Case("default profile separates full version",()=>{
    Environment.SetEnvironmentVariable(keys[0],null);
    var path=new PetSettingsStore().FilePath;Check(path!=null && path.EndsWith(Path.Combine("NecoArcDesktopPet.SourceOnly","settings.json")),"dedicated SourceOnly profile required");
   });
   Case("startup audio does not discover cwd or legacy env",()=>{
    var fake=Path.Combine(root,"unrelated");Directory.CreateDirectory(Path.Combine(fake,"NecoPetMaterials","Voice"));
    Environment.CurrentDirectory=fake;Environment.SetEnvironmentVariable(keys[1],fake);Environment.SetEnvironmentVariable(keys[2],fake);
    var found=PilotStartupVoicePool.ResolveVoiceRootDirectory();
    Check(found==null || found==Path.Combine(AppContext.BaseDirectory,"Voice"),"only explicit root or executable Voice may resolve");
   });
   Case("explicit local voice directory is supported",()=>Check(PilotStartupVoicePool.ResolveVoiceRootDirectory(root)==root,"explicit legal local root remains supported"));
   Case("missing WAV pool stays unavailable",()=>Check(!PilotStartupVoicePool.CheckAssetsAvailability(root,out var missing)&&missing.Count==4,"absent WAV is not audio success"));
  } finally { Environment.CurrentDirectory=cwd;foreach(var key in keys)Environment.SetEnvironmentVariable(key,old[key]); }
  Console.WriteLine($"TOTAL: PASS {passed}, FAIL {failed}");return failed==0?0:1;
 }
}
