namespace NecoArcDesktopPet.Settings;

public sealed record DiagnosticsSettings
{
    public bool MultiClickDiagnosticsEnabled { get; set; }
    public string? MultiClickDiagnosticsPath { get; set; }
}
