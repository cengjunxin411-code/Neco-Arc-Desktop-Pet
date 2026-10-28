using System;
using System.IO;

namespace NecoArcDesktopPet.Settings;

// Candidate builds never share the full local version's settings directory.
internal static class SourceOnlyPaths
{
    internal static string? SettingsFile(string fileName)
    {
        var root = Environment.GetEnvironmentVariable("NECO_SOURCE_ONLY_SETTINGS_DIR");
        if (root != null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
                root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return null;
            return Path.Combine(Path.GetFullPath(root), fileName);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.IsPathFullyQualified(local)
            ? Path.Combine(local, "NecoArcDesktopPet.SourceOnly", fileName) : null;
    }
}
