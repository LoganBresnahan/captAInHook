using System.Threading.Channels;

namespace CsharpActors;

// ---------------------------------------------------------------------------
// The Supervisor is ITSELF an actor. Its mailbox receives two kinds of message:
//   * Spawn  — start a child from its factory,
//   * Exit   — a child crashed; apply the restart strategy.
//
// Strategy implemented: ONE_FOR_ONE. When one child dies, only THAT child is
// restarted; siblings are untouched (their state and loops keep running). This
// is the natural fit because each child is fully isolated behind its mailbox.
//
// RESTART INTENSITY: a child may only be restarted up to `MaxRestarts` times
// within `Window`. Exceed that and the supervisor gives up on the child and
// "escalates" (here: logs and stops restarting). This prevents a poison message
// from spinning a crash/restart loop forever.
// ---------------------------------------------------------------------------

/// <summary>A child is defined by a factory so restart = "call it again".</summary>
public sealed class ChildSpec
{
    public required string Id { get; init; }
    public required Func<IActor> Factory { get; init; }
}

// Messages understood by the supervisor's own mailbox.
public sealed record Spawn(ChildSpec Spec);

public sealed class Supervisor : IActor
{
    public string Name => "supervisor";

    // Restart-intensity budget.
    private readonly int _maxRestarts;
    private readonly TimeSpan _window;

    // Live children: id -> (running cell, its factory, recent restart timestamps).
    private readonly Dictionary<string, ChildSpec> _specs = new();
    private readonly Dictionary<string, ActorCell> _children = new();
    private readonly Dictionary<string, List<DateTime>> _restarts = new();

    // The supervisor needs a writer to ITS OWN mailbox so that children can be
    // wired to send EXIT here. Set once, right after the cell is constructed.
    private ChannelWriter<object>? _selfMailbox;

    public Supervisor(int maxRestarts = 3, TimeSpan? window = null)
    {
        _maxRestarts = maxRestarts;
        _window = window ?? TimeSpan.FromSeconds(5);
    }

    public void BindMailbox(ChannelWriter<object> selfMailbox) => _selfMailbox = selfMailbox;

    /// <summary>Look up a live child cell so the demo can send it messages.</summary>
    public ActorCell? Child(string id) => _children.TryGetValue(id, out var c) ? c : null;

    // Handled one-at-a-time, so all this bookkeeping is lock-free.
    public async ValueTask Handle(object message, IActorContext context)
    {
        switch (message)
        {
            case Spawn spawn:
                StartChild(spawn.Spec);
                break;

            case Exit exit:
                await OnChildExit(exit);
                break;
        }
    }

    private void StartChild(ChildSpec spec)
    {
        _specs[spec.Id] = spec;
        // Build a fresh IActor from the factory => fresh private state, and hand
        // the cell a writer to the supervisor's mailbox for EXIT delivery.
        var cell = new ActorCell(spec.Id, spec.Factory(), _selfMailbox);
        _children[spec.Id] = cell;
        Console.WriteLine($"[supervisor] started child '{spec.Id}'");
    }

    private async ValueTask OnChildExit(Exit exit)
    {
        Console.WriteLine($"[supervisor] EXIT received from '{exit.ChildId}': {exit.Error.Message}");

        if (!_specs.TryGetValue(exit.ChildId, out var spec))
            return; // unknown / already removed

        // Tear down the crashed cell (its loop already ended when it threw).
        if (_children.TryGetValue(exit.ChildId, out var dead))
        {
            await dead.DisposeAsync();
            _children.Remove(exit.ChildId);
        }

        // ---- restart intensity check ----
        var now = DateTime.UtcNow;
        var history = _restarts.TryGetValue(exit.ChildId, out var h) ? h : _restarts[exit.ChildId] = new();
        history.Add(now);
        history.RemoveAll(t => now - t > _window); // drop old attempts outside the window

        if (history.Count > _maxRestarts)
        {
            // Budget blown: give up on this child and escalate.
            Console.WriteLine(
                $"[supervisor] restart intensity exceeded for '{exit.ChildId}' " +
                $"({history.Count} restarts within {_window.TotalSeconds:0}s) -> ESCALATING, will not restart");
            _specs.Remove(exit.ChildId);
            return;
        }

        // ONE_FOR_ONE: restart ONLY this child. Siblings are never touched.
        Console.WriteLine($"[supervisor] one_for_one: restarting ONLY '{exit.ChildId}' " +
                          $"(restart #{history.Count} within window)");
        StartChild(spec);
    }
}
