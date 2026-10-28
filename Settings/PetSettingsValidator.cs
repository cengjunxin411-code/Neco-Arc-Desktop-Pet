using System;
using System.Collections.Generic;

namespace NecoArcDesktopPet.Settings;

public sealed record SettingsValidationError(string Path, string Message)
{
    public override string ToString() => Path + ": " + Message;
}
public sealed record SettingsValidationResult(IReadOnlyList<SettingsValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class PetSettingsValidator
{
    // DispatcherTimer stores its interval in signed integer milliseconds.
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(int.MaxValue);
    public static SettingsValidationResult Validate(PetSettings? settings)
    {
        var errors = new List<SettingsValidationError>();
        void Require(bool valid, string path, string message) { if (!valid) errors.Add(new(path, message)); }
        bool Present(object? value, string path) { Require(value != null, path, "Required section."); return value != null; }
        void Positive(TimeSpan value, string path) => Require(value > TimeSpan.Zero && value <= MaximumDuration,
            path, "Must be greater than zero and within the supported timer range (2147483647 ms).");
        void Speed(double value, string path) => Require(double.IsFinite(value) && value > 0, path, "Must be finite and greater than zero.");
        void Range(TimeSpan min, TimeSpan max, string path)
        {
            Positive(min, path + "MinDelay"); Positive(max, path + "MaxDelay");
            Require(max >= min, path + "MaxDelay", "Maximum cannot be less than minimum.");
        }
        void Loops(int value, string path) => Require(value >= 1, path, "Must be at least one.");
        long totalWeight = 0;
        void Profile(MovementProfileSettings? p, string path)
        {
            if (!Present(p, path)) return;
            Speed(p!.Speed, path + ".Speed");
            Require(double.IsFinite(p.MinDistance) && p.MinDistance >= 0, path + ".MinDistance", "Must be finite and nonnegative.");
            Require(double.IsFinite(p.MaxDistance) && p.MaxDistance >= p.MinDistance, path + ".MaxDistance", "Maximum must be finite and at least minimum.");
            Require(p.Weight >= 0, path + ".Weight", "Must be nonnegative."); totalWeight += p.Weight;
        }
        if (!Present(settings, "Settings")) return new(errors.AsReadOnly());
        var s = settings!;
        if (Present(s.Normal, "Normal")) Range(s.Normal.RandomIdleMinDelay, s.Normal.RandomIdleMaxDelay, "Normal.RandomIdle");
        if (Present(s.Rest, "Rest"))
        {
            Positive(s.Rest.EnterAfter, "Rest.EnterAfter"); Range(s.Rest.AmbientMinDelay, s.Rest.AmbientMaxDelay, "Rest.Ambient");
            Loops(s.Rest.AmbientMinLoops, "Rest.AmbientMinLoops"); Loops(s.Rest.AmbientMaxLoops, "Rest.AmbientMaxLoops");
            Require(s.Rest.AmbientMaxLoops >= s.Rest.AmbientMinLoops, "Rest.AmbientMaxLoops", "Maximum cannot be less than minimum.");
            Require(s.Rest.AmbientMaxLoops < int.MaxValue, "Rest.AmbientMaxLoops", "Maximum must leave room for an inclusive random bound.");
        }
        if (Present(s.Sleep, "Sleep"))
        {
            Positive(s.Sleep.EnterAfterRest, "Sleep.EnterAfterRest"); Range(s.Sleep.AmbientMinDelay, s.Sleep.AmbientMaxDelay, "Sleep.Ambient");
            Loops(s.Sleep.GrouchyLoops, "Sleep.GrouchyLoops");
        }
        if (Present(s.Movement, "Movement"))
        {
            Require(double.IsFinite(s.Movement.SelectionWeight) && s.Movement.SelectionWeight >= 0 && s.Movement.SelectionWeight <= 1,
                "Movement.SelectionWeight", "Must be a probability from zero to one.");
            Profile(s.Movement.Lope, "Movement.Lope"); Profile(s.Movement.TrotRelaxed, "Movement.TrotRelaxed");
            Profile(s.Movement.RunExcited, "Movement.RunExcited"); Profile(s.Movement.SprintPanic, "Movement.SprintPanic");
            Require(totalWeight <= int.MaxValue, "Movement.Profiles", "Total weight exceeds the random selection limit.");
        }
        if (Present(s.Interaction, "Interaction"))
        {
            var i = s.Interaction;
            Positive(i.FirstClickWindow, "Interaction.FirstClickWindow"); Positive(i.SecondClickWindow, "Interaction.SecondClickWindow");
            Speed(i.DragThresholdDip, "Interaction.DragThresholdDip"); Positive(i.RotationInitialDuration, "Interaction.RotationInitialDuration");
            Require(i.RotationExtensionPerClick >= TimeSpan.Zero && i.RotationExtensionPerClick <= MaximumDuration,
                "Interaction.RotationExtensionPerClick", "Must be nonnegative and within the supported duration range.");
            Require(i.RotationMaxDuration >= i.RotationInitialDuration && i.RotationMaxDuration <= MaximumDuration,
                "Interaction.RotationMaxDuration", "Maximum must be at least initial duration and within the supported duration range.");
            Speed(i.RocketVerticalSpeed, "Interaction.RocketVerticalSpeed"); Positive(i.RocketHiddenDuration, "Interaction.RocketHiddenDuration");
            Loops(i.CaperLoops, "Interaction.CaperLoops");
        }
        Present(s.Diagnostics, "Diagnostics");
        if (Present(s.Attachment, "Attachment"))
            Require(double.IsFinite(s.Attachment.SnapDistanceDip) &&
                s.Attachment.SnapDistanceDip >= AttachmentSettings.MinimumSnapDistanceDip &&
                s.Attachment.SnapDistanceDip <= AttachmentSettings.MaximumSnapDistanceDip,
                "Attachment.SnapDistanceDip", "Must be finite and between 24 and 140 DIP.");
        if (Present(s.Voice, "Voice"))
            Require(s.Voice.VolumePercent >= VoiceSettings.MinimumVolumePercent &&
                s.Voice.VolumePercent <= VoiceSettings.MaximumVolumePercent,
                "Voice.VolumePercent", "Volume must be between 0 and 100 percent.");
        return new(errors.AsReadOnly());
    }
}
