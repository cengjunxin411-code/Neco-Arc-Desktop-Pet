using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace NecoArcDesktopPet.Pilot;

internal static class PilotEntry
{
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            var options=PilotOptions.Parse(args);
            using var log=new PilotLog(options.Valid ? options.LogPath : null);
            string Hash(string path) { using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)); }
            var host=Environment.ProcessPath!;
            var dll=typeof(PilotEntry).Assembly.Location;
            log.Write("build-identity",new{host,hostSha256=Hash(host),dll,dllSha256=Hash(dll),pid=Environment.ProcessId});
            var expected=Path.Combine(AppContext.BaseDirectory,"NecoArcDesktopPet.PMv2Pilot.exe");
            if(!string.Equals(Path.GetFullPath(host),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase) ||
                !PilotNative.ProcessV2 || !PilotNative.ThreadV2)
            { log.Write("entry-refused",new{reason="Actual isolated apphost/process/thread PMv2 required before WPF",processV2=PilotNative.ProcessV2,threadV2=PilotNative.ThreadV2},"BLOCKED");return 2; }
            if(!options.Valid) {log.Write("entry-refused",new{reason=options.Error},"BLOCKED");return 3;}
            log.Write("before-wpf",new{processV2=PilotNative.ProcessV2,threadV2=PilotNative.ThreadV2,
                mode=options.CrossScreen ? "EXPERIMENTAL cross-screen Candidate" : "4A single-monitor",
                seamHandoff=options.SeamHandoff,rocket=options.Rocket,attachment=options.Attachment,restSleep=options.RestSleep,randomIdle=options.RandomIdle,autonomousMovement=options.AutonomousMovement,
                scope="real MainWindow/GIF; natural DIP size; independent PMv2 settings"});
            return Launch(log,options);
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    // WPF/JIT type-loading boundary mirrors the verified probe's early native gate.
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Launch(PilotLog log,PilotOptions options) => PilotApplication.Run(log,options);
}
