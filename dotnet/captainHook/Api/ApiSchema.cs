using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace CaptainHook.Api;

// dto-schema-codegen (ADR-0008 decision 6): the C# DTOs are the single source
// of truth for the GUI's types — TS is GENERATED, never hand-kept, so drift is
// a build failure instead of a silent runtime bug. The server is a dumb
// HttpListener (no ASP.NET, so no OpenAPI for NSwag); the BCL-native pipeline
// is JsonSchemaExporter (this file) → web/schema/api.schema.json (checked in,
// pinned by ApiSchemaTests) → json-schema-to-typescript in the web/ build.
// Adds no engine dependency (invariant 3): the exporter is System.Text.Json.
public static class ApiSchema
{
    /// The GUI-consumed response shapes — the four read endpoints' roots (their
    /// nested records inline) plus the discovery file a client bootstraps from.
    /// A new endpoint DTO joins this list or the GUI never learns its shape.
    private static readonly (string Name, Type Type)[] Types =
    [
        ("StatusDto", typeof(StatusDto)),
        ("PolicyDto", typeof(PolicyDto)),
        ("HarnessesDto", typeof(HarnessesDto)),
        ("HandlersDto", typeof(HandlersDto)),
        ("ApiDiscovery", typeof(ApiDiscovery)),
    ];

    /// One JSON-Schema document, `$defs` keyed by DTO name. Property names ride
    /// the SAME Web (camelCase) options ApiJson serializes with — the schema
    /// describes the wire, not the C#. Nullability comes from the NRT
    /// annotations (`string?` ⇒ nullable; unannotated value/reference types ⇒
    /// non-nullable), so the generated TS is honest about optional fields.
    public static string Export()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // The exporter requires an explicit resolver (ApiJson's options get
            // theirs lazily on first serialize); reflection is the engine's
            // norm — the host is JIT (ADR-0007 d3).
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            // Web defaults ACCEPT numbers-from-strings on read; this schema
            // describes what the server EMITS (always numeric), and leaving the
            // read leniency in would generate `string | number` TS for every int.
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        };
        var exporter = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true };
        var defs = new JsonObject();
        foreach (var (name, type) in Types)
            defs[name] = JsonSchemaExporter.GetJsonSchemaAsNode(options, type, exporter);
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "captAInHook management API v1",
            ["description"] =
                "GENERATED from the C# DTOs (dotnet/captainHook/Api/ApiDtos.cs + ApiDiscovery.cs) " +
                "by ApiSchema.Export — do not edit. Regenerate: " +
                "CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj " +
                "--filter ApiSchemaTests",
            ["$defs"] = defs,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
