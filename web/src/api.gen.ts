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
  shimPath: string | null;
}

export interface PolicyDto {
  state: string;
  error: string | null;
  policy: {
    default: string;
    rules: PolicyRuleDto[];
  } | null;
  raw: string | null;
  path: string | null;
  etag: string | null;
}
export interface PolicyRuleDto {
  event: string | null;
  handler: string | null;
  project: string | null;
  session: string | null;
  decision: string;
}

export interface HarnessesDto {
  harnesses: HarnessDto[];
}
export interface HarnessDto {
  name: string;
  responseAdapter: string;
  request: HarnessRequestDto;
  events: {
    [k: string]: string[];
  };
  install: unknown;
}
export interface HarnessRequestDto {
  eventNameField: string;
  sessionIdField: string;
  cwdField: string;
}

export interface HandlersDto {
  handlers: HandlerDto[];
  source: string;
  error: string | null;
  path: string | null;
  expected: ExpectedHandlerDto[];
  raw: string | null;
  etag: string | null;
}
export interface HandlerDto {
  event: string;
  name: string;
  failMode: string;
  generation: number;
  dead: boolean;
  childState: string | null;
  childPid: number | null;
}
export interface ExpectedHandlerDto {
  name: string;
  events: string[];
  mode: string | null;
  failMode: string | null;
  registered: boolean;
  skipReason: string | null;
}

export interface MailDto {
  dir: string;
  chain: MailChainDto;
  since: number;
  sinceAligned: boolean;
  frontier: number;
  lines: MailLineDto[];
  cursors: MailCursorDto[];
  presence: MailPresenceDto[];
}
export interface MailChainDto {
  ok: boolean;
  head: string | null;
  gen: number;
  lines: number;
  bytes: number;
  dirMode: string | null;
  fileMode: string | null;
  faults: MailChainFaultDto[];
}
export interface MailChainFaultDto {
  offset: number;
  kind: string;
  detail: string;
}
export interface MailLineDto {
  offset: number;
  bytes: number;
  terminated: boolean;
  hash: string;
  envelope: {
    id: string;
    ts: string;
    from: MailSenderDto;
    to: string;
    kind: string;
    topic: string;
    priority: string;
    inReplyTo: string | null;
    ttlDeliveries: number;
    body: string;
    prev: string | null;
  } | null;
  errors: string[];
}
export interface MailSenderDto {
  agent: string;
  harness: string;
  session: string | null;
}
export interface MailCursorDto {
  role: string;
  session: string | null;
  gen: number;
  head: string | null;
  frontier: number;
  deliveries: number;
  lastDeliveredId: string | null;
  reanchored: boolean;
  reanchorReason: string | null;
  pending: MailPendingDto[];
  expired: MailPendingDto[];
  skippedMalformed: number;
}
export interface MailPendingDto {
  offset: number;
  id: string;
  priority: string;
  ttlDeliveries: number;
  seenAt: number | null;
  opportunities: number;
}
export interface MailPresenceDto {
  session: string;
  roles: string[];
  lastDispatchAgeMs: number | null;
}

export interface ApiDiscovery {
  port: number;
  token: string;
  pid: number;
  version: string;
}
