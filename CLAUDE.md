# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

KindleKeep API is the .NET 10 backend for an uptime/security-monitoring SaaS (free-tier focused). It is compiled with **Native AOT**, which is the single biggest constraint on how code in this repo must be written — see below before adding endpoints, DTOs, or DB access. Full product/architecture spec (frontend included) lives in `ARCHITECTURE.md`; the frontend is a separate polyrepo (`kindlekeep-app`).

## Commands

```bash
dotnet build                          # build
dotnet run                            # run (http://localhost:5247, launch profile "http")
dotnet watch run                      # run with hot reload

# EF Core migrations (dotnet-ef is a local tool, see dotnet-tools.json)
dotnet tool restore
dotnet ef migrations add <Name> -o Infrastructure/Data/Migrations
dotnet ef database update

# After changing an entity or the model, regenerate the compiled model used by AddDbContextPool
# (see "Native AOT constraints" below) — required, not optional, or the app runs against a stale model:
dotnet ef dbcontext optimize --output-dir Infrastructure/Data/CompiledModels --namespace KindleKeep.Api.Infrastructure.Data.CompiledModels

# Native AOT publish (what Docker/Render actually ships)
dotnet publish kindlekeep-api.csproj -c Release -r linux-musl-x64 --no-restore
```

There is no test project in this repo currently.

## Architecture

### Native AOT constraints (read this before touching data access or JSON)

The project builds with `<PublishAot>true</PublishAot>`. Two things follow from that everywhere in the codebase:

1. **No reflection-based JSON.** All JSON-serialized types (entities, DTOs, OAuth provider payloads, ProblemDetails, etc.) must be registered as `[JsonSerializable(typeof(...))]` on `AppJsonSerializerContext` in `Program.cs`. If you add a new type that crosses an HTTP or SignalR boundary, add it there or serialization will fail at runtime (not compile time).
2. **Dual data-access pattern.** EF Core is used with a *precompiled model* (`Infrastructure/Data/CompiledModels/`, wired via `options.UseModel(KindleDbContextModel.Instance)` in `Program.cs`) because EF's normal reflection-based model building isn't AOT-safe. But EF/LINQ is still avoided on hot paths — `WatcherEngine` and the OAuth callback in `AuthEndpoints` bypass EF entirely and use raw `NpgsqlDataSource`/`NpgsqlCommand` with explicit `NpgsqlDbType` parameters instead. When adding new hot-path or bulk data access, prefer the raw ADO.NET pattern already established in those two files over LINQ; use `KindleDbContext` (see `AlertManager`) for simpler, low-frequency CRUD. Whenever an entity or the EF model shape changes, the compiled model must be regenerated (`dotnet ef dbcontext optimize`, see Commands) — it does not update itself.

### Request pipeline (`Program.cs`)

Minimal APIs only, no controllers. Each feature area is a static class in `API/Endpoints/*Endpoints.cs` exposing a `MapXEndpoints(this IEndpointRouteBuilder)` extension, registered in `Program.cs` (`MapAuthEndpoints`, `MapUserEndpoints`, `MapMonitorEndpoints`, `MapIncidentEndpoints`, `MapVaultEndpoints`). Follow that pattern for new endpoint groups.

Auth is custom: OAuth2 (GitHub/Google/GitLab) is hand-rolled in `AuthEndpoints.cs` (not `AddAuthentication().AddGitHub()` etc.) — each provider does its own token exchange + profile fetch, then upserts a `User` row via raw ADO.NET and issues a first-party JWT via `Infrastructure/Identity/TokenService`. JWT auth is validated by the standard `JwtBearer` middleware; SignalR (`/hubs/pulse`) can't use the `Authorization` header, so `Program.cs` pulls the token from the `access_token` query string in `OnMessageReceived` for that path specifically.

Unhandled exceptions go through `API/Infrastructure/Exceptions/GlobalExceptionHandler.cs`, which serializes `ProblemDetails` directly to the response stream via the AOT-safe `AppJsonSerializerContext` (not `Results.Problem`).

### Background services

- `Infrastructure/BackgroundServices/WatcherEngine.cs` — the core monitoring loop. Runs on a configurable interval (`Watcher:IntervalMinutes`), for each active `MonitorTarget`: probes the URL (3-attempt retry), streams progress to the client over SignalR (`PulseHub`, group = monitor ID) as it goes, computes cold-start/latency metrics, grades security headers (CSP/HSTS/XFO/nosniff → A–F), writes `UptimeLogs`/`SecurityAudits` via raw ADO.NET, auto-quarantines a target after 3 consecutive failures, and pushes `ReceivePulse` to the owning user's SignalR group. This is the file to read to understand the monitoring domain logic end to end.
- `Infrastructure/BackgroundServices/PruningService.cs` — periodic cleanup of old log data (`Pruning:IntervalHours` / `Pruning:RetentionDays`).
- `Infrastructure/Alerting/AlertManager.cs` — dedupes alerts via a SHA-256 fingerprint of `{monitorId}:{type}:{status}` kept in an in-memory `ConcurrentDictionary` plus an `AlertIncidents` table (so restarts don't lose open-incident state); dispatches Discord webhooks for uptime changes and Resend emails for security-grade regressions.

### Configuration

Config values are read via `IConfiguration` first, falling back to `KK_`-prefixed environment variables (e.g. `KK_DATABASE_URL`, `KK_JWT_KEY`, `KK_ALLOWED_ORIGINS`, `KK_WEBHOST_URL`) — this dual lookup is intentional so `appsettings.json`/user-secrets work locally while Render/production uses env vars only. Follow this pattern (`configuration["X"] ?? Environment.GetEnvironmentVariable("KK_X") ?? throw ...`) for any new required setting.

### Deployment

`DockerFile` is a two-stage Alpine build: SDK image restores/publishes as `linux-musl-x64` Native AOT, then copies just the native binary into a minimal `runtime-deps` image. Backend deploys to Render; keep-alive is handled externally (`/api/stay-awake` endpoint is pinged by UptimeRobot to prevent Render's free-tier sleep).

## Companion repo

Frontend lives at ../kindlekeep-app (React 19 + TypeScript, Vite, Zustand, TanStack Query).
It consumes this API two ways: REST (via axios, see src/lib/axios.ts there) and SignalR
(hub at API/Hubs/PulseHub.cs, consumed by src/features/monitors/hooks/useSignalR.ts there).

Type contracts to keep in sync when editing either side:
- Core/DTOs/*.cs <-> ../kindlekeep-app/src/features/monitors/types/monitor.types.ts
- PulseUpdate.cs (MonitorId, NewStatus, LatencyMs) <-> ReceivePulse handler on the frontend
  (SignalR's default JSON protocol camelCases these automatically, this is expected, not a bug)
- PulseHub.cs methods (SubscribeToMonitor, UnsubscribeFromMonitor) <-> hub.invoke() calls in
  ../kindlekeep-app/src/features/monitors/hooks/useSignalR.ts

## Rules
- Never run git push, git commit --amend, or git rebase without explicit instruction.
- Any change to a DTO shape or hub method signature must be checked against the other repo
  before considered complete.
