using System;
using System.Collections.Generic;

namespace NecoArcDesktopPet.Pilot;

internal sealed record PilotOptions(bool Valid,bool CrossScreen,int? DurationSeconds,string? LogPath,string? Error,bool SeamHandoff=false,bool Rocket=false,bool Attachment=false,bool RestSleep=false,bool RandomIdle=false,bool AutonomousMovement=false,bool Physics=false)
{
    internal static PilotOptions Parse(string[] args)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);bool cross=false,seam=false,rocket=false,attachment=false,restSleep=false,randomIdle=false,autonomousMovement=false,physics=false;int? seconds=null;string? log=null;
        PilotOptions Bad(string reason)=>new(false,false,null,null,reason);
        for(int i=0;i<args.Length;i++) {
            var key=args[i];if(!seen.Add(key))return Bad("Duplicate argument: "+key);
            if(key=="--experimental-cross-screen") {cross=true;continue;}
            if(key=="--experimental-seam-handoff") {seam=true;continue;}
            if(key=="--experimental-rocket") {rocket=true;continue;}
            if(key=="--experimental-attachment") {attachment=true;continue;}
            if(key=="--experimental-rest-sleep") {restSleep=true;continue;}
            if(key=="--experimental-random-idle") {randomIdle=true;continue;}
            if(key=="--experimental-autonomous-movement") {autonomousMovement=true;continue;}
            if(key=="--experimental-physics") {physics=true;continue;}
            if(key is not ("--duration-seconds" or "--log"))return Bad("Unknown argument: "+key);
            if(++i>=args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--",StringComparison.Ordinal))return Bad("Missing value: "+key);
            if(key=="--log")log=args[i];
            else {if(!int.TryParse(args[i],out var n)||n<1||n>30)return Bad("duration-seconds must be 1..30");seconds=n;}
        }
        if(seam&&!cross)return Bad("experimental-seam-handoff requires experimental-cross-screen");
        return new(true,cross,seconds,log,null,seam,rocket,attachment,restSleep,randomIdle,autonomousMovement,physics);
    }
}
