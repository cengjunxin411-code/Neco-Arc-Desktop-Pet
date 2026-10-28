using System;
using System.Windows;

namespace NecoArcDesktopPet.Animation;

// One horizontal trip, in logical window DIPs. Owns no timer or animation.
internal sealed class AutonomousMovementSession
{
    public MovementDefinition Definition { get; }
    public bool IsMirrored => Definition.AllowsMirror && Direction != Definition.NativeDirection;
    private const double MinimumSpace = 40;
    public double X { get; private set; }
    public double TargetX { get; }
    public int Direction { get; }
    public bool IsComplete { get; private set; }

    private AutonomousMovementSession(double x, double targetX, int direction, MovementDefinition definition)
    {
        X = x;
        TargetX = targetX;
        Direction = direction;
        Definition = definition;
    }

    public static AutonomousMovementSession? TryCreate(double x, double width, Rect workArea, Random random, MovementDefinition definition)
    {
        if (workArea.IsEmpty || !double.IsFinite(x) || !double.IsFinite(width) || width <= 0 ||
            width > workArea.Width || x < workArea.Left || x + width > workArea.Right) return null;
        var direction = random.Next(2) == 0 ? -1 : 1;
        if (!definition.AllowsMirror) direction = definition.NativeDirection;
        double Space(int facing) => facing < 0 ? x - workArea.Left : workArea.Right - width - x;
        if (Space(direction) < MinimumSpace && definition.AllowsMirror) direction = -direction;
        var space = Space(direction);
        if (space < MinimumSpace) return null;
        var distance = Math.Min(definition.MinDistance + random.NextDouble() * (definition.MaxDistance - definition.MinDistance), space);
        return new AutonomousMovementSession(x, x + direction * distance, direction, definition);
    }

    public void Advance(double elapsedSeconds, double minimumX, double maximumX)
    {
        if (IsComplete || elapsedSeconds <= 0 || !double.IsFinite(elapsedSeconds)) return;
        if (minimumX > maximumX || X < minimumX || X > maximumX)
        {
            // A changed work area may make the current window unsafe. Stop without teleporting.
            IsComplete = true;
            return;
        }
        var target = Math.Clamp(TargetX, minimumX, maximumX);
        X = Direction < 0 ? Math.Max(target, X - Definition.Speed * elapsedSeconds) : Math.Min(target, X + Definition.Speed * elapsedSeconds);
        IsComplete = X == target;
    }
}
