using System;

namespace NecoArcDesktopPet.CharacterProfiles;

internal static class CharacterProfileRuntime
{
    // The snapshot, including a failed selection, is immutable for this process.
    // No watcher/hot reload can invalidate an in-flight geometry/physics owner.
    private static readonly Lazy<CharacterProfileLoadResult> Snapshot = new(() =>
        CharacterProfileLoader.LoadFile(Environment.GetEnvironmentVariable("NECO_CHARACTER_PROFILE")));
    public static CharacterProfileLoadResult Result => Snapshot.Value;
    public static CharacterProfile? Current => Result.Profile;
}
