// dto-schema-codegen (ADR-0008 decision 6): derive src/api.gen.ts from the
// checked-in JSON Schema the engine exports (web/schema/api.schema.json). The
// C# DTOs are the single source of truth; this script is the second half of
// the pipeline. Runs as part of `npm run build` (and `npm run gen`); the
// output is committed so reviewers see type changes and the repo builds
// without a prior engine test run.
//
// Schema stale? Regenerate it first:
//   CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter ApiSchemaTests
import { compile } from "json-schema-to-typescript";
import { readFileSync, writeFileSync } from "node:fs";

const schemaUrl = new URL("../schema/api.schema.json", import.meta.url);
const doc = JSON.parse(readFileSync(schemaUrl, "utf8"));

const parts = [
  "/* GENERATED from web/schema/api.schema.json by scripts/gen-types.mjs — do not edit.",
  "   The C# DTOs (dotnet/captainHook/Api/ApiDtos.cs) are the source of truth;",
  "   regenerate with `npm run gen` (see the schema header for its own regen). */",
  "",
];

for (const [name, schema] of Object.entries(doc.$defs)) {
  parts.push(
    await compile(schema, name, {
      bannerComment: "",
      additionalProperties: false,
    }),
  );
}

writeFileSync(new URL("../src/api.gen.ts", import.meta.url), parts.join("\n"));
console.log(`gen-types: wrote src/api.gen.ts (${Object.keys(doc.$defs).length} roots)`);
