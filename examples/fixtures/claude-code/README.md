# Claude Code tool-use envelopes: real captures

What an exec-wire payload actually receives on stdin for `PreToolUse` and
`PostToolUse` under Claude Code. Each file holds one tool call: its
`PreToolUse` envelope, then its `PostToolUse` envelope, one JSON line each,
verbatim as the engine wrote them. Captured 2026-10-14 from a live session on
**Claude Code 2.1.299**, through the deployed engine at the commit that added
`event.harness` (ADR-0010 d2, 2026-10-14 amendment).

| file | tool | note |
| --- | --- | --- |
| `bash.jsonl` | `Bash` | a two-line command |
| `write-create.jsonl` | `Write` | creates a new file |
| `write-update.jsonl` | `Write` | overwrites an existing file |
| `edit.jsonl` | `Edit` | |
| `read-text.jsonl` | `Read` | a text file |
| `read-notebook.jsonl` | `Read` | a `.ipynb` |
| `notebook-edit.jsonl` | `NotebookEdit` | |

These are the host's bytes, and the host changes them between versions. When
a payload breaks on a new Claude Code, capture again rather than editing these.
`ClaudeCodeFixtureTests` pins that they are what the current encoder writes
and that the facts below hold.

## What they show

- **The envelope.** `{"v":1,"dispatchId":…,"event":{"type","harness","sessionId","cwd","payload"}}`.
  `event.harness` is `"claude-code"`; `payload` is Claude Code's raw hook JSON.
- **Every payload, Pre and Post:** `session_id`, `transcript_path`, `cwd`,
  `scratchpad_dir`, `prompt_id`, `permission_mode`, `effort`,
  `hook_event_name`, `tool_name`, `tool_use_id`, `tool_input`. Post adds
  `tool_response` and `duration_ms`.
- **`tool_use_id` is in BOTH events, with the same value for one call.** It
  is the per-call key that joins a `PreToolUse` to its `PostToolUse`.
- **A turn identifier exists: `prompt_id`.** It was the same on every tool
  call within the one user prompt captured here. That it changes on the next
  prompt is assumed from the name, not shown by these files.
- **The file path field:** `tool_input.file_path` for `Write`, `Edit` and
  `Read`; `tool_input.notebook_path` for `NotebookEdit`. `Bash` has no path,
  only `tool_input.command` (+ `description`).
- **`tool_response` by tool:**
  - `Write`: `type` (`"create"` | `"update"`), `filePath`, `content`,
    `structuredPatch` (empty on create), `originalFile` (`null` on create,
    the whole prior text on update), `userModified`.
  - `Edit`: `filePath`, `oldString`, `newString`, `originalFile` (the whole
    prior text), `structuredPatch`, `userModified`, `replaceAll`. No `type`.
  - `Read`: `type` (`"text"` | `"notebook"`) and `file`: `{filePath, content,
    numLines, startLine, totalLines}` for text, `{filePath, cells}` for a
    notebook.
  - `NotebookEdit`: snake_case: `new_source`, `old_source`, `cell_type`,
    `language`, `edit_mode`, `cell_id`, `error`, `notebook_path`,
    `original_file`, `updated_file`.
  - `Bash`: `stdout`, `stderr`, `interrupted`, `isImage`, `noOutputExpected`.
- **Size:** an envelope carries whole file contents (`tool_input.content`,
  `originalFile`, `updated_file`), so on a large file it is large. No hash
  of anything is included; a payload that needs one hashes the disk itself.
