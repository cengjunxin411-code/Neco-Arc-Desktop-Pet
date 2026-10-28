using NecoArcDesktopPet.Pilot.Audio;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private PilotStartupVoiceService? _pilotStartupVoice;
    private void PilotStartupVoiceInit()
    {
        if (_pilotStartupVoice == null)
        {
            var player = new PilotVoicePlayer(Dispatcher, null, _pilotLog);
            _pilotStartupVoice = new PilotStartupVoiceService(player, _pilotLog);
        }
        _pilotActionVoice ??= new ActionVoiceService(_pilotStartupVoice.Player, _pilotLog);
        // Also binds the injected service used by controlled window integration tests.
        _pilotStartupVoice.EnabledProvider = () => _settings.Voice.Enabled;
        _pilotStartupVoice.VolumeProvider = () => _settings.Voice.ToLinearVolume();
        _pilotActionVoice.EnabledProvider = () => _settings.Voice.Enabled;
        _pilotActionVoice.VolumeProvider = () => _settings.Voice.ToLinearVolume();
    }

    private void PilotStartupVoiceTrigger()
    {
        if (_pilotStartupVoice == null)
        {
            PilotStartupVoiceInit();
        }
        _pilotStartupVoice?.TryTrigger();
    }

    private void PilotStartupVoiceStop()
    {
        PilotActionVoiceSilence();
        _pilotStartupVoice?.Stop();
    }

    private void PilotStartupVoiceClose()
    {
        PilotActionVoiceSilence();
        _pilotActionVoice?.Dispose();
        _pilotActionVoice = null;
        _pilotStartupVoice?.Dispose();
        _pilotStartupVoice = null;
    }

    internal PilotStartupVoiceService? PilotStartupVoiceServiceInstance => _pilotStartupVoice;

    internal void SetPilotStartupVoiceServiceForTesting(PilotStartupVoiceService service)
    {
        PilotActionVoiceSilence();
        _pilotActionVoice?.Dispose();
        _pilotActionVoice = null;
        _pilotStartupVoice?.Dispose();
        _pilotStartupVoice = service;
        PilotStartupVoiceInit();
    }
}
