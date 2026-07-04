namespace AkkaActors;

// ---------------------------------------------------------------------------
// Tiny synchronized console logger, mirroring the F# sibling's `Log` module.
//
// Actors (and Akka's OWN async logger) run concurrently, so bare Console.Write
// calls can interleave mid-line. We serialize whole lines behind one lock so the
// demo TRACE stays readable. This is purely a console concern; it is NOT part of
// the actor model. Every line WE emit is prefixed (e.g. "[worker-1]") so our
// narrative is easy to separate from Akka's own [INFO]/[ERROR] log lines.
// ---------------------------------------------------------------------------
internal static class Trace
{
    private static readonly object Gate = new();

    /// <summary>Print one whole line atomically.</summary>
    public static void Line(string text)
    {
        lock (Gate) Console.WriteLine(text);
    }

    /// <summary>Print a labeled phase banner, e.g. "=== spawn 2 workers ===".</summary>
    public static void Banner(string s)
    {
        lock (Gate) Console.WriteLine($"\n=== {s} ===");
    }
}
