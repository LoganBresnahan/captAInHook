import { useState } from "react";
import { useStore } from "./store.ts";
import type { ExecEntry } from "./handlers.ts";
import {
  TEMPLATES, type Template, templateEntry, suggestedCommand, eventVerbs, effectLandsOn,
} from "./templates.ts";
import { TEMPLATE_SCRIPTS } from "./templateScripts.ts";

// The template gallery (ADR-0015 d3): curated starters that pre-fill the
// install form and hand the user the script to save. Client-side only — the
// API gains no verb that writes an executable, so the trust surface is
// unchanged: the user puts the file on disk, the GUI installs the entry, and
// the verbatim confirm still gates the write.
export function TemplateGallery({ onUse }: { onUse: (entry: ExecEntry) => void }) {
  const status = useStore((s) => s.status);
  const harnesses = useStore((s) => s.harnesses);
  const handlers = useStore((s) => s.handlers);
  const [open, setOpen] = useState<Template | null>(null);
  const [copied, setCopied] = useState(false);

  const verbs = eventVerbs(harnesses);
  const paths = { handlersPath: handlers?.path ?? null, shimPath: status?.shimPath ?? null };

  return (
    <div data-gallery>
      <ul className="gallery">
        {TEMPLATES.map((t) => (
          <li key={t.id} className="template" data-template={t.id}>
            <div className="template-head">
              <strong>{t.title}</strong>
              <span className="event-cap" data-template-effect={t.effect}>{t.effect}</span>
            </div>
            <p className="muted">{t.blurb}</p>
            <p className="template-events">
              {t.entry.events.map((ev) => (
                <span key={ev} className="event-cap" data-template-event={ev}>
                  {ev}
                  <span className="muted">
                    {" · "}
                    {(verbs[ev] ?? []).length > 0 ? (verbs[ev] ?? []).join(" ") : "no loop effects"}
                  </span>
                </span>
              ))}
              <span className="muted">{t.entry.mode}</span>
            </p>
            {t.entry.events.some((ev) => !effectLandsOn(verbs, ev, t.effect)) && (
              <p className="bad" data-template-warning>
                This harness does not list <code>{t.effect}</code> for that event — the
                daemon will accept the entry, but the effect will not land.
              </p>
            )}
            <p className="template-why muted">{t.why}</p>
            <div>
              <button data-template-use={t.id} onClick={() => { setOpen(t); setCopied(false); }}>
                Use this template
              </button>
            </div>
          </li>
        ))}
      </ul>

      {open !== null && (
        <TemplateDetail
          template={open}
          paths={paths}
          copied={copied}
          onCopy={async () => {
            try {
              await navigator.clipboard.writeText(TEMPLATE_SCRIPTS[open.id] ?? "");
              setCopied(true);
            } catch {
              setCopied(false);   // clipboard denied (no user gesture / permissions): the <pre> is still selectable
            }
          }}
          onClose={() => setOpen(null)}
          onUse={() => { onUse(templateEntry(open, paths)); setOpen(null); }}
        />
      )}
    </div>
  );
}

// The script is shown IN FULL, deliberately: the user is about to make this
// file executable and let it run on their own hooks, and ADR-0011's doctrine is
// that they read it first. A collapsed "preview" would be theatre.
function TemplateDetail({ template, paths, copied, onCopy, onClose, onUse }: {
  template: Template;
  paths: { handlersPath: string | null; shimPath: string | null };
  copied: boolean;
  onCopy: () => void;
  onClose: () => void;
  onUse: () => void;
}) {
  const path = suggestedCommand(template, paths);
  return (
    <div className="template-detail" data-template-detail={template.id}>
      <h4>{template.title}</h4>
      <ol className="template-steps">
        <li>
          Copy the script and save it as{" "}
          {path !== null
            ? <code data-template-path>{path}</code>
            : <>an absolute path of your choosing</>}
          , then <code>chmod +x</code> it. captAInHook never writes executables for
          you — you own what runs as you.
        </li>
        <li>Read it. It runs as you, on every one of those events.</li>
        <li>Install the entry below; the verbatim confirm shows exactly what will be written.</li>
      </ol>
      <div>
        <button data-template-copy onClick={onCopy}>{copied ? "Copied ✓" : "Copy script"}</button>{" "}
        <button data-template-install className="btn-primary" onClick={onUse}>
          Pre-fill the install form
        </button>{" "}
        <button onClick={onClose}>Close</button>
      </div>
      <p className="muted">
        Source: <code>{template.file}</code> — editing it in the repo changes this
        gallery on the next build.
      </p>
      <pre data-template-script>{TEMPLATE_SCRIPTS[template.id] ?? "(script unavailable)"}</pre>
    </div>
  );
}
