using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NecoArcDesktopPet.Settings;

// Explicit opt-in only. No settings values, environment dump, or production fallback path.
public sealed class SettingsPersistenceDiagnostics : IDisposable
{
    public const string Switch = "--diagnose-settings-persistence";
    private readonly StringBuilder _report = new();
    private readonly string? _preferredLogPath;
    private FileStream? _logStream;
    public string? LogPath { get; private set; }
    public bool LastWriteSucceeded { get; private set; }
    public string Report => _report.ToString();

    public SettingsPersistenceDiagnostics(string? preferredLogPath = null)
    {
        _preferredLogPath = preferredLogPath;
        _report.AppendLine("Neco-Arc persistence diagnostic; no configuration values recorded.");
        _report.AppendLine("Launch origin: cannot determine human vs automation from this report alone; retain the actual launcher observation.");
        Context("ProcessId", () => Environment.ProcessId.ToString());
        Context("Executable", () => Environment.ProcessPath ?? "无法确定");
        Context("EntryAssembly", () => Assembly.GetEntryAssembly()?.Location ?? "无法确定");
        Context("CurrentDirectory", () => Environment.CurrentDirectory);
        Context("LocalApplicationData", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Context("Identity", () => { using var identity = WindowsIdentity.GetCurrent(); return identity.Name; });
        Context("Elevated", () => TokenFlag(20));
        Context("TokenHasRestrictions", () => TokenFlag(21));
        Context("RestrictingSidCount", RestrictingSidCount);
        Context("AppContainer", () => TokenFlag(29));
        Context("ProcessAncestry", ProcessAncestry);
    }

    public static SettingsPersistenceDiagnostics? FromEnvironment() =>
#if PMV2_PRODUCT
        Product.ProductDiagnostics.PersistenceFromEnvironment();
#else
        Environment.GetEnvironmentVariable("NECO_SETTINGS_PERSISTENCE_DIAGNOSTICS") == "1"
            ? new(Environment.GetEnvironmentVariable("NECO_SETTINGS_PERSISTENCE_LOG")) : null;
#endif

    private void Context(string name, Func<string> read)
    {
        try { _report.AppendLine(name + "=" + read()); }
        catch (Exception e) { _report.AppendLine(name + "=无法确定 (" + e.GetType().Name + ": " + e.Message + ")"); }
    }

    internal void Record(string stage, string? path, string outcome)
    {
        _report.AppendLine($"{DateTime.UtcNow:O} Stage={stage} Outcome={outcome}");
        if (path == null) return;
        Context("Path", () => Path.GetFullPath(path));
        Inspect(path);
        var parent = Path.GetDirectoryName(path);
        if (parent != null) Inspect(parent);
    }

    private void Inspect(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            _report.AppendLine($"PathState={path}; Type={((attributes & FileAttributes.Directory) != 0 ? "Directory" : "File")}; Attributes={attributes}");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { _report.AppendLine("PathState=" + path + "; Missing"); }
        catch (Exception e)
        { _report.AppendLine("PathState=" + path + "; 无法确定; " + e.GetType().FullName + $"; HResult=0x{e.HResult:X8}"); }
    }

    internal void RecordFailure(PetSettingsFailure failure)
    {
        _report.AppendLine($"Failure Stage={failure.Stage}; Type={failure.ExceptionType}; HResult=0x{failure.HResult:X8}");
        _report.AppendLine(failure.Details); // Full original stack and InnerException chain.
    }

    private bool Attempt(string stage, string? path, Action operation)
    {
        Record(stage, path, "Begin");
        try { operation(); Record(stage, path, "Success"); return true; }
        catch (Exception e) { RecordFailure(PetSettingsFailure.Capture(stage, e)); return false; }
    }

    // Never read configuration contents. Only independently owned probe files may be deleted.
    public void ProbeDirectory(string directory)
    {
        _report.AppendLine("Probe: File.Open baseline=CreateNew/Write/None; Store=FileStream CreateNew/Write/None, buffer=4096, FileOptions.None.");
        if (!Attempt("Probe.EnsureDirectory", directory, () => Directory.CreateDirectory(directory))) return;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = Path.Combine(directory, ".neco-write-probe-" + Guid.NewGuid().ToString("N"));
        bool Write(string path, string label, Func<string, FileStream> open)
        {
            FileStream? stream = null;
            try
            {
                if (!Attempt(label + ".OpenTempFile", path, () => { stream = open(path); owned.Add(path); })) return false;
                var bytes = Encoding.UTF8.GetBytes("Neco-Arc non-sensitive persistence probe\n");
                if (!Attempt(label + ".Write", path, () => stream!.Write(bytes))) return false;
                if (!Attempt(label + ".Flush", path, () => stream!.Flush(flushToDisk: true))) return false;
                var closing = stream!;
                stream = null;
                return Attempt(label + ".Dispose", path, closing.Dispose);
            }
            finally
            {
                if (stream != null) Attempt(label + ".Dispose", path, stream.Dispose);
            }
        }
        try
        {
            Write(prefix + ".baseline.tmp", "Baseline", p => File.Open(p, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            var temporary = prefix + ".tmp";
            var final = prefix + ".final";
            var replacement = prefix + ".replace.tmp";
            var backup = prefix + ".bak";
            if (!Write(temporary, "StoreOptions", PetSettingsStore.OpenTemporary)) return;
            if (!Attempt("Probe.PublishMove", final, () => File.Move(temporary, final))) return;
            owned.Remove(temporary); owned.Add(final);
            if (!Write(replacement, "StoreOptionsReplace", PetSettingsStore.OpenTemporary)) return;
            // Destination and source were created above; backup must not preexist.
            if (File.Exists(backup) || Directory.Exists(backup))
            { Record("Probe.PublishReplace", backup, "Skipped: unowned backup path exists"); return; }
            // Own the backup before Replace, including Windows failures that already renamed the old file.
            if (!Write(backup, "ProbeReserveBackup", PetSettingsStore.OpenTemporary)) return;
            if (Attempt("Probe.PublishReplace", final, () => File.Replace(replacement, final, backup)))
                owned.Remove(replacement);
        }
        finally
        {
            foreach (var path in owned) Attempt("Probe.Cleanup", path, () => File.Delete(path));
        }
    }

    public bool IsolatedRoundTrip(string directory)
    {
        // Dedicated new directory only, never the default settings directory or caller's existing data.
        if (Directory.Exists(directory) || File.Exists(directory))
        { Record("Isolated.Start", directory, "Skipped: directory already exists"); return false; }
        var path = Path.Combine(directory, "settings.json");
        _report.AppendLine("Isolated Store round trip; this path is not a production fallback.");
        var store = new PetSettingsStore(path, this);
        var settings = PetSettingsDefaults.Create();
        settings.Rest.EnterAfter = TimeSpan.FromSeconds(15);
        var first = store.Save(settings, false);
        Record("Isolated.FirstSave", path, first.Success ? "Success" : "Failure");
        if (!first.Success) return false;
        settings.Rest.EnterAfter = TimeSpan.FromSeconds(25);
        var second = store.Save(settings, true);
        Record("Isolated.SecondSave", path, second.Success ? "Success" : "Failure");
        if (!second.Success) return false;
        var loaded = new PetSettingsStore(path).Load();
        var restored = loaded.Message == null && loaded.Topmost && loaded.Settings.Rest.EnterAfter == TimeSpan.FromSeconds(25);
        Record("Isolated.Reload", path, restored ? "Success" : "Failure");
        var prior = new PetSettingsStore(path + ".bak").Load();
        var backedUp = prior.Message == null && !prior.Topmost && prior.Settings.Rest.EnterAfter == TimeSpan.FromSeconds(15);
        Record("Isolated.Backup", path + ".bak", backedUp ? "Success" : "Failure");
        // Retain dedicated fixture for inspection; it is never the real user's settings.
        return restored && backedUp;
    }

    public static SettingsPersistenceDiagnostics RunOneShot()
    {
        var diagnostic = new SettingsPersistenceDiagnostics(Environment.GetEnvironmentVariable("NECO_SETTINGS_PERSISTENCE_LOG"));
        var path = new PetSettingsStore().FilePath; // Path resolution only; no Load/Save of real configuration.
        diagnostic.Record("ResolveDirectory", path, path == null ? "Failure" : "Success");
        if (path != null) diagnostic.ProbeDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var isolated = Path.Combine(Path.GetTempPath(), "NecoArcExePersistence-" + Guid.NewGuid().ToString("N"));
            diagnostic.Record("Isolated.RoundTrip", isolated, diagnostic.IsolatedRoundTrip(isolated) ? "Success" : "Failure");
        }
        catch (Exception e) { diagnostic.RecordFailure(PetSettingsFailure.Capture("Isolated.Run", e)); }
        diagnostic.WriteReport();
        return diagnostic;
    }

    // Unique independent log; no overwrite of a preexisting supplied path. In-memory UI remains usable on failure.
    public void WriteReport()
    {
        LastWriteSucceeded = false;
        try
        {
            if (_logStream != null) { Write(); LastWriteSucceeded = true; return; }
            if (!string.IsNullOrWhiteSpace(_preferredLogPath))
            {
                try
                {
                    if (!Path.IsPathFullyQualified(_preferredLogPath)) throw new ArgumentException("诊断日志路径必须是绝对路径。");
                    if (!string.Equals(Path.GetExtension(_preferredLogPath), ".log", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("诊断日志必须使用独立的 .log 文件；禁止配置/备份/临时文件路径。");
                    if (_preferredLogPath[Path.GetPathRoot(_preferredLogPath)!.Length..].Contains(':'))
                        throw new ArgumentException("诊断日志禁止 NTFS alternate stream 路径。");
                    OpenLog(_preferredLogPath);
                    LastWriteSucceeded = true;
                    return;
                }
                catch (Exception e) { RecordFailure(PetSettingsFailure.Capture("Diagnostic.WritePreferredLog", e)); }
            }
#if !PMV2_PRODUCT
            var fallback = Path.Combine(Path.GetTempPath(), "NecoArcPersistenceDiagnostics-" + Guid.NewGuid().ToString("N") + ".log");
            OpenLog(fallback);
            LastWriteSucceeded = true;
#endif
        }
        catch (Exception e) { RecordFailure(PetSettingsFailure.Capture("Diagnostic.WriteLog", e)); }
    }

    private void OpenLog(string path)
    {
        // Retain this owned handle; never reopen a path that another process could replace.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { _logStream = stream; Write(); LogPath = path; }
        catch { _logStream = null; stream.Dispose(); throw; }
    }

    private void Write()
    {
        var bytes = Encoding.UTF8.GetBytes(Report);
        var stream = _logStream!;
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        try { _logStream?.Dispose(); }
        catch (Exception e) { RecordFailure(PetSettingsFailure.Capture("Diagnostic.DisposeLog", e)); }
        finally { _logStream = null; }
    }

    // Microsoft TOKEN_INFORMATION_CLASS: elevation=20, has restrictions=21, AppContainer=29.
    private static string TokenFlag(int informationClass)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, informationClass, out var value, sizeof(uint), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return value == 0 ? "False" : "True";
    }

    private static string RestrictingSidCount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        GetTokenInformationBuffer(identity.AccessToken, 11, IntPtr.Zero, 0, out var length);
        var error = Marshal.GetLastWin32Error();
        if (error != 122 || length < sizeof(uint)) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformationBuffer(identity.AccessToken, 11, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.ReadInt32(buffer).ToString(); // Count only; never log token or SID data.
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ProcessAncestry()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entries = new Dictionary<uint, (uint Parent, string Name)>();
        do { entries[entry.ProcessId] = (entry.ParentId, entry.Name); } while (Process32NextW(snapshot, ref entry));
        var parts = new List<string>();
        var seen = new HashSet<uint>();
        var pid = (uint)Environment.ProcessId;
        while (parts.Count < 8 && seen.Add(pid) && entries.TryGetValue(pid, out var item))
        { parts.Add($"{item.Name}(PID={pid}, ParentPID={item.Parent})"); pid = item.Parent; }
        return string.Join(" <- ", parts) + "; snapshot only, parent PID may have exited/reused";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out uint information, int length, out int returnLength);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
