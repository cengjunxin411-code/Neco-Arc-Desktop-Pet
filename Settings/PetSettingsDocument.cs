namespace NecoArcDesktopPet.Settings;

// Only configuration snapshots cross the JSON boundary; no window/session state.
public sealed record PetSettingsDocument(int SchemaVersion, PetSettings PetSettings, bool Topmost)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? PetHeightDip { get; init; }
#if PMV2_PILOT
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PhysicsMode { get; init; }
#endif
}
public sealed record PetSettingsLoadResult(PetSettings Settings, bool Topmost, string? Message)
{
    public double? PetHeightDip { get; init; }
#if PMV2_PILOT
    internal Pilot.Physics.PhysicsMode PhysicsMode { get; init; }
#endif
}
public sealed record PetSettingsFailure(string Stage, string ExceptionType, int HResult, string Details)
{
    internal static PetSettingsFailure Capture(string stage, System.Exception error) =>
        new(stage, error.GetType().FullName ?? error.GetType().Name, error.HResult, error.ToString());
}
public sealed record PetSettingsSaveResult(bool Success, string Message)
{
    public PetSettingsFailure? Failure { get; init; }
    public string? DiagnosticLogPath { get; init; }
    public string? DiagnosticReport { get; init; }
}
