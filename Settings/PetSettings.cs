using System;

namespace NecoArcDesktopPet.Settings;

// Mutable working model. Validate at the boundary before publishing a live copy.
public sealed class PetSettings
{
    public NormalBehaviorSettings Normal { get; set; } = new();
    public RestSettings Rest { get; set; } = new();
    public SleepSettings Sleep { get; set; } = new();
    public MovementSettings Movement { get; set; } = new();
    public InteractionSettings Interaction { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public AttachmentSettings Attachment { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();

    public PetSettings DeepClone() => new()
    {
        Normal = Normal with { }, Rest = Rest with { }, Sleep = Sleep with { },
        Movement = Movement.DeepClone(), Interaction = Interaction with { }, Diagnostics = Diagnostics with { },
        Attachment = Attachment with { }, Voice = Voice with { }
    };

    public void ApplyFrom(PetSettings workingCopy)
    {
        var validation = PetSettingsValidator.Validate(workingCopy);
        if (!validation.IsValid) throw new ArgumentException(string.Join("; ", validation.Errors), nameof(workingCopy));
        var copy = workingCopy.DeepClone();
        Normal = copy.Normal; Rest = copy.Rest; Sleep = copy.Sleep;
        Movement = copy.Movement; Interaction = copy.Interaction; Diagnostics = copy.Diagnostics;
        Attachment = copy.Attachment;
        Voice = copy.Voice;
    }
}
