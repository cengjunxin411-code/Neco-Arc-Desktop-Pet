using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NecoArcDesktopPet.CharacterProfiles;

static class PathAdmissionCases
{
    public static void Run(string root, string json, Action<bool, string> check)
    {
        root = Path.Combine(root, "paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "profile.json");
        File.WriteAllText(file, json);
        check(CharacterProfileLoader.LoadFile(file).Success, "path ordinary A admits");
        foreach (var path in new[] { "relative.json", @"\\server\share\profile.json", file + ":stream", root,
            @"\\?\" + file, @"\\.\" + file, Path.Combine(root, "gone", "profile.json") })
            check(!CharacterProfileLoader.LoadFile(path).Success, "path invalid/UNC/ADS/device/directory/missing refuses");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(!CharacterProfileLoader.LoadFile(file).Success, "path exclusive file lock refuses");
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            check(!CharacterProfileLoader.LoadFile(file).Success, "path existing shared writer refuses");
        File.WriteAllBytes(file, new byte[CharacterProfileLoader.MaximumBytes + 1]);
        check(!CharacterProfileLoader.LoadFile(file).Success, "path oversized file refuses");
        File.WriteAllBytes(file, new byte[] { 0xff, 0xff });
        check(!CharacterProfileLoader.LoadFile(file).Success, "path malformed encoding refuses");
        File.Delete(file);
        check(!CharacterProfileLoader.LoadFile(file).Success, "path deleted file refuses");
        File.WriteAllText(file, json);
        using (var lease = CharacterProfileFile.Open(file))
        {
            foreach (var operation in new Action[] {
                () => Directory.Move(root, root + "-renamed"),
                () => File.Move(file, file + "-renamed"),
                () => File.Delete(file),
                () => { using var writer = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            })
            {
                var refused = false;
                try { operation(); } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; }
                check(refused, "path live admission lease blocks rename/delete/write");
            }
            using var reader = new StreamReader(lease.Stream, leaveOpen: true);
            check(reader.ReadToEnd() == json, "path admitted handle reads exact A bytes");
        }
        var info = new FileInfo(file);
        var originalAcl = info.GetAccessControl();
        var deniedAcl = info.GetAccessControl();
        deniedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(deniedAcl);
            check(!CharacterProfileLoader.LoadFile(file).Success, "path read permission denied refuses");
        }
        finally { info.SetAccessControl(originalAcl); }
        foreach (var directory in new[] { false, true })
        {
            var link = Path.Combine(root, directory ? "directory-symlink" : "file-symlink.json");
            if (CreateSymbolicLink(link, directory ? root : file, (directory ? 1u : 0u) | 2u))
                check(!CharacterProfileLoader.LoadFile(directory ? Path.Combine(link, "profile.json") : link).Success,
                    directory ? "path directory symlink refuses" : "path file symlink refuses");
            else Console.WriteLine($"NOT RUN {(directory ? "directory" : "file")} symlink: Win32={Marshal.GetLastWin32Error()}");
        }
        // Use a real junction (no developer-mode/symlink privilege requirement).
        Race(Path.Combine(root, "direct"), json, "", check);
        Race(Path.Combine(root, "ancestor"), json, Path.Combine("nested", "deeper"), check);
    }

    static void Race(string root, string json, string suffix, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var door = Path.Combine(root, "door");
        var park = Path.Combine(root, "safe-park");
        var link = Path.Combine(root, "junction-park");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(Path.Combine(door, suffix));
        Directory.CreateDirectory(Path.Combine(other, suffix));
        var a = json.Replace("synthetic-block", "ordinary-A");
        var b = json.Replace("synthetic-block", "junction-only");
        File.WriteAllText(Path.Combine(door, suffix, "profile.json"), a);
        File.WriteAllText(Path.Combine(other, suffix, "profile.json"), b);
        try { Junction(link, other); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine("NOT RUN junction race: " + e.Message); return; }
        var read = Path.Combine(door, suffix, "profile.json");
        check(!CharacterProfileLoader.LoadFile(Path.Combine(link, suffix, "profile.json")).Success, "path static junction refuses");
        check(CharacterProfileLoader.LoadFile(read).Profile?.Id == "ordinary-A", "path race A control admits");
        using var stop = new CancellationTokenSource();
        var swaps = 0;
        var task = Task.Run(() =>
        {
            var phase = 0;
            var timer = Stopwatch.StartNew();
            while ((!stop.IsCancellationRequested || phase != 0) && timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                try
                {
                    switch (phase)
                    {
                        case 0: Directory.Move(door, park); phase = 1; break;
                        case 1: Directory.Move(link, door); phase = 2; Thread.SpinWait(1000); break;
                        case 2: Directory.Move(door, link); phase = 3; break;
                        case 3: Directory.Move(park, door); phase = 0; Interlocked.Increment(ref swaps); break;
                    }
                }
                catch (IOException) { Thread.Yield(); }
                catch (UnauthorizedAccessException) { Thread.Yield(); }
            }
            if (phase != 0) throw new IOException("Race did not restore ordinary directory within its bound.");
        });
        int accepted = 0, refused = 0, redirected = 0, iterations = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            while (iterations++ < 20000 && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                var result = CharacterProfileLoader.LoadFile(read);
                if (!result.Success) refused++;
                else if (result.Profile!.Id != "ordinary-A") { redirected++; break; }
                else accepted++;
            }
        }
        finally
        {
            stop.Cancel();
            if (!task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Bounded path race worker did not stop.");
        }
        Console.WriteLine($"RACE depth={suffix} iterations={iterations} swaps={swaps} A={accepted} refused={refused} B={redirected}");
        check(swaps > 0 && redirected == 0, "path concurrent parent junction never admits B");
        check(CharacterProfileLoader.LoadFile(read).Profile?.Id == "ordinary-A", "path restored A still admits");
    }

    static void Junction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot open junction: " + Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(0xA0000003u); writer.Write((ushort)(buffer.Length - 8)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substitute.Length);
            writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)print.Length);
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
        }
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Cannot set junction: " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool CreateSymbolicLink(string path, string target, uint flags);
}
