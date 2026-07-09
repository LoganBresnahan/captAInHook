using CaptainHook.Api;

namespace CaptainHook.Tests;

// dto-schema-codegen (ADR-0008 decision 6): the checked-in schema IS the drift
// detector — a DTO change that forgets to regenerate web/schema/api.schema.json
// (and the TS the web/ build derives from it) fails HERE, at build time, never
// as a silent shape mismatch in the browser. Regenerate + commit in the SAME
// commit as the DTO change:
//   CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter ApiSchemaTests
//   (cd web && npm run build)   # re-derives src/api.gen.ts and rebuilds ui/
public class ApiSchemaTests
{
    private static string SchemaPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "schema", "api.schema.json"));

    [Fact]
    public void CheckedInSchema_MatchesTheDtos()
    {
        var generated = ApiSchema.Export();
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_SCHEMA_UPDATE") == "1")
            File.WriteAllText(SchemaPath, generated);
        Assert.True(File.Exists(SchemaPath), $"missing {SchemaPath} — run with CAPTAINHOOK_SCHEMA_UPDATE=1");
        Assert.Equal(File.ReadAllText(SchemaPath), generated);
    }

    [Fact]
    public void Schema_CarriesEveryEndpointRoot_InWireCasing()
    {
        // The wire is camelCase (ApiJson's Web options); the schema must
        // describe THAT, not the C# property spelling.
        var schema = ApiSchema.Export();
        foreach (var def in new[] { "StatusDto", "PolicyDto", "HarnessesDto", "HandlersDto", "ApiDiscovery" })
            Assert.Contains($"\"{def}\"", schema);
        Assert.Contains("\"uptimeMs\"", schema);       // camelCase property
        Assert.Contains("\"openStreams\"", schema);
        Assert.DoesNotContain("\"UptimeMs\"", schema); // never PascalCase on the wire
    }

    [Fact]
    public void Schema_IsHonestAboutNullability()
    {
        // PolicyDto's tri-state fields are genuinely nullable (string? Error);
        // StatusDto's version is not. The generated TS leans on this.
        var schema = ApiSchema.Export();
        Assert.Contains("\"null\"", schema);            // some nullable exists
        var start = schema.IndexOf("\"StatusDto\"", StringComparison.Ordinal);
        var end = schema.IndexOf("\"PolicyDto\"", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "defs out of order");
        Assert.DoesNotContain("\"null\"", schema[start..end]);   // StatusDto: all required, none nullable
    }
}
