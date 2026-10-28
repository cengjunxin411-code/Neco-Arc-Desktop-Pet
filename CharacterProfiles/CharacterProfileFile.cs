using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NecoArcDesktopPet.CharacterProfiles;

// A small Windows-only admission lease, not a general file-system abstraction.
// Keep every ancestor open without write/delete sharing until the file read ends.
internal sealed class CharacterProfileFile : IDisposable
{
    private readonly List<SafeFileHandle> directories = new();
    private FileStream? stream;
    internal FileStream Stream => stream ?? throw new ObjectDisposedException(nameof(CharacterProfileFile));
    private const uint ReadAttributes = 0x80, GenericRead = 0x80000000;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint VolumeNameGuid = 1;

    internal static CharacterProfileFile Open(string full)
    {
        if (!OperatingSystem.IsWindows() || full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new IOException("A local Windows drive path is required.");
        // Resolve ONLY the drive root through the local volume manager. Never open a
        // user-specified DOS/device/UNC path, and never resolve a child mount point.
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(full[..3], volume, (uint)volume.Capacity)) ThrowLastError();
        var anchor = volume.ToString();
        if (!anchor.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) || !anchor.EndsWith(@"}\", StringComparison.Ordinal) ||
            !Guid.TryParse(anchor.AsSpan(11, anchor.Length - 13), out _))
            throw new IOException("Cannot establish a local volume identity.");

        var lease = new CharacterProfileFile();
        try
        {
            var current = anchor;
            lease.OpenDirectory(current);
            var parts = full[3..].Split('\\');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'))
                    throw new IOException("Ambiguous path component.");
                current += part;
                if (i < parts.Length - 1)
                {
                    lease.OpenDirectory(current);
                    current += '\\';
                    continue;
                }
                var file = OpenHandle(current, GenericRead);
                try
                {
                    Validate(file, current, directory: false);
                    // FileStream consumes this exact validated handle; no path reopen.
                    lease.stream = new FileStream(file, FileAccess.Read);
                }
                catch { file.Dispose(); throw; }
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void OpenDirectory(string path)
    {
        var handle = OpenHandle(path, ReadAttributes);
        try { Validate(handle, path, directory: true); directories.Add(handle); }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenHandle(string path, uint access)
    {
        // OPEN_REPARSE_POINT acts on the last component. All earlier components
        // have already been validated and pinned, root first, by live handles.
        var handle = CreateFile(path, access, (uint)FileShare.Read, IntPtr.Zero, 3,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowError(error);
        throw new IOException("Cannot open Profile path.");
    }

    private static void Validate(SafeFileHandle handle, string expected, bool directory)
    {
        if (GetFileType(handle) != 1) throw new IOException("Profile path is not a disk object.");
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        var attributes = (FileAttributes)info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Profile path contains a reparse point or wrong object type.");
        var name = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, VolumeNameGuid);
        if (length == 0) ThrowLastError();
        if (length >= name.Capacity)
        {
            if (length > 32768) throw new IOException("Profile final path is too long.");
            name = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, VolumeNameGuid);
            if (length == 0) ThrowLastError();
        }
        if (length >= name.Capacity || !string.Equals(name.ToString(), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Profile final object identity cannot be verified.");
    }

    private static void ThrowLastError() => ThrowError(Marshal.GetLastWin32Error());
    private static void ThrowError(int error)
    {
        if (error == 2) throw new FileNotFoundException();
        if (error == 3) throw new DirectoryNotFoundException();
        if (error == 5) throw new UnauthorizedAccessException();
        throw new IOException("Profile path admission failed.", new Win32Exception(error));
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
        for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
        directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
}
