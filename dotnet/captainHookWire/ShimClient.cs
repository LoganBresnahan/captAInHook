using System.Diagnostics;
using System.Net.Sockets;

namespace CaptainHook.Wire;

/// How one forward attempt ended (ADR-0004 decisions 2–3). A closed set, like
/// Effect: the caller pattern-matches, and the AT-MOST-ONCE boundary is
/// encoded in the types — NotDelivered is the ONLY outcome that permits a
/// collapsed fallback, because it is the only one where non-delivery is
/// provable (connect failed, or the request frame was never fully written —
/// the daemon dispatches nothing off an incomplete frame).
public abstract record ForwardOutcome
{
    /// The daemon answered: relay verbatim — stdout bytes, stderr text, exit code.
    public sealed record Answered(int ExitCode, byte[] StdoutBytes, string StderrText) : ForwardOutcome;

    /// Failure provably BEFORE delivery: fall back to collapsed dispatch
    /// (and, once detached-daemon-spawn lands, spawn a daemon for next time).
    public sealed record NotDelivered(string Reason) : ForwardOutcome;

    /// The request was (or may have been) delivered, then the exchange failed:
    /// the daemon might already be running non-idempotent Background effects.
    /// This dispatch is FAILED — re-dispatching would run them twice. Callers
    /// emit zero stdout bytes and a non-zero exit; they never retry.
    public sealed record FailedAfterDelivery(string Reason) : ForwardOutcome;
}

/// The shim's warm path: connect to the daemon's socket, forward the framed
/// request, relay the framed response. Deadlines are phase-scoped, not one
/// umbrella: the PRE-DELIVERY phase (connect + request write) is a small
/// multiple of a warm round-trip — its expiry proves non-delivery and permits
/// fallback — while the RESPONSE phase must cover the daemon's full dispatch
/// budget + grace (a legitimate slow handler is not a wedged daemon), and its
/// expiry is a failed dispatch, not a retry. A daemon that accepts but never
/// answers — wedged accept loop, mid-drain listener — is thereby bounded: the
/// agent host is never hung on a UDS backlog that connect() happily enters.
public static class ShimClient
{
    /// Connect + write budget: a warm daemon does both in single-digit ms;
    /// 250ms is generous headroom without a human-noticeable stall.
    public static readonly TimeSpan PreDeliveryTimeout = TimeSpan.FromMilliseconds(250);

    /// Response budget: the daemon's 2s dispatch budget + grace + margin.
    /// Expiry here does NOT permit fallback — the dispatch was delivered.
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ForwardOutcome> TryForwardAsync(
        string socketPath, HookRequest req,
        TimeSpan? preDeliveryTimeout = null, TimeSpan? responseTimeout = null)
    {
        var sw = Stopwatch.StartNew();
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        // ---- pre-delivery phase: connect + write the request frame ----------
        // Any failure in here provably precedes delivery: no socket, no daemon;
        // an incomplete frame is never dispatched (the daemon frames on the
        // length prefix). One deadline spans both — the boundary is WriteAsync
        // returning, not the phase seams inside it.
        using (var pre = new CancellationTokenSource(preDeliveryTimeout ?? PreDeliveryTimeout))
        {
            try
            {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), pre.Token);
            }
            catch (Exception ex)
            {
                // The everyday cold case (no daemon yet) — info, not error.
                WireLog.Info("shim", "shim.fallback", new WireLogFields
                {
                    DispatchId = req.DispatchId, DurMs = sw.Elapsed.TotalMilliseconds,
                    Msg = $"connect: {Reason(ex)}",
                });
                return new ForwardOutcome.NotDelivered($"connect: {Reason(ex)}");
            }

            // The EXACT boundary: `committed` flips the instant the last
            // payload byte is accepted by the transport. A failure thrown out
            // of the write call before that is provably pre-delivery; the same
            // exception thrown after it (a deadline landing on the flush or on
            // the way out) means the daemon may already be dispatching — and a
            // fallback would run this hook TWICE. The flag, not the catch
            // block, decides.
            var committed = false;
            try
            {
                var stream = new NetworkStream(sock, ownsSocket: false);
                await Frame.WriteAsync(stream, req.Encode(), pre.Token, committed: () => committed = true);
            }
            catch (Exception ex) when (!committed)
            {
                WireLog.Warn("shim", "shim.fallback", new WireLogFields
                {
                    DispatchId = req.DispatchId, DurMs = sw.Elapsed.TotalMilliseconds,
                    Msg = $"request write: {Reason(ex)}",
                });
                return new ForwardOutcome.NotDelivered($"request write: {Reason(ex)}");
            }
            catch (Exception ex)
            {
                return Failed(req, sw, $"post-commit write failure: {Reason(ex)}");
            }
        }
        // ==== the at-most-once boundary: the request frame is fully written ====

        try
        {
            using var rcts = new CancellationTokenSource(responseTimeout ?? ResponseTimeout);
            var stream = new NetworkStream(sock, ownsSocket: false);
            var payload = await Frame.ReadAsync(stream, rcts.Token);
            if (payload is null)
                return Failed(req, sw, "daemon closed the connection before answering");

            var res = HookResponse.Decode(payload);
            WireLog.Info("shim", "shim.answered", new WireLogFields
            {
                DispatchId = req.DispatchId, DurMs = sw.Elapsed.TotalMilliseconds,
                Data = new Dictionary<string, object>
                {
                    ["exit"] = res.ExitCode,
                    ["stdoutBytes"] = res.StdoutBytes.Length,
                },
            });
            return new ForwardOutcome.Answered(res.ExitCode, res.StdoutBytes, res.StderrText);
        }
        catch (Exception ex)
        {
            return Failed(req, sw, Reason(ex));
        }
    }

    private static ForwardOutcome Failed(HookRequest req, Stopwatch sw, string reason)
    {
        // Delivered (or possibly delivered), then broken: Background effects
        // may already be running daemon-side. At-most-once forbids the retry.
        WireLog.Error("shim", "shim.deliveryFailed", new WireLogFields
        {
            DispatchId = req.DispatchId, DurMs = sw.Elapsed.TotalMilliseconds, Msg = reason,
        });
        return new ForwardOutcome.FailedAfterDelivery(reason);
    }

    private static string Reason(Exception ex) => ex switch
    {
        OperationCanceledException => "deadline expired",
        SocketException se => se.SocketErrorCode.ToString(),
        _ => ex.Message,
    };
}
