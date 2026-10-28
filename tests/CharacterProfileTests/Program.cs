using System.IO;
using System.Text.Json.Nodes;
using NecoArcDesktopPet;
using NecoArcDesktopPet.CharacterProfiles;

static class Program
{
    const string Key = "NecoPetMaterials/idle/base_idle.gif";
    // Independently constructed block geometry, never sampled from character media.
    const string Valid = """
    {"schemaVersion":1,"id":"synthetic-block","canonicalCanvas":{"width":80,"height":120},"logicalAspect":0.75,
      "animations":{"NecoPetMaterials/idle/base_idle.gif":{"canvas":{"width":80,"height":120},
        "visual":{"scale":1.5,"offsetXFactor":0.1,"offsetYFactor":-0.2},
        "contacts":{"top":{"x":0.5,"y":0.9},"left":{"x":0.85,"y":0.5},"right":{"x":0.15,"y":0.5}},
        "detectionBounds":{"x":8,"y":12,"width":64,"height":96},
        "headAnchor":{"x":0.5,"y":0.2},"bodyAnchor":{"x":0.5,"y":0.65},
        "sideOverlay":{"wallInset":3,"bottomToContact":24}}}}
    """;
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static JsonObject Fixture() => JsonNode.Parse(Valid)!.AsObject();
    static JsonObject Clip(JsonObject root) => root["animations"]![Key]!.AsObject();
    static CharacterProfileLoadResult Parse(JsonNode root) => CharacterProfileLoader.Parse(root.ToJsonString());
    static void Bad(Action<JsonObject> mutate, string name) {
        var root = Fixture(); mutate(root); var result = Parse(root);
        Check(!result.Success && result.Profile == null && result.Code != "Valid", name + " rejects the WHOLE snapshot");
    }
    static int Main(string[] args)
    {
        try
        {
            var rootPath = Path.GetFullPath(args[0]); Directory.CreateDirectory(rootPath);
            var good = CharacterProfileLoader.Parse(Valid);
            Check(good.Success && good.Code == "Valid", "valid structure parses");
            var profile = good.Profile!;
            Check(profile.MatchesCanvas(Key, 80, 120), "configured source canvas matches");
            Check(!profile.MatchesCanvas(Key, 81, 120) && !profile.MatchesCanvas("other.gif",80,120), "canvas mismatch and unmapped clip refuse");
            Check(profile.TryAnimation(Key.Replace('/','\\'),out _), "caller slash normalization is deterministic");
            Check(profile.HasContact(Key,AttachmentSide.Top) && profile.HasContact(Key,AttachmentSide.Left), "explicit contacts grant only defined capabilities");
            Check(!profile.HasContact(null,AttachmentSide.Top), "missing resource has no contact");
            Bad(r=>r.Remove("schemaVersion"), "missing schema");
            Bad(r=>r["schemaVersion"]=9, "unknown schema");
            Bad(r=>r.Remove("id"), "missing id");
            Bad(r=>r["id"]="  ", "blank id");
            Bad(r=>r.Remove("canonicalCanvas"), "missing canonical canvas");
            Bad(r=>r["animations"]=new JsonObject(), "empty mapping");
            Bad(r=>Clip(r).Remove("canvas"), "missing clip dimensions");
            Bad(r=>Clip(r).Remove("visual"), "missing clip visual");
            Bad(r=>Clip(r)["visual"]!.AsObject().Remove("scale"), "missing visual scale");
            Bad(r=>Clip(r)["contacts"]!["top"]!.AsObject().Remove("x"), "incomplete point");
            foreach(var number in new[]{0d,-1d,0.5,20000d})
                Bad(r=>Clip(r)["canvas"]!["width"]=number,"invalid canvas "+number);
            foreach(var number in new[]{0d,-1d,1000d})
                Bad(r=>Clip(r)["visual"]!["scale"]=number,"invalid scale "+number);
            foreach(var number in new[]{-0.01,1.01})
                Bad(r=>Clip(r)["contacts"]!["top"]!["x"]=number,"out-of-canvas contact "+number);
            Bad(r=>Clip(r)["detectionBounds"]!["width"]=-1,"negative rectangle");
            Bad(r=>Clip(r)["detectionBounds"]!["width"]=0,"empty rectangle");
            Bad(r=>Clip(r)["detectionBounds"]!["x"]=79,"overflowing rectangle");
            Bad(r=>Clip(r)["headAnchor"]!["y"]=2,"invalid head anchor");
            Bad(r=>Clip(r)["sideOverlay"]!["wallInset"]=41,"invalid overlay inset");
            Bad(r=>r["safetyStop"]=false,"attempt to configure safety");
            Bad(r=>Clip(r)["physicalOwnership"]=false,"attempt to configure ownership");
            foreach(var key in new[]{"../idle.gif","https://example.invalid/idle.gif","NecoPetMaterials/../idle.gif","/NecoPetMaterials/idle.gif","NecoPetMaterials/idle/a.gif?x=1","NecoPetMaterials/idle/a.gif\n"})
                Bad(r=>{var clip=Clip(r).DeepClone();r["animations"]=new JsonObject{[key]=clip};},"invalid resource key");
            Bad(r=>r["animations"]![Key.ToUpperInvariant()]=Clip(r).DeepClone(),"case-colliding resources");
            foreach(var token in new[]{"NaN","Infinity","-Infinity","1e999","\"NaN\"","\"Infinity\""}) {
                var json=Valid.Replace("\"scale\":1.5","\"scale\":"+token);
                Check(!CharacterProfileLoader.Parse(json).Success,"nonfinite/overflow token "+token);
            }
            Check(!CharacterProfileLoader.Parse("{").Success,"invalid JSON");
            Check(!CharacterProfileLoader.Parse(Valid.Replace("\"id\":","\"id\":\"duplicate\",\"id\":")).Success,"duplicate properties");
            Check(!CharacterProfileLoader.Parse(Valid.Replace("\"id\":","\"ID\":\"duplicate\",\"id\":")).Success,"case duplicate properties");
            Check(!CharacterProfileLoader.Parse(new string(' ',CharacterProfileLoader.MaximumBytes+1)).Success,"oversized input");
            var partial=Fixture(); Clip(partial).Remove("contacts"); var noContacts=Parse(partial).Profile!;
            Check(noContacts!=null&&!noContacts.HasContact(Key,AttachmentSide.Top)&&!noContacts.HasContact(Key,AttachmentSide.Right),"missing optional contacts deny attachment");
            Clip(partial)["contacts"]=new JsonObject { ["top"]=new JsonObject { ["x"]=0.5,["y"]=0.9 } };
            var topOnly=Parse(partial).Profile!;
            Check(topOnly.HasContact(Key,AttachmentSide.Top)&&!topOnly.HasContact(Key,AttachmentSide.Left),"top contact never fabricates a side");
            Clip(partial).Remove("sideOverlay");
            Check(!Parse(partial).Profile!.CanOverlay(Key,AttachmentSide.Left),"missing side overlay denies side action");
            Check(!noContacts!.CanOverlay(Key,AttachmentSide.Top),"missing support denies top overlay");
            var file=Path.Combine(rootPath,"profile.json"); File.WriteAllText(file,Valid);
            var first=CharacterProfileLoader.LoadFile(file).Profile!;
            Check(first.Id=="synthetic-block","explicit local file reads");
            var next=Fixture();next["id"]="second-revision";Clip(next)["visual"]!["scale"]=2;
            File.WriteAllText(file,next.ToJsonString());
            var second=CharacterProfileLoader.LoadFile(file).Profile!;
            Check(first.Id=="synthetic-block"&&first.Animations[Key].Visual.Scale==1.5&&second.Id=="second-revision"&&second.Animations[Key].Visual.Scale==2,"independent immutable revisions never mix");
            File.WriteAllText(file,"{");
            Check(!CharacterProfileLoader.LoadFile(file).Success&&first.Animations.Count==1,"failed reread cannot corrupt prior snapshot");
            Check(CharacterProfileLoader.LoadFile(null).Code=="Missing","missing selection");
            Check(CharacterProfileLoader.LoadFile(Path.Combine(rootPath,"absent.json")).Code=="Missing","missing file");
            Check(!CharacterProfileLoader.LoadFile("profile.json").Success,"relative file does not search cwd or parents");
            Check(!CharacterProfileLoader.LoadFile(@"\\server\share\profile.json").Success,"network file refuses before IO");
            File.WriteAllText(file,Valid);Environment.SetEnvironmentVariable("NECO_CHARACTER_PROFILE",file);
            var pinned=CharacterProfileRuntime.Result;
            File.WriteAllText(file,next.ToJsonString());Environment.SetEnvironmentVariable("NECO_CHARACTER_PROFILE",Path.Combine(rootPath,"absent.json"));
            Check(ReferenceEquals(pinned,CharacterProfileRuntime.Result)&&CharacterProfileRuntime.Current!.Id=="synthetic-block","process snapshot remains pinned across file/env replacement");
            GeometryCases.Run(profile,Check);
            PathAdmissionCases.Run(rootPath,Valid,Check);
            Console.WriteLine($"PASS {checks} Character Profile checks");return 0;
        }
        catch(Exception e) {Console.Error.WriteLine("FAIL "+e);return 1;}
    }
}
