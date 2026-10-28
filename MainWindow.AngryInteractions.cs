using System;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private AngrySession? _angrySession;
    private bool _caperAvailable;
    private bool _angry1Available;

    private void BeginCaper()
    {
        if (_restSession.IsResting || HasSleepOwnership) return;
        StopAutonomousMovement();
        CancelClickInteraction();
        ResetIdleWithDiagnostics();
        _ambientGifActive = false;
        _bubbleWindow?.Hide();
        if (!_caperAvailable || _gifPlayer?.TrySwitchToLoops(AngrySession.CaperPath, _settings.Interaction.CaperLoops) != true)
        {
            RestoreIdleGif();
            return;
        }
        _angrySession = new AngrySession(_gifPlayer.PlaybackGeneration);
        _gifPlayer.Resume();
        _randomIdleScheduler.Update();
    }

    private void CompleteAngryPlayback(object? sender, EventArgs e)
    {
        var session = _angrySession;
        if (session == null || !ReferenceEquals(sender, _gifPlayer) || _gifPlayer?.IsComplete != true ||
            e is not GifPlaybackCompletedEventArgs completed || completed.Generation != session.PlaybackGeneration ||
            completed.Generation != _gifPlayer.PlaybackGeneration ||
            completed.ResourcePath != (session.Stage == AngryStage.Caper ? AngrySession.CaperPath : AngrySession.Angry1Path))
            return;

        if (session.Stage == AngryStage.Caper && session.EscalateToAngry1 && _angry1Available &&
            _gifPlayer.TrySwitchToLoops(AngrySession.Angry1Path, 1))
        {
            session.BeginAngry1(_gifPlayer.PlaybackGeneration);
            if (_leftPressed) _suppressClickOnRelease = true;
            CancelClickBubble();
            ResetIdleWithDiagnostics();
            _gifPlayer.Resume();
            _randomIdleScheduler.Update();
            return;
        }
        RestoreIdleGif();
    }

    private void CancelAngrySequence()
    {
        // A held gesture that outlives the sequence must not become a fresh Idle click.
        if (_angrySession != null && _leftPressed) _suppressClickOnRelease = true;
        _angrySession = null;
    }
}
