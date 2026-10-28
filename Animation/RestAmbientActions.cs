using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

// Rest has its own catalog; sharing the deadline utility never shares Normal candidates.
internal static class RestAmbientActions
{
    public const string LieDownPath = "NecoPetMaterials/rest/lie_down.gif";
    private static readonly string[] Candidates = { LieDownPath };

    public static int LoopsFor(string resourcePath, Random random)
        => LoopsFor(resourcePath, random, PetSettingsDefaults.Create().Rest);
    public static int LoopsFor(string resourcePath, Random random, RestSettings settings)
    {
        foreach (var action in Candidates)
            if (string.Equals(action, resourcePath, StringComparison.OrdinalIgnoreCase))
                return random.Next(settings.AmbientMinLoops, settings.AmbientMaxLoops + 1);
        throw new ArgumentException("Unknown Rest Ambient action", nameof(resourcePath));
    }

    public static string[] Available(GifFramePlayer? player)
    {
        var available = new List<string>();
        if (player != null)
            foreach (var action in Candidates)
                if (player.TryCacheClip(action)) available.Add(action);
        return available.ToArray();
    }
}
