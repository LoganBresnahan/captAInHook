namespace CaptainHook.Mail;

// Roadmap item 21 / ADR-0016 decision 14 — the READ-ONLY face of the bus, and
// the first of the three pins that make "observation is not delivery" a
// property of the architecture rather than a promise the UI makes.
//
// The management API never receives a `MailStore` or a `MailCursors`. It
// receives THIS: five method-group delegates bound to the three read
// operations d14 names (`MailStore.Read`, `MailStore.VerifyChain`,
// `MailCursors.Pending`) plus the two cheap identity/listing reads the
// snapshot needs (`HeadHash`, `MailCursors.List`). `Append` and `Advance` are
// not among them, so a "mark read" button in the GUI has nothing to call: the
// write half of the bus is not reachable from the API graph at all, and the
// only code that can advance a cursor stays the digest running at a declared
// seam.
//
// Why delegates rather than an interface `MailStore` implements: an interface
// is implemented BY the writable type, so an API holding it holds the writable
// object and one cast (or one added interface member) re-opens the write path.
// A port that captures method groups can only ever do the five things it was
// built with. The honest bound, stated because the test that pins this says
// the same: the captured closures DO reference the store and the cursors, so
// reflection over a live port's fields cannot prove the absence — it proves
// the DECLARED graph, and a source pin over `Api/` proves nothing there names
// Append or Advance. Both are asserted (MailApiTests); neither alone is the
// claim.
public sealed class MailReadPort
{
    private readonly Func<IReadOnlyList<MailLine>> _read;
    private readonly Func<IReadOnlyList<MailChainFault>> _verifyChain;
    private readonly Func<string?> _headHash;
    private readonly Func<IReadOnlyList<(string Role, string? Session)>> _cursors;
    private readonly Func<string, string?, MailPendingView> _pending;

    private MailReadPort(
        string dir,
        string filePath,
        Func<IReadOnlyList<MailLine>> read,
        Func<IReadOnlyList<MailChainFault>> verifyChain,
        Func<string?> headHash,
        Func<IReadOnlyList<(string Role, string? Session)>> cursors,
        Func<string, string?, MailPendingView> pending)
    {
        Dir = dir;
        FilePath = filePath;
        _read = read;
        _verifyChain = verifyChain;
        _headHash = headHash;
        _cursors = cursors;
        _pending = pending;
    }

    /// The mail directory being observed — reported to the client so the GUI
    /// can say WHICH bus it is drawing (the same reason PolicyDto carries its
    /// path). A path, not a handle: naming a directory opens nothing.
    public string Dir { get; }

    /// The store file itself. Named by the port rather than reassembled by the
    /// caller, so the API never has to know the format's filename.
    public string FilePath { get; }

    /// The only constructor path. The store and the cursors are created HERE
    /// and captured; no caller ever holds them, so no caller can hand the API
    /// a writable one by accident.
    public static MailReadPort Over(string dir)
    {
        var store = new MailStore(dir);
        var cursors = new MailCursors(store);
        return new MailReadPort(
            store.Dir, store.FilePath, store.Read, store.VerifyChain, store.HeadHash,
            // The names on disk ARE cursor keys (role × instance, ADR-0018 d3),
            // so the second half goes in as the instance and no hook session
            // goes in at all: an observation reads a mailbox, it does not
            // happen inside anyone's dispatch, and nothing it does writes a
            // trail line that would need a window's name.
            () => MailCursors.List(store.Dir), (role, instance) => cursors.Pending(role, instance));
    }

    /// The store generation cursors compare against (`MailCursors.CurrentGen`
    /// — 1 until rotation machinery lands, ADR-0016 N4). Re-exported here so
    /// the API never has to NAME the writable types at all: the source pin
    /// that keeps `Api/` free of `MailStore`/`MailCursors` is only meaningful
    /// if nothing there needs them, constants included.
    public int Gen => MailCursors.CurrentGen;

    /// Every line on disk, torn tail included (each carries `Terminated`), in
    /// file order. An unreadable store reads as empty — never a throw.
    public IReadOnlyList<MailLine> Read() => _read();

    /// Every link that does not hold; empty means the chain verifies back to
    /// genesis (ADR-0016 d11).
    public IReadOnlyList<MailChainFault> VerifyChain() => _verifyChain();

    /// The chain's identity: the first COMPLETE line's hash, null when the
    /// store is absent, empty, or a single torn line.
    public string? HeadHash() => _headHash();

    /// Who holds a cursor file right now (role, session) — the presence
    /// inference's on-disk half.
    public IReadOnlyList<(string Role, string? Session)> Cursors() => _cursors();

    /// One recipient's view of the store: frontier, pending, held, expired.
    /// A pure read — `MailCursors.Pending` computes and writes nothing, which
    /// is why the observation surface may call it per cursor on every request.
    public MailPendingView Pending(string role, string? session) => _pending(role, session);
}
