using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Static metadata and asset validation rules for the four approved startup greeting audio files.
/// </summary>
public static class PilotStartupVoicePool
{
    /// <summary>
    /// Canonical filenames approved for the startup greeting pool.
    /// Exactly four files with 25% equal probability weighting.
    /// </summary>
    public static readonly string[] ApprovedVoices =
    {
        "nec00_2001.wav",
        "nec00_2125.wav",
        "nec00_2301.wav",
        "nec00_3056.wav"
    };

    /// <summary>
    /// Validates whether a given filename is one of the four approved voices.
    /// Strictly rejects paths, traversals, and unauthorized files.
    /// </summary>
    public static bool IsApprovedVoiceFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) return false;
        return ApprovedVoices.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the voice root directory following standard conventions.
    /// Rejects non-local / UNC paths.
    /// </summary>
    public static string? ResolveVoiceRootDirectory(string? explicitRoot = null)
    {
        // Source-only builds never discover voices in a parent checkout, CWD,
        // or the full local version's environment. An explicit caller root is local-only.
        var root = explicitRoot ?? Path.Combine(AppContext.BaseDirectory, "Voice");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return null;
        return Directory.Exists(root) ? Path.GetFullPath(root) : null;
    }

    /// <summary>
    /// Checks whether all four approved WAV files are present in the target directory.
    /// The complete 4-file pool is required for the 25% equal random probability guarantee.
    /// </summary>
    public static bool CheckAssetsAvailability(string? rootDir, out List<string> missingFiles)
    {
        missingFiles = new List<string>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            missingFiles.AddRange(ApprovedVoices);
            return false;
        }

        foreach (var voice in ApprovedVoices)
        {
            var fullPath = Path.Combine(rootDir, voice);
            if (!File.Exists(fullPath))
            {
                missingFiles.Add(voice);
            }
        }

        return missingFiles.Count == 0;
    }
}

/// <summary>
/// Service managing the one-shot random startup greeting voice playback lifecycle for PMv2 Pilot.
/// </summary>
public sealed class PilotStartupVoiceService : IDisposable
{
    private readonly PilotVoicePlayer _player;
    private readonly PilotLog? _log;
    private readonly Func<int, int> _selector;
    private readonly string? _explicitRoot;

    private int _triggerAttempted;
    private string? _chosenVoice;
    private VoicePlayResult? _lastResult;
    private bool _disposed;

    public PilotStartupVoiceService(
        PilotVoicePlayer player,
        Func<int, int>? selector = null,
        string? explicitRoot = null)
        : this(player, null, selector, explicitRoot)
    {
    }

    internal PilotStartupVoiceService(
        PilotVoicePlayer player,
        PilotLog? log,
        Func<int, int>? selector = null,
        string? explicitRoot = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log;
        _selector = selector ?? (count => Random.Shared.Next(count));
        _explicitRoot = explicitRoot;
    }

    public bool Attempted => Volatile.Read(ref _triggerAttempted) != 0;
    public string? ChosenVoice => _chosenVoice;
    public VoicePlayResult? LastResult => _lastResult;
    public PilotVoicePlayer Player => _player;
    public bool IsDisposed => _disposed;
    internal Func<bool>? EnabledProvider { get; set; }
    internal Func<double>? VolumeProvider { get; set; }

    /// <summary>
    /// Attempts to trigger the startup greeting voice once.
    /// Guarantees strictly one attempt per lifecycle; fails closed and remains silent if assets are missing.
    /// </summary>
    public bool TryTrigger()
    {
        if (Interlocked.Exchange(ref _triggerAttempted, 1) != 0)
        {
            return false;
        }

        if (_disposed)
        {
            return false;
        }

        if (EnabledProvider?.Invoke() == false)
        {
            try { _log?.Write("startup-voice-disabled", new { attempted = true }); } catch { }
            return false;
        }

        var root = PilotStartupVoicePool.ResolveVoiceRootDirectory(_explicitRoot);
        if (!PilotStartupVoicePool.CheckAssetsAvailability(root, out var missingFiles))
        {
            try
            {
                _log?.Write("startup-voice-assets-unavailable", new
                {
                    missing = missingFiles.ToArray(),
                    approvedCount = PilotStartupVoicePool.ApprovedVoices.Length,
                    availableCount = PilotStartupVoicePool.ApprovedVoices.Length - missingFiles.Count
                });
            }
            catch { }
            return false;
        }

        int index = _selector(PilotStartupVoicePool.ApprovedVoices.Length);
        if (index < 0 || index >= PilotStartupVoicePool.ApprovedVoices.Length)
        {
            index = 0;
        }

        string chosenFileName = PilotStartupVoicePool.ApprovedVoices[index];
        _chosenVoice = chosenFileName;
        string fullPath = Path.Combine(root!, chosenFileName);

        try
        {
            _log?.Write("startup-voice-selected", new
            {
                file = chosenFileName,
                index = index,
                total = PilotStartupVoicePool.ApprovedVoices.Length
            });
        }
        catch { }

        var result = _player.Play(fullPath, VolumeProvider?.Invoke() ?? PilotVoicePlayer.DefaultVolume);
        _lastResult = result;
        return result.Accepted;
    }

    public void Stop()
    {
        if (_disposed) return;
        _player.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Dispose();
    }
}
