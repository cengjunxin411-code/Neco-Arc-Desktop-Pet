using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

internal enum MovementType { BaseMove, RelaxedTrot, ExcitedRun, PanicSprint }

// ResourcePath is also the key in AnimationVisualProfiles. Direction is -1 left / +1 right.
internal sealed record MovementDefinition(MovementType Type, string ResourcePath,
    double Speed, double MinDistance, double MaxDistance, int Weight,
    int NativeDirection, bool AllowsMirror = true);

internal static class MovementDefinitions
{
    // Catalog owns resource/facing metadata; configurable values come only from settings.
    private sealed record Metadata(MovementType Type, string ResourcePath, int NativeDirection);
    private static readonly Metadata[] Catalog =
    {
        new(MovementType.BaseMove, "NecoPetMaterials/move/lope.gif", -1),
        new(MovementType.RelaxedTrot, "NecoPetMaterials/move/trot_relaxed.gif", 1),
        new(MovementType.ExcitedRun, "NecoPetMaterials/move/run_excited.gif", 1),
        new(MovementType.PanicSprint, "NecoPetMaterials/move/sprint_panic.gif", 1)
    };
    public static IReadOnlyList<MovementDefinition> All { get; } = Array.AsReadOnly(Create(PetSettingsDefaults.Create().Movement));

    private static MovementProfileSettings Profile(MovementType type, MovementSettings settings) => type switch
    {
        MovementType.BaseMove => settings.Lope, MovementType.RelaxedTrot => settings.TrotRelaxed,
        MovementType.ExcitedRun => settings.RunExcited, MovementType.PanicSprint => settings.SprintPanic,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    private static MovementDefinition[] Create(MovementSettings settings) => Array.ConvertAll(Catalog, metadata =>
    {
        var profile = Profile(metadata.Type, settings);
        return new MovementDefinition(metadata.Type, metadata.ResourcePath, profile.Speed, profile.MinDistance,
            profile.MaxDistance, profile.Weight, metadata.NativeDirection);
    });
    public static bool CanChoose(IReadOnlyList<MovementDefinition> available, MovementSettings settings)
    {
        if (!settings.Enabled) return false;
        foreach (var definition in available) if (Profile(definition.Type, settings).Weight > 0) return true;
        return false;
    }
    public static MovementDefinition? ChooseConfigured(IReadOnlyList<MovementDefinition> available, Random random, MovementSettings settings)
    {
        if (!settings.Enabled) return null;
        var configured = new List<MovementDefinition>();
        foreach (var definition in available)
        {
            var profile = Profile(definition.Type, settings);
            configured.Add(definition with { Speed = profile.Speed, MinDistance = profile.MinDistance,
                MaxDistance = profile.MaxDistance, Weight = profile.Weight });
        }
        return Choose(configured, random);
    }

    public static MovementDefinition[] Available(GifFramePlayer? player)
    {
        var available = new List<MovementDefinition>();
        if (player != null)
            foreach (var definition in All)
                if (player.TryCacheClip(definition.ResourcePath)) available.Add(definition);
        return available.ToArray();
    }

    // Called only after the existing Idle Activity scheduler has chosen Movement.
    public static MovementDefinition? Choose(IReadOnlyList<MovementDefinition> available, Random random)
    {
        var total = 0;
        foreach (var definition in available) total += definition.Weight;
        if (total == 0) return null;
        var roll = random.Next(total);
        foreach (var definition in available)
        {
            if (roll < definition.Weight) return definition;
            roll -= definition.Weight;
        }
        return null;
    }
}
