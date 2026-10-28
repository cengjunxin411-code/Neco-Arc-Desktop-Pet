using System;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>B.1 compatibility facade; preserves its Flick-only and explicit flat-root contracts.</summary>
public sealed class PilotFlickVoiceService : IDisposable
{
    private readonly ActionVoiceService _service;
    public PilotFlickVoiceService(PilotVoicePlayer player, Func<int, int>? selector = null, string? explicitRoot = null)
        : this(player, null, selector, explicitRoot) { }
    internal PilotFlickVoiceService(PilotVoicePlayer player, PilotLog? log,
        Func<int, int>? selector = null, string? explicitRoot = null)
    {
        _service = new ActionVoiceService(player, log, selector,
            explicitRoot ?? System.IO.Path.Combine(ActionVoiceCatalog.DevelopmentRoot, "flick"), flatFlickRoot: true);
    }
    public PilotVoicePlayer Player => _service.Player;
    public bool TryStart(long actionId, string resourcePath) =>
        resourcePath == "NecoPetMaterials/click/flick.gif" && _service.TryStart(actionId, resourcePath);
    public void End(long actionId, bool completed) => _service.End(actionId, completed);
    public void Dispose() => _service.Dispose();
}
