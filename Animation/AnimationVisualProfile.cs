namespace NecoArcDesktopPet.Animation;

// Material correction only. Offsets are fractions of the user's logical pet height.
internal readonly record struct AnimationVisualProfile(
    double Scale = 1,
    double OffsetXFactor = 0,
    double OffsetYFactor = 0);
