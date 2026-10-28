using System;
using System.IO;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Product;

internal static class ProductDiagnostics
{
    internal static PilotLog FromEnvironment()
    {
        try { return CreateLog(
            Environment.GetEnvironmentVariable("NECO_PRODUCT_DIAGNOSTICS") == "1",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); }
        catch (Exception) { return PilotLog.CreateOptional(null); }
    }

    internal static PilotLog CreateLog(bool enabled, string localAppData)
    {
        if (!enabled) return PilotLog.CreateOptional(null);
        try { return PilotLog.CreateOptional(NewPath(localAppData, "product", ".jsonl")); }
        catch (Exception) { return PilotLog.CreateOptional(null); }
    }

    internal static string NewDiagnosticPath(string name) => NewPath(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name, ".log");

    private static string NewPath(string localAppData, string name, string extension)
    {
        if (!Path.IsPathFullyQualified(localAppData)) throw new IOException("LocalAppData unavailable.");
        var directory = Path.Combine(localAppData, "NecoArcDesktopPet.SourceOnly", "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}{extension}");
    }

    internal static SettingsPersistenceDiagnostics? PersistenceFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("NECO_SETTINGS_PERSISTENCE_DIAGNOSTICS") != "1") return null;
        try { return new SettingsPersistenceDiagnostics(NewDiagnosticPath("settings")); }
        catch (Exception) { return null; }
    }
}
