# captAInHook management GUI (`web/`)

The browser UI for captAInHook, per **ADR-0008**. A separate React + Vite +
Zustand project whose **build output is committed to the repo's `ui/` dir** and
served **same-origin** by the daemon at `GET /ui` — forced by the API's Origin
gate (a second origin would be 403'd on every call).

**Node/npm is a dev-only build tool — never a runtime or deploy dependency.**
The built assets in `../ui/` are committed; deploying or *running* captAInHook
needs no Node. Only someone *modifying* the UI runs the build.

## Develop

```sh
npm install          # once
npm run dev          # vite build --watch → writes ../ui/ on every change
```

Then drive the daemon's own same-origin `http://127.0.0.1:<port>/ui` (open it
with `captainHook ui`, which hands off the bearer token in the URL fragment).
The dev loop is asset-rebuild + full page reload (ADR-0008 d7); Vite HMR on its
own origin is the reserved, env-gated escape hatch, not the default.

## Ship

```sh
npm run build        # tsc -b && vite build → ../ui/
git add ../ui        # commit the rebuilt assets alongside the source change
```

**Rebuild before every commit that touches `web/`** — the committed `ui/` can
drift from source otherwise. `/deploy` stages `../ui/` as the third artifact
beside the two executables.
