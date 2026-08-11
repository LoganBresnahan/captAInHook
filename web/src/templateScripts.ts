import decideSrc from "../../examples/payloads/starter-decide.sh?raw";
import injectSrc from "../../examples/payloads/starter-inject.sh?raw";
import sideEffectSrc from "../../examples/payloads/starter-side-effect.sh?raw";
import llmSrc from "../../examples/payloads/starter-llm.sh?raw";
import retrieverSrc from "../../examples/payloads/retriever.sh?raw";
import memorySrc from "../../examples/payloads/memory.sh?raw";

// The ONE file that inlines payload text (ADR-0015 d3 + N3). Vite's `?raw`
// reads the scripts at BUILD time from `examples/payloads/`, so the gallery
// ships exactly the bytes the repo's examples contain — no second copy to
// drift, no runtime fetch, no generation step. The import deliberately crosses
// `web/`'s boundary into the repo: editing a starter script changes the shipped
// GUI on the next build, which the examples README states.
//
// Kept apart from templates.ts so the metadata and mapping stay importable by
// `node --test` (zero dependencies — Node cannot resolve a `?raw` suffix).
export const TEMPLATE_SCRIPTS: Record<string, string> = {
  "starter-decide": decideSrc,
  "starter-inject": injectSrc,
  "starter-side-effect": sideEffectSrc,
  "starter-llm": llmSrc,
  retriever: retrieverSrc,
  memory: memorySrc,
};
