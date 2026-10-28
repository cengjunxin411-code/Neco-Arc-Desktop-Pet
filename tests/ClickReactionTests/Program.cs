using System;
using NecoArcDesktopPet.Animation;

internal static class Program
{
    private static int Main()
    {
        try { return Run(); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static int Run()
    {
        var cursor = new GifPlaybackCursor(2);

        cursor.Reset(loop: false);
        Check(cursor.FrameIndex == 0, "one-shot starts at frame one");
        Check(!cursor.Advance() && cursor.FrameIndex == 1, "one-shot reaches final frame");
        Check(cursor.Advance() && cursor.IsComplete, "one-shot completes after final frame duration");
        Check(cursor.FrameIndex == 1 && !cursor.Advance(), "completed clip holds final frame and completes once");

        cursor.Reset(loop: true);
        Check(!cursor.Advance() && cursor.FrameIndex == 1, "loop reaches final frame");
        Check(!cursor.Advance() && cursor.FrameIndex == 0 && !cursor.IsComplete, "loop wraps without completion");

        cursor.Reset(loop: false);
        cursor.Advance();
        cursor.Reset(loop: true);
        Check(cursor.FrameIndex == 0 && !cursor.IsComplete, "switch interrupts old one-shot");

        var finiteReset = typeof(GifPlaybackCursor).GetMethod("Reset", new[] { typeof(bool), typeof(int) });
        Check(finiteReset != null, "cursor supports a finite number of complete loops");
        foreach (var repeatCount in new[] { 7, 9, 13 })
        {
            finiteReset!.Invoke(cursor, new object[] { false, repeatCount });
            for (var frame = 0; frame < repeatCount * 2 - 1; frame++)
                Check(!cursor.Advance() && !cursor.IsComplete, "finite repeats hold every frame through the final loop");
            Check(cursor.FrameIndex == 1 && cursor.Advance() && cursor.IsComplete, "finite repeat completes only after final frame duration");
            Check(!cursor.Advance(), "finite repeat notifies completion once");
            cursor.Rewind();
            for (var frame = 0; frame < repeatCount * 2 - 1; frame++) Check(!cursor.Advance(), "rewind restores the full repeat budget");
            Check(cursor.Advance(), "rewound finite repeat completes after all loops");
        }
        finiteReset!.Invoke(cursor, new object[] { false, 13 });
        cursor.Advance();
        cursor.Reset(loop: false);
        Check(!cursor.Advance() && cursor.Advance(), "replacement one-shot clears old finite repeat budget");

        Console.WriteLine("PASS: one-shot, endless loop, finite complete loops, rewind and interruption");
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception($"FAIL: {message}");
        }
    }
}
