using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NecoArcDesktopPet.Settings;

// Synchronous small-file operations are serialized; UI callers publish a live snapshot first.
public sealed class PetSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private bool _versionProtected;
    private readonly SettingsPersistenceDiagnostics? _diagnostics;
    public string? FilePath { get; }
    private bool IsPilotStore => FilePath != null &&
        string.Equals(Path.GetFileName(FilePath), "pmv2-settings.json", StringComparison.OrdinalIgnoreCase);

    public PetSettingsStore() : this(DefaultPath(), SettingsPersistenceDiagnostics.FromEnvironment()) { }

    private static string? DefaultPath()
    {
        return SourceOnlyPaths.SettingsFile("settings.json");
    }

    // Explicit paths are used by isolated consumers, including the PMv2 Pilot.
    public PetSettingsStore(string? filePath, SettingsPersistenceDiagnostics? diagnostics = null)
    {
        FilePath = filePath == null ? null : Path.GetFullPath(filePath);
        _diagnostics = diagnostics;
    }

    public PetSettingsLoadResult Load() => Load(PetSettingsDefaults.CreateForStartup());

    internal PetSettingsLoadResult Load(PetSettings startupBaseline)
    {
        lock (_gate)
        {
            var baseline = startupBaseline.DeepClone();
            if (FilePath == null) return new(baseline, true, "配置目录无法定位；已使用启动默认值，无法保存。请检查当前用户 LocalAppData。");
            try { return Parse(File.ReadAllText(FilePath), baseline, IsPilotStore); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { return new(baseline, true, null); }
            catch (UnsupportedVersionException)
            {
                _versionProtected = true;
                return new(baseline, true, "配置版本缺失或不受支持；已使用启动默认值。原文件保留，禁止覆盖；请先移走或修正文件并重启。");
            }
            catch (Exception e) when (IsBoundaryError(e))
            {
                // Keep the original untouched. An explicit Apply may replace it only after archiving.
                return new(baseline, true, "配置无法加载；已使用启动默认值，原文件保留。打开工作台应用可重试。原因：" + e.Message);
            }
        }
    }

    public PetSettingsSaveResult Save(PetSettings settings, bool topmost) => Save(settings, topmost, null);

    internal PetSettingsSaveResult Save(PetSettings settings, bool topmost, double? petHeightDip)
#if PMV2_PILOT
        => Save(settings, topmost, petHeightDip, null);

    internal PetSettingsSaveResult Save(PetSettings settings, bool topmost, double? petHeightDip, Pilot.Physics.PhysicsMode? physicsMode)
#endif
    {
        lock (_gate)
        {
            var result = SaveCore(settings, topmost, petHeightDip
#if PMV2_PILOT
                , physicsMode
#endif
            );
            _diagnostics?.WriteReport();
            return result with
            {
                DiagnosticLogPath = _diagnostics?.LogPath,
                DiagnosticReport = _diagnostics != null && !_diagnostics.LastWriteSucceeded ? _diagnostics.Report : null
            };
        }
    }

    private PetSettingsSaveResult SaveCore(PetSettings settings, bool topmost, double? petHeightDip
#if PMV2_PILOT
        , Pilot.Physics.PhysicsMode? physicsMode
#endif
    )
    {
        string? temporary = null;
        FileStream? stream = null;
        var ownsTemporary = false;
        var stage = "ResolveDirectory";
        void Enter(string next, string? path = null)
        { stage = next; _diagnostics?.Record(next, path ?? FilePath, "Begin"); }
        try
        {
            Enter("ResolveDirectory");
            if (FilePath == null) return Failure("无法定位当前用户 LocalAppData 配置目录。");
            if (_versionProtected) return Failure("配置版本不兼容；请先移走或修正文件并重启，原文件不会被覆盖。");
            Enter("ValidateSnapshot");
            var validation = PetSettingsValidator.Validate(settings);
            if (!validation.IsValid) return Failure(string.Join("; ", validation.Errors));
            if (petHeightDip != null && !IsPilotStore) return Failure("尺寸只允许写入 PMv2 独立设置。");
            if (petHeightDip is {} size && (!double.IsFinite(size) || size < 140 || size > 560))
                return Failure("PetHeightDip 必须是 140–560 范围内的有限 WPF DIP 高度。");
#if PMV2_PILOT
            if (physicsMode != null && (!IsPilotStore || !Enum.IsDefined(physicsMode.Value)))
                return Failure("PhysicsMode 只允许在 PMv2 独立设置中保存 Off / Gravity / PlanarInertia。");
#endif
            var snapshot = settings.DeepClone();
            var directory = Path.GetDirectoryName(FilePath)!;
            Enter("EnsureDirectory", directory);
            Directory.CreateDirectory(directory);
            Enter("InspectExisting");
            var exists = File.Exists(FilePath);
            var previousValid = false;
            if (exists)
            {
                // Recheck before every publication, including a file edited while the app is running.
                Enter("ReadExisting");
                var previous = File.ReadAllText(FilePath);
                Enter("ValidateExisting");
                try {
                    var parsed = Parse(previous, PetSettingsDefaults.CreateForStartup(), IsPilotStore);
#if PMV2_PILOT
                    // Topmost/Workbench/size saves must retain an existing Pilot preference.
                    if (IsPilotStore) physicsMode ??= parsed.PhysicsMode;
#endif
                    previousValid = true;
                }
                catch (UnsupportedVersionException)
                {
                    _versionProtected = true;
                    return Failure("配置版本不兼容；原文件保留，请处理后重启。");
                }
                catch (Exception e) when (e is JsonException or ArgumentException)
                {
                    // Copy must succeed before replacement. Never rotate invalid data over a valid .bak.
                    var archive = Path.Combine(directory, Path.GetFileNameWithoutExtension(FilePath) + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")
                        + "-" + Guid.NewGuid().ToString("N") + ".json");
                    Enter("ArchiveCorrupt", archive);
                    File.Copy(FilePath, archive, overwrite: false);
                }
            }
            temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            Enter("OpenTempFile", temporary);
            _diagnostics?.Record("FileStreamOptions", temporary, "CreateNew/Write/None; buffer=4096; FileOptions.None");
            stream = OpenTemporary(temporary);
            ownsTemporary = true;
            Enter("Serialize", temporary);
            JsonSerializer.Serialize(stream, new PetSettingsDocument(CurrentSchemaVersion, snapshot, topmost)
                { PetHeightDip = petHeightDip
#if PMV2_PILOT
                    , PhysicsMode = IsPilotStore ? (physicsMode ?? Pilot.Physics.PhysicsMode.Off).ToString() : null
#endif
                }, JsonOptions);
            Enter("Flush", temporary);
            stream.Flush(flushToDisk: true);
            Enter("Dispose", temporary);
            var closing = stream;
            stream = null;
            closing.Dispose();
            Enter(exists ? "PublishReplace" : "PublishMove");
            if (exists) File.Replace(temporary, FilePath, previousValid ? FilePath + ".bak" : null);
            else File.Move(temporary, FilePath);
            _diagnostics?.Record(stage, FilePath, "Success");
            return new(true, "已应用并已保存。");
        }
        catch (Exception e) when (IsBoundaryError(e))
        {
            var evidence = PetSettingsFailure.Capture(stage, e);
            _diagnostics?.RecordFailure(evidence);
            return Failure($"阶段 {stage}；{evidence.ExceptionType}；HResult=0x{e.HResult:X8}；{e.Message}")
                with { Failure = evidence };
        }
        finally
        {
            // Dispose after a write/flush failure without replacing the original exception evidence.
            if (stream != null)
            {
                try { stream.Dispose(); }
                catch (Exception e) when (IsBoundaryError(e))
                { _diagnostics?.RecordFailure(PetSettingsFailure.Capture("CleanupDispose", e)); }
            }
            if (ownsTemporary && temporary != null)
            {
                try { File.Delete(temporary); _diagnostics?.Record("Cleanup", temporary, "Success"); }
                catch (Exception e) when (IsBoundaryError(e))
                { _diagnostics?.RecordFailure(PetSettingsFailure.Capture("Cleanup", e)); }
            }
        }
    }

    // Four-argument FileStream defaults made explicit; shared by the opt-in comparison probe.
    internal static FileStream OpenTemporary(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);

    private static PetSettingsSaveResult Failure(string reason) => new(false,
        "设置已用于本次运行，但保存失败；重启后可能恢复旧值。" + reason);
    private static bool IsBoundaryError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private sealed class UnsupportedVersionException : Exception { }

    private static PetSettingsLoadResult Parse(string text, PetSettings baseline, bool pilotStore)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("配置根节点必须是对象。");
        var versions = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "SchemaVersion") continue;
            versions++;
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number)
                || number != CurrentSchemaVersion) throw new UnsupportedVersionException();
        }
        if (versions != 1) throw new UnsupportedVersionException();
        var known = new HashSet<string> { "PetSettings", "Topmost" };
        if (pilotStore) known.Add("PetHeightDip");
#if PMV2_PILOT
        if (pilotStore) known.Add("PhysicsMode");
#endif
        RejectKnownDuplicates(root, known);
        var merged = JsonSerializer.SerializeToNode(baseline)!.AsObject();
        if (root.TryGetProperty("PetSettings", out var settings)) Overlay(merged, settings, "PetSettings");
        var topmost = true;
        if (root.TryGetProperty("Topmost", out var top))
        {
            if (top.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Topmost 必须是布尔值。");
            topmost = top.GetBoolean();
        }
        double? petHeightDip = null;
        if (pilotStore && root.TryGetProperty("PetHeightDip", out var height))
        {
            if (height.ValueKind != JsonValueKind.Number || !height.TryGetDouble(out var value) ||
                !double.IsFinite(value) || value < 140 || value > 560)
                throw new JsonException("PetHeightDip 必须是 140–560 范围内的有限 WPF DIP 高度。");
            petHeightDip = value;
        }
        var candidate = merged.Deserialize<PetSettings>() ?? throw new JsonException("缺少配置对象。");
        var validation = PetSettingsValidator.Validate(candidate);
        if (!validation.IsValid) throw new JsonException(string.Join("; ", validation.Errors));
#if PMV2_PILOT
        var physicsMode = Pilot.Physics.PhysicsMode.Off;
        if (pilotStore && root.TryGetProperty("PhysicsMode", out var mode))
        {
            if (mode.ValueKind != JsonValueKind.String) throw new JsonException("PhysicsMode 必须是模式名称字符串。");
            physicsMode = mode.GetString() switch {
                "Off" => Pilot.Physics.PhysicsMode.Off,
                "Gravity" => Pilot.Physics.PhysicsMode.Gravity,
                "PlanarInertia" => Pilot.Physics.PhysicsMode.PlanarInertia,
                _ => throw new JsonException("PhysicsMode 只允许 Off / Gravity / PlanarInertia。")
            };
        }
#endif
        return new PetSettingsLoadResult(candidate, topmost, null) { PetHeightDip = petHeightDip
#if PMV2_PILOT
            , PhysicsMode = physicsMode
#endif
        };
    }

    // Only known properties are overlaid; STJ checks scalar types after merging.
    // Explicit false/zero survives, while absent fields keep the startup baseline.
    private static void Overlay(JsonObject baseline, JsonElement input, string path)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new JsonException(path + " 必须是对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            var name = path == "PetSettings.Diagnostics" ? property.Name switch
            {
                "Enabled" => "MultiClickDiagnosticsEnabled",
                "Path" => "MultiClickDiagnosticsPath",
                _ => property.Name
            } : property.Name;
            if (!baseline.TryGetPropertyValue(name, out var target)) continue;
            if (!seen.Add(name)) throw new JsonException(path + "." + name + " 重复定义。");
            if (target is JsonObject child) Overlay(child, property.Value, path + "." + name);
            else baseline[name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static void RejectKnownDuplicates(JsonElement input, HashSet<string> known)
    {
        var seen = new HashSet<string>();
        foreach (var property in input.EnumerateObject())
            if (known.Contains(property.Name) && !seen.Add(property.Name)) throw new JsonException(property.Name + " 重复定义。");
    }
}
