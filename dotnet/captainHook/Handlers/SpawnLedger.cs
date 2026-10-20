using System.Text;
using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Handlers;

/// The daemon's ONESHOT spawn ledger (ADR-0010, 2026-10-19 amendment):
/// `children/daemon-<pid>.spawns`, one append-only line per oneshot spawn
/// (a ChildRecords.Record, mode "oneshot") and one `{"Done":<pid>}` line when
/// the dispatch lets go of it. Measured: a oneshot mid-run when its daemon is
/// SIGKILLed outlives it — reparented, unrecorded, invisible to doctor, alive
/// until it ends on its own (forever, if it hangs). The ledger is what the
/// next daemon's OrphanReaper reads to find it.
///
/// Why a ledger and not a record per oneshot: a record is a file create and
/// a delete on the dispatch critical path (the item-12 tax ChildRecords
/// refuses for oneshots). This is two `write(2)`s on an fd held open for the
/// daemon's life — no create, no unlink, no fsync (a crash that loses the
/// page cache also loses the processes the lines describe). The Done line is
/// what keeps the reap honest: without it every oneshot the daemon ever ran
/// would be a candidate, and a long-dead pid recycled onto a stranger's
/// group could draw a kill. With it, the candidates are exactly the spawns
/// in flight when the daemon died.
///
/// Daemon-only: DaemonHost opens `Current`; collapsed runs and tests leave
/// it null and every call here is a no-op. Best-effort throughout: a ledger
/// failure never fails a spawn — one warn, then silence.
internal sealed class SpawnLedger : IDisposable
{
    /// The running daemon's ledger, or null (collapsed run, tests, a ledger
    /// that could not be opened).
    internal static SpawnLedger? Current;

    internal string Path { get; }
    private readonly FileStream _fs;
    private readonly object _lock = new();
    private bool _closed, _warned;

    private SpawnLedger(string path, FileStream fs) => (Path, _fs) = (path, fs);

    internal static string PathFor(string dir, int daemonPid) =>
        System.IO.Path.Combine(dir, $"daemon-{daemonPid}.spawns");

    /// Open (truncating) this process's ledger in `dir`. Truncating is right:
    /// a file already at this name belongs to a dead predecessor that held
    /// the same pid, and the boot reap has already read it — it runs first.
    internal static SpawnLedger? Open(string dir)
    {
        var path = PathFor(dir, Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception) { /* permissions best-effort, like ChildRecords */ }
            var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 0,   // every line is one write(2), visible to a reader at once
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            return new SpawnLedger(path, fs);
        }
        catch (Exception ex)
        {
            Log.Warn("exec", "exec.ledgerError", new LogFields
            {
                Msg = $"oneshot spawn ledger unavailable: {ex.Message} — a SIGKILLed daemon's in-flight oneshots won't be reaped",
                Data = new Dictionary<string, object> { ["path"] = path },
            });
            return null;
        }
    }

    internal void Spawned(int pid, string command, string entry, string evt) =>
        Append(JsonSerializer.Serialize(new ChildRecords.Record(
            pid, ChildRecords.ProcStartTime(pid), command, entry, evt, "oneshot",
            Environment.ProcessId, DateTimeOffset.UtcNow, ChildRecords.SelfStartTime)));

    internal void Done(int pid) => Append($"{{\"Done\":{pid}}}");

    private void Append(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_lock)
        {
            if (_closed) return;
            try { _fs.Write(bytes); }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Log.Warn("exec", "exec.ledgerError", new LogFields
                {
                    Msg = $"oneshot spawn ledger write failed: {ex.Message}",
                    Data = new Dictionary<string, object> { ["path"] = Path },
                });
            }
        }
    }

    /// Orderly exit: every oneshot has been seen off by the drain's child
    /// phase, so the ledger has nothing left to say — close and remove it.
    public void Dispose() => Close(delete: true);

    /// `delete: false` when the drain could NOT prove its children dead (the
    /// teardown outran its bound): the file stays for the next boot reap.
    internal void Close(bool delete)
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            try { _fs.Dispose(); } catch (Exception) { }
        }
        if (delete)
            try { File.Delete(Path); } catch (Exception) { /* the next boot reap clears it */ }
    }

    /// The oneshots a ledger says were still in flight: the latest spawn line
    /// per pid with no Done after it. Unparseable lines are
    /// skipped (a torn final line from a SIGKILL mid-write is expected).
    internal static IReadOnlyList<ChildRecords.Record> InFlight(string path)
    {
        var open = new Dictionary<int, ChildRecords.Record>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("Done", out var done))
                    open.Remove(done.GetInt32());
                else if (doc.RootElement.Deserialize<ChildRecords.Record>() is { } rec)
                    open[rec.Pid] = rec;
            }
            catch (Exception) { /* torn or foreign line: no evidence value */ }
        }
        return open.Values.ToList();
    }

    /// The owning daemon's pid, from the file name (`daemon-<pid>.spawns`).
    internal static int? OwnerPid(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("daemon-", StringComparison.Ordinal)
               && int.TryParse(name["daemon-".Length..], out var pid) ? pid : null;
    }
}
