using System;
using System.Collections.Generic;

namespace NecoArcDesktopPet.Animation;

internal static class RandomIdleActions
{
    public static readonly TimeSpan DefaultAmbientDuration = TimeSpan.FromSeconds(5);
    private sealed record ActionDefinition(string ResourcePath, TimeSpan? Duration = null);
    private static readonly ActionDefinition[] Candidates =
    {
        new("NecoPetMaterials/idle/watch_phone.gif"),
        new("NecoPetMaterials/idle/goblet.gif"),
        new("NecoPetMaterials/idle/laugh.gif")
    };

    public static bool Contains(string path) => Array.Exists(Candidates,
        action => string.Equals(action.ResourcePath, path, StringComparison.OrdinalIgnoreCase));

    public static TimeSpan DurationFor(string resourcePath)
    {
        foreach (var action in Candidates)
            if (string.Equals(action.ResourcePath, resourcePath, StringComparison.OrdinalIgnoreCase))
                return action.Duration ?? DefaultAmbientDuration;
        return DefaultAmbientDuration;
    }

    public static string[] Available(GifFramePlayer? player)
    {
        var available = new List<string>();
        if (player != null)
            foreach (var action in Candidates)
                if (player.TryCacheClip(action.ResourcePath)) available.Add(action.ResourcePath);
        return available.ToArray();
    }
}
