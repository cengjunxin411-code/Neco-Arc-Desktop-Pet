using System;

namespace NecoArcDesktopPet.Animation;

internal sealed record ClickActionDefinition(string ResourcePath, int RepeatCount);

internal static class ClickDefinitions
{
    public static ClickActionDefinition[] SingleActions { get; } =
    {
        new("NecoPetMaterials/click/flick.gif", 1),
        new("NecoPetMaterials/click/pound.gif", 7),
        new("NecoPetMaterials/click/quick_punch.gif", 15)
    };
    public static ClickActionDefinition[] RocketActions { get; } =
    {
        new("NecoPetMaterials/click/rocket.gif", 0),
        new("NecoPetMaterials/click/rocket2.gif", 0)
    };
    public const string RotationPath = "NecoPetMaterials/click/rotation.gif";

    public static ClickActionDefinition[] Available(ClickActionDefinition[] actions, GifFramePlayer? player) =>
        Array.FindAll(actions, action => player?.TryCacheClip(action.ResourcePath) == true);

    // One bucket per available candidate gives equal weights without probability tests.
    public static ClickActionDefinition? Choose(ClickActionDefinition[] available, Random random) =>
        available.Length == 0 ? null : available[random.Next(available.Length)];
}
