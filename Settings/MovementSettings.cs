namespace NecoArcDesktopPet.Settings;

public sealed record MovementProfileSettings
{
    public double Speed { get; set; }
    public double MinDistance { get; set; }
    public double MaxDistance { get; set; }
    public int Weight { get; set; }
}

public sealed record MovementSettings
{
    public bool Enabled { get; set; }
    // Probability of Movement when both Ambient and weighted Movement are available.
    public double SelectionWeight { get; set; }
    public MovementProfileSettings Lope { get; set; } = new();
    public MovementProfileSettings TrotRelaxed { get; set; } = new();
    public MovementProfileSettings RunExcited { get; set; } = new();
    public MovementProfileSettings SprintPanic { get; set; } = new();
    public MovementSettings DeepClone() => this with
    {
        Lope = Lope with { }, TrotRelaxed = TrotRelaxed with { },
        RunExcited = RunExcited with { }, SprintPanic = SprintPanic with { }
    };
}
