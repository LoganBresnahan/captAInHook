namespace CaptainHook.Mail;

// Roadmap item 22 / ADR-0017 slice `turn-provenance` — the amendment to N6.
//
// N6 says a woken turn's `from.session` is "a payload-stamped name". As first
// built it was MODEL-stamped, which is a different and weaker thing: `mail
// send` takes an envelope on stdin, so the turn's own model composed its
// provenance and the first real robot answer arrived as
// `{agent: "claude", harness: "claude-code", session: "reviewer"}` — `agent`
// naming a MODEL where the field means the member, and `session` holding a
// ROLE name that joins to nothing on any picture. A record that misdescribes
// who spoke is a defect at any scope, and prose is not provenance.
//
// THE DECLARATION IS AN ENVIRONMENT FACT, not an argument. The seam has to be
// something the model cannot compose, and everything else about a woken turn's
// `mail send` call IS composed by the model: the flags it types, the JSON it
// writes. What it does not write is the environment it inherited from the
// payload that spawned it — so the payload states the identity once, before
// the model exists, and every `mail send` in that process tree is written by
// that member whatever the model says about itself.
//
// THE RULE IS THE ONE `hops` AND `ts` ALREADY USE, and it is the same rule for
// all three fields:
//
//   * the envelope STATES the declared value ⇒ it stands (a no-op).
//   * the envelope STATES something else ⇒ REFUSED, nothing written. Not
//     corrected: silently rewriting a sender's claim would hide the attempt on
//     a chain nobody can amend, exactly as `mail send` already refuses a stated
//     `hops` that disagrees with the ledger.
//   * the envelope STATES NOTHING ⇒ stamped, the `ts` rule. Only `session` can
//     be absent — the parser requires `agent` and `harness` — so this is the
//     case that gives a turn the instance it could not know it had.
//
// NOT DECLARED IS NOT AN ERROR. With no declaration in the environment this
// type does nothing at all, which is every human at a CLI and every window: a
// member that names itself is the ordinary case, and the bus has always taken
// a sender at its word (N1 — every member is already a same-user process, so
// this is honesty plumbing, not a trust boundary).
public sealed record MailSenderDeclaration(string? Agent, string? Harness, string? Session)
{
    // Normalized AT CONSTRUCTION, not at the environment read, so a blank can
    // never become a declaration by any route: `export X=` is how a shell says
    // "unset" often enough that reading it as a member named "" would turn a
    // payload's typo into a nameless sender.
    public string? Agent { get; } = Blank(Agent) ? null : Agent;
    public string? Harness { get; } = Blank(Harness) ? null : Harness;
    public string? Session { get; } = Blank(Session) ? null : Session;

    private static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);

    /// The three env vars a spawning payload sets. Three scalars rather than
    /// one packed string on purpose: each maps 1:1 onto an envelope field, a
    /// partial declaration is meaningful, and nothing here needs a grammar or
    /// a parser of its own.
    public const string AgentEnv = "CAPTAINHOOK_MAIL_FROM_AGENT";
    public const string HarnessEnv = "CAPTAINHOOK_MAIL_FROM_HARNESS";
    public const string SessionEnv = "CAPTAINHOOK_MAIL_FROM_SESSION";

    /// What every ordinary sender gets: no declaration, no change.
    public static readonly MailSenderDeclaration None = new(null, null, null);

    public bool Declares => Agent is not null || Harness is not null || Session is not null;

    /// Read the environment. Blanks are normalized away by the constructor.
    public static MailSenderDeclaration FromEnvironment() => new(
        Environment.GetEnvironmentVariable(AgentEnv),
        Environment.GetEnvironmentVariable(HarnessEnv),
        Environment.GetEnvironmentVariable(SessionEnv));

    /// Apply the declaration to what a sender stated, per the header's three
    /// rules. `stated*` is null when the sender said nothing about that field.
    /// Every violation is collected — the parser's all-or-nothing shape — and a
    /// caller with any refusal must write NOTHING.
    public (string? Agent, string? Harness, string? Session) Resolve(
        string? statedAgent, string? statedHarness, string? statedSession,
        out IReadOnlyList<string> refusals)
    {
        var errs = new List<string>();
        var resolved = (
            Field(Agent, statedAgent, "agent", AgentEnv, errs),
            Field(Harness, statedHarness, "harness", HarnessEnv, errs),
            Field(Session, statedSession, "session", SessionEnv, errs));
        refusals = errs;
        return resolved;
    }

    /// One field. The declaration is the authority; the stated value only ever
    /// agrees with it or is refused.
    private static string? Field(
        string? declared, string? stated, string field, string env, List<string> errs)
    {
        if (declared is null) return stated;
        if (stated is null || string.Equals(stated, declared, StringComparison.Ordinal))
            return declared;

        errs.Add(
            $"'from.{field}' says \"{MailEnvelope.ClampField(stated)}\", but this process is " +
            $"declared as \"{MailEnvelope.ClampField(declared)}\" ({env}). The payload that " +
            "spawned you names the sender; a member cannot name itself somebody else. Write " +
            $"\"{field}\": \"{MailEnvelope.ClampField(declared)}\" — or leave the field out where " +
            "it is optional and this stamps it.");
        return declared;
    }
}
