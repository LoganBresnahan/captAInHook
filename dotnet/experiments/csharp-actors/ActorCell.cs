using System.Threading.Channels;

namespace CsharpActors;

// ---------------------------------------------------------------------------
// ActorCell = the RUNTIME wrapper around a user-supplied IActor.
//
// It provides the three things the IActor itself does not:
//   1. the MAILBOX (Channel<object>),
//   2. the consumer LOOP (one Task per actor, reading run-to-completion),
//   3. crash handling: a try/catch that converts an exception into an EXIT
//      message delivered to the owning supervisor's mailbox.
//
// Restart is deliberately NOT handled here. A cell is disposable: to restart,
// the supervisor throws the whole cell away and builds a brand-new one from the
// child's factory func. Fresh IActor => fresh state, fresh mailbox, fresh loop.
// ---------------------------------------------------------------------------

public sealed class ActorCell : IActorContext, IAsyncDisposable
{
    private readonly IActor _actor;
    private readonly Channel<object> _mailbox;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();

    // Where to send EXIT when this actor's handler throws. Null for a top-level
    // actor (like the supervisor itself) that nobody supervises.
    private readonly ChannelWriter<object>? _supervisorMailbox;

    public string SelfId { get; }

    public ActorCell(string id, IActor actor, ChannelWriter<object>? supervisorMailbox = null)
    {
        SelfId = id;
        _actor = actor;
        _supervisorMailbox = supervisorMailbox;

        // Unbounded FIFO mailbox. SingleReader = only our one loop consumes it,
        // which lets the channel skip reader-side synchronisation.
        _mailbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _loop = Task.Run(RunLoop);
    }

    /// <summary>TELL: fire-and-forget. Post a message and move on.</summary>
    public ValueTask Tell(object message) => _mailbox.Writer.WriteAsync(message, _cts.Token);

    /// <summary>
    /// ASK: request/reply. We wrap the request in an <see cref="Ask{TReply}"/>
    /// carrying a TaskCompletionSource, post it, then await the TCS. The actor
    /// completes the TCS from inside its handler, which resolves our Task.
    /// </summary>
    public async Task<TReply> Ask<TReply>(object request)
    {
        var tcs = new TaskCompletionSource<TReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _mailbox.Writer.WriteAsync(new Ask<TReply> { Request = request, Reply = tcs }, _cts.Token);
        return await tcs.Task;
    }

    // The heart of the actor: read ONE message, fully handle it, repeat.
    private async Task RunLoop()
    {
        try
        {
            // ReadAllAsync yields messages strictly in arrival order. The await
            // inside the loop body is what enforces run-to-completion: we do not
            // come back around for the next message until this one is done.
            await foreach (var message in _mailbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _actor.Handle(message, this);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown (Dispose). Not a crash — swallow it.
        }
        catch (Exception ex)
        {
            // CRASH. The handler threw. We do NOT try to recover in place —
            // instead we tell our supervisor (EXIT) and let it decide policy.
            // If we have no supervisor, the process is expected to notice.
            if (_supervisorMailbox is not null)
            {
                await _supervisorMailbox.WriteAsync(new Exit { ChildId = SelfId, Error = ex });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _mailbox.Writer.TryComplete();
        try { await _loop; } catch { /* already torn down */ }
        _cts.Dispose();
    }
}
