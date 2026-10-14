using System.Text.Json;
using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// examples/fixtures/claude-code/ — real PreToolUse/PostToolUse envelopes
// captured from a live Claude Code session (see that dir's README). Payload
// authors build against them, so two things must stay true and neither can be
// checked by reading the files: (1) they are what THIS encoder writes, byte
// for byte, from Claude Code's raw payload through the embedded claude-code
// spec, and (2) the facts the README states about them hold. A fixture that
// drifts from the encoder, or a README claim the files contradict, fails here.
public class ClaudeCodeFixtureTests
{
    private static string FixturesDir() =>
        Path.Combine(Path.GetDirectoryName(DogfoodInfra.PayloadsDir())!, "fixtures", "claude-code");

    public static TheoryData<string> Files() =>
        new(Directory.GetFiles(FixturesDir(), "*.jsonl").Select(Path.GetFileName).Order()!);

    private static (JsonElement Pre, JsonElement Post, string[] Lines) Load(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(FixturesDir(), file));
        Assert.Equal(2, lines.Length);
        return (JsonDocument.Parse(lines[0]).RootElement, JsonDocument.Parse(lines[1]).RootElement, lines);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EachFixture_IsExactlyWhatTheEncoderWrites_FromTheRawPayload(string file)
    {
        var spec = new HarnessRegistry(NoHarnessDir()).Get("claude-code");
        foreach (var line in Load(file).Lines)
        {
            var root = JsonDocument.Parse(line).RootElement;
            var raw = root.GetProperty("event").GetProperty("payload");

            var e = Harness.ParseEvent(spec, cliEventName: null, raw);
            var reEncoded = ExecWire.Envelope(e, root.GetProperty("dispatchId").GetString()!);

            Assert.Equal(line, reEncoded);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void PreThenPost_OneCall_SameToolUseId_HarnessNamed_PromptIdPresent(string file)
    {
        var (pre, post, _) = Load(file);
        var (preP, postP) = (pre.GetProperty("event").GetProperty("payload"), post.GetProperty("event").GetProperty("payload"));

        Assert.Equal("PreToolUse", pre.GetProperty("event").GetProperty("type").GetString());
        Assert.Equal("PostToolUse", post.GetProperty("event").GetProperty("type").GetString());
        Assert.Equal("claude-code", pre.GetProperty("event").GetProperty("harness").GetString());
        Assert.Equal("claude-code", post.GetProperty("event").GetProperty("harness").GetString());

        var id = preP.GetProperty("tool_use_id").GetString();
        Assert.StartsWith("toolu_", id);
        Assert.Equal(id, postP.GetProperty("tool_use_id").GetString());

        Assert.False(string.IsNullOrEmpty(preP.GetProperty("prompt_id").GetString()));
        Assert.Equal(preP.GetProperty("prompt_id").GetString(), postP.GetProperty("prompt_id").GetString());

        Assert.False(preP.TryGetProperty("tool_response", out _));
        Assert.True(postP.TryGetProperty("tool_response", out _));
        Assert.True(postP.TryGetProperty("duration_ms", out _));
    }

    [Theory]
    [InlineData("write-create.jsonl", "file_path")]
    [InlineData("write-update.jsonl", "file_path")]
    [InlineData("edit.jsonl", "file_path")]
    [InlineData("read-text.jsonl", "file_path")]
    [InlineData("read-notebook.jsonl", "file_path")]
    [InlineData("notebook-edit.jsonl", "notebook_path")]
    public void TheFilePathField_IsTheOneTheReadmeNames(string file, string field)
    {
        var post = Load(file).Post.GetProperty("event").GetProperty("payload");

        Assert.StartsWith("/", post.GetProperty("tool_input").GetProperty(field).GetString());
    }

    [Theory]
    [InlineData("write-create.jsonl", "create", true)]
    [InlineData("write-update.jsonl", "update", false)]
    public void WriteSaysCreateOrUpdate_AndOriginalFileIsNullOnlyOnCreate(string file, string type, bool originalIsNull)
    {
        var resp = Load(file).Post.GetProperty("event").GetProperty("payload").GetProperty("tool_response");

        Assert.Equal(type, resp.GetProperty("type").GetString());
        Assert.Equal(originalIsNull, resp.GetProperty("originalFile").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void BashCarriesACommandAndNoPath()
    {
        var input = Load("bash.jsonl").Post.GetProperty("event").GetProperty("payload").GetProperty("tool_input");

        Assert.Contains("\n", input.GetProperty("command").GetString());
        Assert.False(input.TryGetProperty("file_path", out _));
    }
}
