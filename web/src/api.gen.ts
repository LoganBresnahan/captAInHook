/* GENERATED from web/schema/api.schema.json by scripts/gen-types.mjs — do not edit.
   The C# DTOs (dotnet/captainHook/Api/ApiDtos.cs) are the source of truth;
   regenerate with `npm run gen` (see the schema header for its own regen). */

export interface StatusDto {
  version: string;
  pid: number;
  uptimeMs: number;
  active: number;
  served: number;
  backgroundPending: number;
  openStreams: number;
}

export interface PolicyDto {
  state: string;
  error: string | null;
  policy: {
    default: string;
    rules: {
      event: string | null;
      handler: string | null;
      project: string | null;
      session: string | null;
      decision: string;
    }[];
  } | null;
  raw: string | null;
  path: string | null;
  etag: string | null;
}

export interface HarnessesDto {
  harnesses: {
    name: string;
    responseAdapter: string;
    request: {
      eventNameField: string;
      sessionIdField: string;
      cwdField: string;
    };
    events: {
      [k: string]: string[];
    };
    install: unknown;
  }[];
}

export interface HandlersDto {
  handlers: {
    event: string;
    name: string;
    failMode: string;
    generation: number;
    dead: boolean;
  }[];
}

export interface ApiDiscovery {
  port: number;
  token: string;
  pid: number;
  version: string;
}
