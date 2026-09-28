# Instella.Server

ASP.NET Core (net10.0) package distribution + update server: content-addressed blob storage, durable
upload sessions, durable background patch generation, publisher-signed release manifests relayed to
clients, and a Blazor Server admin UI. Attribute-routed MVC controllers (not minimal API) + Razor
Components. SQLite via EF Core migrations; local-disk or S3-compatible blob storage. Deployment guide:
[docs/server-deployment.md](../../docs/server-deployment.md).

## Architecture

```
Api/*Controller → Services → Data (AppDbContext, SQLite) → Storage (IStorageProviderAccessor → IStorageProvider)
Auth: cookie "AdminCookie" (UI) + "ApiKey" bearer scheme (API)     Hosted: PatchJobWorker, OrphanSweeper
Components/ (Blazor admin UI, interactive server)                    Wire contract: Instella.Core.Wire (ApiRoutes, DTOs)
```

DI (`Program.cs`):

| Lifetime | Registrations |
|---|---|
| Singleton | `SecretProtector`, `PendingSecondFactorStore`, `SetupTokenService(configDir)`, `ServerConfigDirectory(configDir)`, `IStorageProviderAccessor→StorageProviderAccessor`, `IRateLimitService→RateLimitService`, `SecurityEventThrottle`, `IpBanCache` |
| Transient | `IStorageProvider` = `accessor.Current` (rebuilt after settings change, no restart) |
| Scoped | `PackageService, DownloadPageService, AuthService, DownloadAccess, DownloadTokenService, DiffService, StorageSettingsService, ContentStorageService, UploadService, ISecurityLogService→SecurityLogService, IIpBanService→IpBanService` |
| Hosted | `PatchJobWorker`, `OrphanSweeper` |
| Other | `AddProblemDetails`, `AddMemoryCache`, `AddCascadingAuthenticationState`, health check `DatabaseHealthCheck` ("database"), rate limiter policies `downloads` / `api` / `auth`, `Configure<UploadLimits>("Upload")`, Data Protection, controllers with `WireJsonContext` first in the JSON resolver chain |

No `ManifestService`/`DownloadService`: download logic is in `DownloadController` + `PackageService`/`ContentStorageService`.
`ContentLocks` is not registered: `ContentLocks.Shared` is one static instance per process.

Pipeline order: `UseForwardedHeaders` (only if proxies configured) → `Migrate()` + setup-token check →
exception handlers (non-Development) → status-code pages (UI only) → HSTS/HTTPS redirect (unless
`Server:TlsTerminatedByProxy`) → static files → routing → `UseRateLimiter` → authN → authZ →
`UseAntiforgery` → `/healthz`, controllers, Razor components.

## Configuration

**Config directory** (holds `instella.db`, `keys/`, `setup-token`, optional `appsettings.json`), first hit wins:
`Instella:ConfigDir` setting → `INSTELLA_CONFIG_DIR` env → `/config` (Linux, if it exists) → content root.
`Instella:ConfigDir` is read before the config-dir file is loaded, so it can only come from the built-in
`appsettings.json`, `Instella__ConfigDir` env or the command line.

**Source precedence** (low → high): built-in `appsettings.json` / `appsettings.{Env}.json` → unprefixed env
vars (`Section__Key`) → command line → `{configDir}/appsettings.json` (only when configDir ≠ content root,
`reloadOnChange`) → `INSTELLA_`-prefixed env vars (`INSTELLA_Database__ConnectionString`).

| Key | Default | Read by |
|---|---|---|
| `Database:ConnectionString` | `Data Source={configDir}/instella.db` | `Program.cs` |
| `ReverseProxy:KnownProxies` / `KnownNetworks` | `[]` | forwarded headers (IPs / CIDRs) |
| `Server:TlsTerminatedByProxy` | `false` (Dockerfile: `true`) | skips HSTS + HTTPS redirect |
| `Downloads:PermitsPerMinute` / `Api:PermitsPerMinute` / `Auth:PermitsPerMinute` | 600 / 300 / 20 | rate-limit policies `downloads` / `api` / `auth` (per client address) |
| `Retention:SecurityEventDays` / `Retention:DownloadLogDays` | 90 / 365 | `OrphanSweeper.ApplyRetentionAsync` (hourly, batches of 10 000) |
| `Upload:MaxFileBytes` / `MaxFilesPerSession` / `MaxPathLength` / `MaxPathSegments` | 2 GiB / 20 000 / 260 / 32 | `UploadLimits` |
| `Diff:Enabled` / `Diff:MaxPatchRatio` / `Diff:MaxFileBytes` | `true` / 0.9 / 256 MiB | `DiffService` (IConfiguration, not the DB); a changed file over `MaxFileBytes` on either side is shipped whole in the patch, never diffed |
| `Storage:Local:BasePath` | `./packages` (Dockerfile: `/packages`) | `StorageProviderAccessor`, **only while no `ServerSettings` row exists** |

There is no `Server:BaseUrl`, `Storage:Provider` or `Storage:S3:*` setting (the shipped appsettings files do
not have them): S3 is configured in the admin Settings page and stored in the `ServerSettings`
row. Patch generation is configured only by the `Diff:*` configuration keys (`ServerSettings` has no diff
columns).

## Schema and migrations

- Startup runs `db.Database.Migrate()`. No `EnsureCreated`, no raw SQL.
- Baseline `Migrations/20260927011420_InitialCreate`, then `20260928162942_ReleaseApproval` (renames
  `VersionBuilds.IsDraft` to `State`, since 0/1 = Published/Draft; adds `PublishAfter`, `UploadedByApiKeyId`,
  `UploadedByKeyName`, `Packages.ReleaseApproval`/`ReleaseDelayMinutes`, `ApiKeys.CanApproveReleases`; `Down` maps
  Pending to Draft) (+ `AppDbContextModelSnapshot`). `Integration/MigrationDataTests` migrates old rows.
  `DesignTimeDbContextFactory` serves `dotnet ef`. `verify.ps1 -Stage Migrations` fails on
  `has-pending-model-changes`.
- Every schema change is a new migration; released migrations are never edited. Before 1.0 a release
  may still require an empty database (its CHANGELOG says so): recreate it (empty `/config` + `/packages`).
- A database without `__EFMigrationsHistory` (created by `EnsureCreated`) is not upgradable:
  `Migrate()` tries to create existing tables and startup fails.

## Entities (20 DbSets, PKs `long` unless noted)

| Entity (DbSet) | Key fields / notes |
|---|---|
| `Package` | PackageId (unique, ≤100), DisplayName, Description, IconPath?, DownloadAccessMode, **ReleaseApproval** (`Automatic`/`Delayed`/`Required`, default Automatic), **ReleaseDelayMinutes** (default 1440, 10..43200) → Versions, ApiKeys, PublisherKeys |
| `PackageVersion` | PackageId FK (cascade), VersionString (canonical `AppVersions` form, `1.3` → `1.3.0`, unique per package), VersionKey (`AppVersions.ToSortKey`, 43 chars, set by the VersionString setter; index `(PackageId, Channel, VersionKey)`), Channel (string ≤ 32, default `stable`), Changelog, ReleasedAt (set at creation, and again when a draft becomes the version's first published build; never orders versions), IsDeprecated → Builds |
| `PackageChannel` | PackageId FK (cascade), Name (unique per package), PinnedVersionId? (FK, `SetNull`), CreatedAt. Created by the first version on the channel (upload completion) |
| `DownloadToken` | PackageId FK (cascade), Name ≤ 100, TokenHash (SHA-256 hex, unique), DisplayPrefix (8 chars after `idt_`), CreatedAt, ExpiresAt?, LastUsedAt?, IsRevoked |
| `VersionBuild` | VersionId FK (cascade), OS, Architecture (unique per version), TotalSize, ManifestHash, **ReleaseManifestBytes? / ReleaseSignature? / ReleaseKeyId?** (signed release, stored verbatim), **State** (`BuildState`: Published = 0, Draft = 1 (unsigned), Pending = 2 (signed, held by release approval); every client route requires Published; index `(State, PublishAfter)`), **PublishAfter?** (Delayed: when the worker publishes it), **UploadedByApiKeyId? / UploadedByKeyName?** (no FK; the self-approval rule), **UploadedAt**, DownloadCount, PatchDownloadCount → Files, Installers |
| `BuildInstaller` | BuildId FK (cascade), Kind (`online`/`offline`, one per build), FileName (the upload's name, served as-is), ContentHash FK→StoredFile (counted like a `BuildFile` reference), Size, DownloadCount |
| `BuildFile` | BuildId FK (cascade), RelativePath (unique per build), ContentHash FK→StoredFile (**Restrict**), Size |
| `StoredFile` | ContentHash (PK, string), Size, StoragePath, ReferenceCount, **PendingSince?** (indexed), FirstUploadedAt |
| `BuildPatch` | FromBuildId (cascade) / ToBuildId (restrict), unique pair, PatchSize, PatchHash, StoragePath, ManifestJson, GeneratedAt |
| `PackagePublisherKey` | PackageId FK (cascade), KeyId (≤16, unique per package), PublicKey (base64 ECDSA P-256), Label?, AddedAt |
| `UploadSessionRecord` (`UploadSessions`, table `UploadSessions`) | Id (Guid PK), ApiKeyId?, PackageDbId, PackageId, Version, Channel, OS, Architecture, CreatedAt, ExpiresAt (indexed) |
| `UploadSessionFile` (`UploadSessionFiles`) | SessionId FK (cascade), RelativePath (unique per session), ContentHash (indexed), Size, Deduplicated |
| `UploadSessionInstaller` (`UploadSessionInstallers`) | SessionId FK (cascade), Kind (one per session; a re-upload replaces), FileName, ContentHash, Size, Deduplicated; becomes a `BuildInstaller` at completion |
| `ApiKey` | KeyHash (SHA-256 hex, unique), Name, Scope, PackageId? (cascade), CanUpload (default true), CanDownload (default false), CanManageVersions (default false), **CanApproveReleases** (default false; never together with CanUpload: `CreateApiKeyAsync` throws), IsRevoked, LastUsedAt |
| `AdminUser` | Username (unique), PasswordHash (`PasswordHasher<AdminUser>`), TotpSecret? (**protected**), TotpEnabled, **LastTotpTimeStep** |
| `ServerSettings` | singleton row: StorageProvider, LocalBasePath, S3Preset/Endpoint/Bucket/AccessKey/**S3SecretKey (protected)**/Region/UrlExpiryMinutes/AllowInsecureEndpoint |
| `PendingPatchJob` | FromBuildId? (set null) / ToBuildId (cascade), Status, Attempts, LastError, NextAttemptAt, **LeaseExpiresAt?**, RowVersion (uint, concurrency token) |
| `DownloadLog` | BuildId FK (cascade), IsPatch, IPHash (first 16 hex of SHA-256(ip)), UserAgent?, Timestamp |
| `IpBan` | IpAddress (unique, ≤45), Reason, CreatedByAdminId FK (restrict) |
| `SecurityEvent` | EventType, IpAddress, Username?, ApiKeyName?, PackageId?, Details?, Timestamp (audit log) |

Enums: `Models`: `TargetOS{Windows,Linux,MacOS}`, `Architecture{X64,X86,ARM64,ARM32}`. Channels are strings (`ChannelNames`).
`Data.Entities`: `ApiKeyScope{Admin,Package}` (Admin = all packages), `DownloadAccessMode{Open,MasterKeyRequired,PackageKeyRequired}`,
`StorageProviderType{Local,S3}`, `S3ProviderPreset{Custom,AwsS3,MinIO,CloudflareR2,BackblazeB2,DigitalOceanSpaces}`,
`PatchJobStatus{Pending,InProgress,Completed,Failed,Dead}`, `BuildState{Published,Draft,Pending}`, `ReleaseApproval{Automatic,Delayed,Required}`,
`SecurityEventType` (login/upload/api-key/hash-mismatch/ban/download-denied; appended, stored as int: `ReleasePending`, `ReleaseApproved`,
`ReleaseRejected`, `ReleaseAutoPublished`, `ReleaseAutoPublishBlocked`, `ReleaseApprovalChanged`, `PublisherKeyAdded`, `PublisherKeyRemoved`,
`DraftSigned`; these are never throttled, `SecurityEventThrottle.IsNeverThrottled`).
Wire names come from `Instella.Core.Wire.PlatformStrings` via `Models/PlatformMapping`: os `windows|linux|macos`
(accepts `win`, `osx`), arch `x64|x86|arm64|arm32` (accepts `amd64`, `i386`, `i686`, `aarch64`, `arm`, `armv7`).

## API surface

All API routes are `api/v1/` + a template constant from `Instella.Core.Wire.ApiRoutes` (shared with clients;
the contract tests pin it). Errors are `ApiError { error }` JSON; unhandled exceptions are ProblemDetails.

**DownloadController** — `[EnableRateLimiting("downloads")]`; access per package via `ValidateDownloadAccessAsync`.

| Method | Route | Notes |
|---|---|---|
| GET | `check-update?packageId=&currentVersion=&os=&arch=&channel=` | → `CheckUpdateResponse {updateAvailable, version, changelog, fullSize, patchAvailable, patchSize, patchSha256, mandatory, release?}`; `mandatory` is **reserved**: always `false` in 1.x, no column, clients ignore it. Latest = `PackageService.GetLatestVersionAsync` (below). Versions compare canonically (`AppVersions`); the patch is looked up from the canonical current version. Unknown package or channel / no build / not newer → `{updateAvailable:false}`. Access check applies once a version exists. |
| GET | `release/{packageId}/{version}/{os}/{arch}` | → `SignedRelease {manifest (base64 of stored bytes), signature, keyId}`; 404 if the build was uploaded unsigned |
| GET | `download/{packageId}/{version}/{os}/{arch}` | full build as ZIP streamed straight into the response (`ZipArchive` on `Response.Body`); every blob's existence + `SafePath` of every entry checked **before** the first byte (500 otherwise); counts a download |
| GET | `download/{packageId}/{version}/{os}/{arch}/file/{**path}` | one file; S3 → 302 to presigned URL (`S3UrlExpiryMinutes`, default 60), else streamed |
| GET | `patch/{packageId}/{fromVersion}/{toVersion}/{os}/{arch}` | patch ZIP; S3 → presigned redirect, else streamed; counts a patch download |
| GET | `patch-manifest/{packageId}/{fromVersion}/{toVersion}/{os}/{arch}` | `BuildPatch.ManifestJson` |

**PackagesController** — reads under the download access rule (`Auth/DownloadAccess`, shared with
`DownloadController` and `InstallerController`: Open → anyone; otherwise a bearer key the permission matrix allows
to download (admin scope for `MasterKeyRequired`) or a download token. **Uniform 404**: no or invalid
credentials on a non-Open package get the route's unknown-package answer (404 `Package not found`, check-update's
"no update"), so private ids cannot be probed; only a valid credential without the permission gets 403. Each denial
is security-logged). Rate-limit policy `api`.

| Method | Route | Notes |
|---|---|---|
| GET | `packages` | `PackageSummary[]` (Open packages only; private ids are never listed) |
| GET | `packages/{packageId}` | `PackageSummary`; 404 unknown |
| GET | `packages/{packageId}/versions` | `VersionSummary[]` with `BuildSummary[]` (each with `installers[{kind, fileName, size}]`) |

**InstallerController** — same download access rule and rate limit as `DownloadController`.

| Method | Route | Notes |
|---|---|---|
| GET | `installer/{packageId}/{version}/{os}/{arch}/{kind}[?channel=]` | `kind` = `online`/`offline`; `version` = a version (canonicalised: `1.3` = `1.3.0`) or `latest` (the same "latest" as check-update, pin included, on the channel, default stable; if that build has no installer of the kind → 404, an older version's installer is never substituted). Counts `BuildInstaller.DownloadCount`; S3 → presigned redirect; else the file with its upload file name |

**UploadController** — `[Authorize(Policy="ApiKey")]`; every action goes through `ApiPermissions.Allows`
(401 no key, 404 unknown package, 403 missing permission).

| Method | Route | Notes |
|---|---|---|
| POST | `upload/start` | body `StartUploadRequest {packageId, version, os?, arch?, channel?}`; checked first: the version must parse with `AppVersions` (else 400 "invalid version '…': use numbers like 1.2.3 or 1.2.3.4"; `latest` too) and is stored canonically (`1.2` → `1.2.0`, echoed in the response); an invalid channel name → 400 (`ChannelNames.Rule`; `Beta` is stored as `beta`); the version already on another channel → 409 (`UploadConflictException`, checked again inside the completion transaction); package must exist (created in admin UI) and the build must not; → `{sessionId, packageId, version, os, arch}` |
| POST | `upload/{sessionId}/file?path=&sha256=` | raw body; body limit = `Upload:MaxFileBytes` (+ streaming counter); per-key+IP backoff `upload:{keyhash[..16]}@{ip}`; → `{stored, deduplicated, path, size, hash}` |
| GET | `drafts/{packageId}/{version}/{os}/{arch}` | draft build's unsigned manifest → `DraftResponse{manifest (base64), uploadedAt, changelog}`; 404 when there is no draft (Upload permission) |
| POST | `drafts/{packageId}/{version}/{os}/{arch}/publish` | body `SignedRelease`; manifest bytes must equal the stored draft bytes; with registered publisher keys the signature must verify against one; stores signature + key id and sets the state from the package's release approval (`ReleaseApprovalService.InitialState`: Published, or Pending with the delay starting now); sets `ReleasedAt` if published and it is the version's first published build; → `PublishDraftResponse{message, state, publishAfter}`; logs `DraftSigned` (+ `ReleasePending`) (Upload permission) |
| POST | `upload/{sessionId}/installer?kind=&fileName=&sha256=` | same limits; stored content-addressed (`UploadSessionInstaller`, one per kind, re-upload replaces); at completion becomes a `BuildInstaller` (one reference each). With a signed release, `installers` must equal the uploaded set (kind, fileName, sha256, size) |
| POST | `upload/{sessionId}/complete` | body `CompleteUploadRequest {changelog?, release?: SignedRelease, draftManifest?: base64}` (a draft: unsigned bytes checked like a release, build stored as Draft and hidden from every client route until published; release and draftManifest together → 400); creates version (if new) + build in one transaction, enqueues `PendingPatchJob` (also for Draft/Pending builds, so approval is instant); a non-draft build is Published or Pending per the package's release approval, and records the uploading key; → `{success, buildId, versionId, fileCount, totalSize, deduplicatedCount, state, publishAfter?}`; logs `UploadSuccess` (+ `ReleasePending`) |
| DELETE | `upload/{sessionId}` | cancel: deletes session rows only (content was never counted) |
| PUT | `packages/{packageId}/versions/{version}` | body `UpdateVersionRequest {changelog?, isDeprecated?}` (ManageVersions) |
| DELETE | `packages/{packageId}/versions/{version}` | delete version + all its builds (ManageVersions) |

Session calls also require `session.ApiKeyId == calling key` (403 otherwise). Package deletion and build
deletion exist only in the admin UI (no API route).

**ApprovalsController** — `[Authorize(Policy="ApiKey")]`, every action needs `ApproveReleases` (401/404/403 as above);
decisions by `ReleaseApprovalService` with `ReleaseActor.Key(key, ip)`.

| Method | Route | Notes |
|---|---|---|
| GET | `approvals/{packageId}` | drafts and pending builds, oldest first → `UnpublishedReleaseSummary[]` (version, channel, os, arch, state, uploadedAt, publishAfter?, keyId?, keyLabel?, uploadedBy?, manifestSha256?, fileCount, totalSize) |
| GET | `approvals/{packageId}/{version}/{os}/{arch}` | → `UnpublishedReleaseResponse{summary, manifest (base64), changelog}`; 404 when not Draft/Pending |
| POST | `approvals/{packageId}/{version}/{os}/{arch}/approve` | body `ApproveReleaseRequest{manifestSha256}`; Pending → Published. 409: not pending, manifest changed, signature no longer verifies against the currently registered keys; 403: the key uploaded this build |
| POST | `approvals/{packageId}/{version}/{os}/{arch}/reject` | body `RejectReleaseRequest{manifestSha256?, reason?}`; deletes a Draft or Pending build (and its version when empty); 409 for Published |

**AuthController** `[Route("api/auth")]`, rate-limit policy `auth` — admin form posts, not API contract (outside `ApiRoutes`), all
`[RequireAntiforgeryToken]` + class-level `[RejectInvalidAntiforgery]` (bad token → 400, not 500):
`POST login`, `POST totp`, `POST logout` (logout is POST-only).

**Other**: `GET /healthz` (anonymous; `DatabaseHealthCheck` = `Database.CanConnectAsync()`; 200 `Healthy` /
503 `Unhealthy`; no storage check). UI pages: `/` `/packages` `/api-keys` `/settings` `/docs` (Admin policy),
`/login` `/logout` `/setup` `/Error` `/not-found` `/download/{packageId}[?channel=]` (`[AllowAnonymous]`).
`/download/{packageId}` (`Pages/Download.razor`, `Services/DownloadPageService`) is the public download page:
only `Open` packages (a private or unknown id shows the same "not found"); non-deprecated versions on the channel
(default stable) that have installers, newest first, with changelogs; the newest version per platform links
through `installer/…/latest/…` (plus `?channel=` off stable), older ones by version. The admin package pane links
to it; the build pane lists the build's installers with download counts.

## API key permission matrix (`Auth/ApiPermissions.cs`)

`Allows(key, package, p) = !key.IsRevoked && (key.Scope == Admin || key.PackageId == package.Id) && flag(p)`

| Permission | Flag | Used by |
|---|---|---|
| `Upload` | `CanUpload` | `upload/*` (+ session must belong to the key) |
| `ManageVersions` | `CanManageVersions` (off by default; the CLI's `delete` explains a 403) | `PUT`/`DELETE packages/{id}/versions/{v}` (version canonicalised) |
| `Download` | `CanDownload` | `download/*`, `patch*`, `release/*`, `check-update` of a non-Open package |
| `ApproveReleases` | `CanApproveReleases` (never with `CanUpload`) | `approvals/*`; approve also refuses the build's own uploading key (`UploadedByApiKeyId`) |

Admin UI users (any admin; no roles yet) may approve and reject any build and change release approval (package pane).

Download access modes: `Open` → no key needed; `PackageKeyRequired` → `Allows(..., Download)`;
`MasterKeyRequired` → `Allows(..., Download)` **and** `Scope == Admin`.
**Download tokens**: a bearer value that `DownloadTokens.LooksLikeToken` (`idt_` + 43 base64url chars, 47 total;
Core, internal) takes the token path instead: `DownloadTokenService.IsValidForAsync` (lookup by hash, `IMemoryCache` 60 s,
dropped on revoke) → valid = found, not revoked, not expired, issued for this package; admits `PackageKeyRequired`
only (never `MasterKeyRequired`; `Open` ignores any credential). `LastUsedAt` is written at most once an hour
(`ExecuteUpdateAsync`). Created and revoked in the package pane ("Download tokens", shown once with a copy button);
security events `DownloadTokenCreated` / `DownloadTokenRevoked`. Denials are logged as
`DownloadDenied{NoKey,InvalidKey,InsufficientPermission}`. Keys: 64 random bytes, base64, shown once; stored as
lowercase-hex SHA-256; `LastUsedAt` updated on use; invalid bearer → `ApiKeyInvalid` event.

## Admin authentication

- Cookie scheme `AdminCookie`, cookie `Instella.Admin`: HttpOnly, SameSite=Strict, `SecurePolicy.Always`
  (`SameAsRequest` only in Development), 8 h sliding. Policies: `Admin` (cookie), `ApiKey`, `AdminOrApiKey`.
- UI is default-deny: `Pages/_Imports.razor` applies `[Authorize(Policy="Admin")]`; `Routes.razor` uses
  `AuthorizeRouteView` + `RedirectToLogin`. Pages opt out with `[AllowAnonymous]`.
- Password: `PasswordHasher<AdminUser>` (PBKDF2, rehash on login). Login keyed backoff `login:{user}@{ip}` (IP bans are the global middleware).
- **TOTP bound to the password step** (`Auth/PendingSecondFactor.cs`): a correct password for a TOTP user
  issues cookie `Instella.Pending2fa` = Data-Protection time-limited ticket `{userId|nonce}` (purpose
  `Instella.Auth.Pending2fa.v1`, 5 min, HttpOnly, SameSite=Strict). `POST totp` takes only `code`; missing/expired/
  tampered ticket → `/login?error=expired`. Limits: 5 attempts per ticket nonce (`IMemoryCache`) **and** the
  `login:` backoff. Replay: code accepted only if its time step > `AdminUser.LastTotpTimeStep` (window ±1 step).
- Antiforgery: `UseAntiforgery` + `<AntiforgeryToken />` in the login/TOTP/logout forms.

## Forwarded headers

- `ForwardedHeadersOptions`: `XForwardedFor | XForwardedProto`, `ForwardLimit = 1`, `KnownProxies` /
  `KnownIPNetworks` cleared then filled from `ReverseProxy:KnownProxies` (IPs) / `ReverseProxy:KnownNetworks` (CIDRs).
- `UseForwardedHeaders()` is registered **only when at least one proxy/network is configured**. Reason:
  the middleware treats *empty* known lists as "trust every peer", so with nothing configured it would let any
  client spoof `X-Forwarded-For` and escape rate limits/bans (test `SpoofedForwardedFor_DoesNotEscapeABan`).
- `HttpContextExtensions.GetClientIpAddress()` = `Connection.RemoteIpAddress` (rewritten only for a trusted
  peer). `X-Real-IP` and `X-Forwarded-Host` are not consulted. Without configured proxies, every client appears
  as the proxy's address.

## Transport

- `Server:TlsTerminatedByProxy=false` (default): `UseHsts()` (non-Development) + `UseHttpsRedirection()`.
- `true` (Dockerfile default): neither; the proxy does TLS/HSTS; scheme comes from `X-Forwarded-Proto` (trusted proxies only).
- Admin cookies are always `Secure` outside Development, so the admin UI needs HTTPS end to end at the browser.

## First-run setup token (`Services/SetupTokenService.cs`)

At startup, if no `AdminUser` exists: `EnsureToken()` writes 32 random bytes as hex to `{configDir}/setup-token`
(mode 0600 on Unix; reused if present) and logs the **path** (not the token). `/setup` step 1 requires it
(constant-time compare), creates the admin (password ≥ 8 chars), then `Consume()` deletes the file. Step 2
saves storage settings (`ServerSettings`). Once an admin exists, `/setup` redirects to `/login`.

## Data Protection and secrets at rest

- `AddDataProtection().SetApplicationName("Instella.Server").PersistKeysToFileSystem({configDir}/keys)`;
  `ProtectKeysWithDpapi()` on Windows only. On Linux the key XML is unencrypted; the directory's permissions are
  the protection: `Program.cs` sets the config dir and `keys/` to 0700 when looser (`ConfigDirectory.RestrictToOwner`,
  skipped on Windows and when the config dir is the content root; EPERM on a directory it does not own, such as
  a host bind mount, is a stderr warning, not fatal).
- Protects: admin auth cookies, antiforgery tokens, pending-2FA tickets, and via `SecretProtector`
  (purpose `Instella.Secrets.v1`): `ServerSettings.S3SecretKey` and `AdminUser.TotpSecret`. Services protect on
  write / unprotect on read (`StorageSettingsService`, `StorageProviderAccessor`, `AuthService`); not EF converters.
- Losing `keys/`: everyone signs in again, and the S3 secret + TOTP secrets can no longer be decrypted
  (`CryptographicException` on storage build, Settings page load and TOTP sign-in).

## Rate limiting and bans

| Mechanism | Scope | Behaviour |
|---|---|---|
| ASP.NET rate limiter, policies `downloads` / `api` / `auth` | `DownloadController` + `InstallerController` / `PackagesController` / `AuthController` | fixed window per `GetClientIpAddress()`, 600 / 300 / 20 per minute → 429 |
| Login backoff (`RateLimitService`) | login, TOTP | `login:{user}@{ip}` (exponential from the first failure) plus `login-ip:{ip}` (the same schedule after 20 failures across any usernames); expired entries cleaned at most once a minute; at most 100 000 keys (oldest evicted) |
| No existence oracle | `DownloadAccess.CheckAsync(…, unknown)` | no / invalid credentials on a non-Open package → the route's unknown-package answer (404, or check-update's "no update"); valid credential lacking permission → 403 |
| `SecurityEventThrottle` (singleton) | `SecurityLogService` | a repeat of (type, ip, package) within 60 s is counted, not written; the next written one gets "(+N similar)" |
| `RateLimitService` (singleton, in-memory, lost on restart) | keys `login:{user}@{ip}`, `upload:{keyhash16}@{ip}` | exponential backoff on failures: 1 s ×2 up to 900 s; reset 900 s after last failure; `Retry-After` on upload 429 |
| `IpBan` table (`IpBanService`, admin Settings → Security) | every request except `/healthz` | permanent; a middleware in `Program.cs` right after forwarded headers → 403 (`ApiError` JSON under `/api`); `IpBanCache` (singleton set, reloaded after 15 s or when `IpBanService` changes a ban) |

`/healthz` is not rate limited.

**One spelling per client address.** `Extensions/IpAddresses.Normalize`: `unknown` for none, the IPv4 form of
an IPv4-mapped IPv6 address, else the standard text (compact IPv6). `GetClientIpAddress()` returns it, so rate-limit
partitions, the login backoff, bans, security events and download-log hashes all agree. `IpBanService.AddBanAsync`
parses and normalises (`'abc' is not an IP address` otherwise); `IpBanCache` normalises what it loads.

## Uploads

- Sessions are DB rows (`UploadSessions`/`UploadSessionFiles`), 2 h idle timeout (`ExpiresAt` extended on each
  file), survive restarts. One build per (package, version, os, arch); starting a session for an existing build fails.
- `UploadFileAsync` validates before storage: `sha256` = 64 lowercase hex; `SafePath.TryNormalizeRelative`
  (only the normalized path is stored); ≤ `MaxPathLength` chars and ≤ `MaxPathSegments` segments;
  ≤ `MaxFilesPerSession` files; streams to a temp file with incremental SHA-256 and a byte counter
  (`MaxFileBytes`); hash mismatch → 400 + `HashMismatch` event. Re-uploading a path replaces its row.
- New content: `StoredFile` with `ReferenceCount = 0`, `PendingSince = now` (`StorePendingAsync`). Concurrent
  insert of the same hash (SQLite error 19) → detach and reuse the winner's row (identical content).
- `CompleteSessionAsync`: validate release (below) first, then one transaction: version (created or changelog
  updated), build, `BuildFile` rows, `ReferenceCount += n` grouped by hash with `PendingSince = null` (bulk
  `ExecuteUpdate`), delete session. References are counted **only** here, so cancel/abandon never touches counts.

## Publisher keys and signed release manifests

- Publisher keys (`PackagePublisherKey`) are registered per package in the admin UI (Packages → package
  properties); `KeyIds.FromPublicKey` validates base64 ECDSA P-256 and derives `KeyId`.
- On complete: if the package has keys, an unsigned upload is refused, and a `SignedRelease` is fully verified
  with `ReleaseVerifier.Verify` (signature against the registered keys + identity: app id, os, arch, exact
  version). With no registered keys a signed release is only parsed (not signature-checked). Either way the
  server checks `FormatVersion == 1`, app id, version, platform, channel, and that the manifest's
  `(path, sha256, size)` set **equals** the uploaded files.
- Stored verbatim (`ReleaseManifestBytes`, `ReleaseSignature`, `ReleaseKeyId`) and relayed by `release/...`
  and in `check-update`'s `release`. The server never signs; clients verify against keys compiled into the
  installer. See [docs/security-model.md](../../docs/security-model.md).
- Key add/remove take an `actor` and log `PublisherKeyAdded`/`PublisherKeyRemoved`. Removing the package's last key
  throws while release approval is not Automatic or a build is Pending.

## Release approval (`Services/ReleaseApprovalService.cs`, `DelayedReleaseWorker`)

- `InitialState(package, isDraft, now)`: draft → Draft; else Automatic → Published, Delayed → Pending with
  `PublishAfter = now + ReleaseDelayMinutes`, Required → Pending. Used by `CompleteCoreAsync` and `PublishDraftAsync`.
- Every transition is a conditional UPDATE on `State` (the compare-and-set token) with its `SecurityEvent` in the
  same transaction: approve and auto-publish `WHERE Id AND State = Pending` (+ `PublishAfter <= now` for the worker);
  reject first claims with `UPDATE … SET PublishAfter = NULL WHERE State <> Published` (holds SQLite's write lock),
  then `BuildPurger.PurgeBuildsAsync` (shared with `PackageService` deletes) and deletes the version if empty.
- Approve checks: Pending, `ManifestSha256(bytes)` equals the reviewed hash, the actor's key is not the uploader,
  and `ReleaseVerifier.Verify` against the keys registered **now**. `ReleasedAt` = now when it is the version's first
  published build.
- `SetReleaseApprovalAsync(package, approval, delay, actor)`: Delayed/Required need a registered key; never publishes;
  switching to Required cancels running timers (count in the `ReleaseApprovalChanged` entry).
- `DelayedReleaseWorker` (hosted): every 30 s (first pass at startup) up to 50 due builds, each via
  `AutoPublishAsync(id, now)` in its own scope. A build whose key is no longer registered gets `PublishAfter = null`
  and `ReleaseAutoPublishBlocked`. Internal `PublishDueAsync(now, ct)` for tests.
- Admin UI: package pane "Release approval" (setting, delay presets, "Awaiting a decision" list with Review →
  `OnSelectBuild`), build pane (state banner, key, uploader, manifest SHA-256, Approve/Reject behind an inline
  confirmation, actor = `User.Identity.Name`), tree badges (`PendingCount`), dashboard notice, API key checkbox.

## Deletion (`PackageService`)

`DeleteVersionAsync` / `DeletePackageAsync` / `DeleteBuildAsync` (and release reject) run one DB transaction via `BuildPurger.PurgeBuildsAsync`:
delete patches touching the builds (either direction) and their patch jobs → per-hash `ReferenceCount -= n`
(bulk) after deleting `BuildFile` rows → delete `StoredFile` rows now ≤ 0 that no open upload session references →
delete builds (download logs cascade) → version(s) / package (API keys + publisher keys cascade). Blobs
(content + patch) are deleted **after commit**, best effort (`TryDeleteBlobAsync`); failures are left to the
sweeper.

## Content locks (`Services/ContentLocks.cs`)

`ContentLocks.Shared`: in-process per-hash async locks (one server per database; sorted, distinct acquisition;
idle entries removed). An upload holds its hash from the dedup lookup (`StorePendingAsync`, which re-uploads a
missing blob for a reused row) through the session-row insert. `PackageService` deletions take the locks of every
hash the builds reference **before** `BeginTransaction` (SQLite takes the write lock at once; waiting inside could
deadlock with an upload) and hold them until the blobs are deleted after commit.

## OrphanSweeper (hosted, runs at startup then hourly)

1. Delete expired upload sessions (files cascade).
2. Delete `StoredFile` rows with `ReferenceCount ≤ 0` and (`PendingSince` null or older than 24 h) that no
   session file references, then their blobs: per batch of 500, under the batch's `ContentLocks`, with every
   predicate repeated in the delete, and only the blobs of rows actually deleted.
3. List **every** object in storage (`IStorageProvider.ListAsync`); delete a key that is neither a
   `StoredFile.StoragePath` nor a `BuildPatch.StoragePath`, is older than 24 h, **and** is in Instella's layout
   (`IsInstellaKey`: `ab/cd/{sha256}`, `patches/…/patch.zip`, or their `.{guid}.tmp`). Other keys are left
   alone and reported once per sweep ("storage contains N files that are not Instella's …"). `SaveSettingsAsync`
   refuses a local folder that is relative, a filesystem root, or equal to / containing the config directory
   (`ValidateLocalBasePath`, `ServerConfigDirectory` singleton).

## Patch generation (`PatchJobWorker` + `DiffService`)

- Upload completion adds `PendingPatchJob {ToBuildId}` **inside its transaction**; nothing is generated inline.
- `DiffService.GeneratePatchAsync` (internal) returns `PatchResult(PatchOutcome {Created, NotWorthIt, NoPreviousBuild,
  Disabled, BuildGone}, BuildPatch?)`: every outcome completes the job. Real failures (storage, a missing blob, the
  archive) throw `PatchGenerationException`, so the job is retried. File paths are compared ordinally (case-variant
  paths are two files).
- Worker polls every 5 s, runs one due job per poll. Claimable: (`Pending`|`Failed`) with `NextAttemptAt ≤ now`,
  or `InProgress` with expired `LeaseExpiresAt`; and `Attempts < 3`. Claim = conditional `ExecuteUpdate` on
  `RowVersion` (compare-and-set) setting `InProgress`, `Attempts+1`, lease 10 min; lease renewed every minute.
  Failure → `Failed` with backoff 30/120/600 s, `Dead` after 3 attempts. Each poll first marks `Dead` any
  `InProgress` job whose lease expired with `Attempts = 3` (its worker stopped during the final attempt).
- **"Latest"**, one query everywhere (`PackageService.GetLatestVersionAsync`, `Releasable`): the highest
  `VersionKey` on the channel, not deprecated, with a published build for the OS/arch, and `<=` the channel's pin
  (`PackageChannel.PinnedVersion`) when set. Release dates never order versions. A platform without a build at the pin
  falls back to the highest lower version that has one. Used by check-update, `installer/…/latest`, the download page
  (versions above the pin are listed, not linked as latest) and `PackageSummary.LatestVersion` (stable, any platform;
  `VersionCount` = versions with a published build). Pins are set in the admin package pane ("Channels",
  `SetChannelPinAsync`, security event `ChannelPinChanged`); no HTTP API yet. Deleting the pinned version clears it.
- `DiffService`: previous build = the highest `VersionKey` below this version, same package + channel, non-deprecated, same
  os/arch. Changed files → BSDiff, new files → full, removed → delete list. Writes patch ZIP (`manifest.json` +
  patch entries) at `patches/{pkg}/{from}-to-{to}/{os}-{arch}/patch.zip`, `BuildPatch` row with `ManifestJson`
  (`Instella.Core.Update.PatchManifest`). Discarded if size ≥ `Diff:MaxPatchRatio` × full. Only patches from the
  immediately previous version; a client further behind downloads changed files whole.

## Storage providers

- `IStorageProvider`: `UploadAsync, DownloadAsync, DownloadRangeAsync, DeleteAsync, ExistsAsync, GetInfoAsync,
  GetPresignedUrlAsync, ListAsync` (default-interface `ListAsync` yields nothing).
- Content keys `{hash[0:2]}/{hash[2:4]}/{hash}`; patch keys under `patches/`.
- `LocalStorageProvider(basePath)`: writes to a temp file then renames (a collision on an existing key is success).
- `S3StorageProvider`: AWS SDK, `ForcePathStyle = true`, custom `ServiceURL` if an endpoint is set; downloads
  return the response stream (disposes the response); presigned URLs for file/patch downloads. Endpoint must be
  `https` unless `S3AllowInsecureEndpoint` (dev only; .NET refuses https→http redirects). `ExistsAsync` / `DeleteAsync`
  return false only for a 404 and throw anything else. `TestConnectionAsync` writes a 12-byte object under
  `__instella_connection_test__/`, reads its metadata and deletes it; a failure is reported as the HTTP status and S3
  error code only.
- `StorageProviderAccessor` builds from the `ServerSettings` row on first use (fallback before setup:
  `Storage:Local:BasePath`); `StorageSettingsService.SaveSettingsAsync` calls `Invalidate()`.
  Configured in `/setup` step 2 and admin Settings (with "Test Connection": `ValidateS3Endpoint` first, including
  `S3AllowInsecureEndpoint`, then `TestConnectionAsync`).

## Error handling

Non-Development: `/api/*` → `UseExceptionHandler()` → RFC 7807 ProblemDetails with `traceId`, no exception
details; UI → re-execute `/Error` (shows request id). UI 404s → `/not-found` (API 404s untouched). Development
keeps the developer exception page. EF command logging is `Warning` in `appsettings.json`.

## Docker

- `src/Instella.Server/Dockerfile` (not at repo root): `aspnet:10.0-alpine`, copies a pre-built `publish/`
  (from `scripts/build.ps1`, which also `docker save`s `{ImageName}:{version}` to a tar; `:latest` only for a
  final version), runs as the base image's non-root `app` user, **uid/gid 1654** (host volumes need
  `chown -R 1654:1654`, which `scripts/deploy.sh` does in `prepare_volumes`), `ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production INSTELLA_CONFIG_DIR=/config
  Storage__Local__BasePath=/packages Server__TlsTerminatedByProxy=true`, `VOLUME ["/config","/packages"]`,
  `EXPOSE 8080`, `HEALTHCHECK wget -qO- http://localhost:8080/healthz`.
- `deploy/docker-compose.yml` (with `deploy/config/appsettings.json`): image `${INSTELLA_IMAGE:-instella-server}:${VERSION:-latest}`, `${INSTELLA_PORT:-8580}:8080`,
  bind mounts `./config:/config` + `./packages:/packages`, same health check, commented
  `ReverseProxy__KnownProxies__0`. `scripts/deploy.sh` loads the tar, stops the container, backs up `config/` +
  `packages/` + compose + `.env`, prepares volume ownership, restarts and waits for `/healthz`.
- One server instance per database: `ContentLocks` and the rate-limit/backoff state are in-process.

## Tests

`tests/Instella.Server.Tests` use in-memory SQLite + `Migrate()` (real FKs/transactions/bulk SQL, not the EF
InMemory provider); `ContractServer` (`WebApplicationFactory<PackagesController>`) sets `Instella:ConfigDir` to a
temp root and a fixed client address `198.51.100.7`. Queries compare against a captured `DateTime.UtcNow`
parameter, never SQL `now` (precision differs). Release approval: `Infrastructure/ReleaseTestBed` (package + registered
key + upload key; signed, draft and sign-draft helpers), `ReleaseApprovalServiceTests`, `ReleaseApprovalSettingTests`,
`DelayedReleaseWorkerTests`, `Integration/ReleaseApprovalUiTests` (bUnit), `Contract.Tests/ReleaseApprovalContractTests`.

## File structure

```
Program.cs
Api/{Upload,Download,Installer,Packages,Approvals,Auth}Controller.cs, SiteLinks.cs
Auth/{AdminAuthHandler,ApiKeyAuthHandler,ApiPermissions,DownloadAccess,PendingSecondFactor,RejectInvalidAntiforgeryAttribute}.cs
Extensions/{HttpContextExtensions,IpAddresses}.cs
Data/AppDbContext.cs, DesignTimeDbContextFactory.cs, Entities/ (20 entity types in 18 files)
Migrations/20260927011420_InitialCreate*.cs, 20260928162942_ReleaseApproval*.cs, AppDbContextModelSnapshot.cs
Models/ (TargetOS, Architecture, PlatformMapping, grid/paging models)
Services/{Package,DownloadPage,DownloadToken,Auth,Diff,StorageSettings,ContentStorage,Upload,ReleaseApproval,SecurityLog,IpBan,RateLimit}Service.cs
Services/{PatchJobWorker,DelayedReleaseWorker,BuildPurger,OrphanSweeper,ContentLocks,IpBanCache,SecurityEventThrottle,SecretProtector,SetupTokenService,DatabaseHealthCheck,UploadLimits}.cs
Storage/{IStorageProvider,LocalStorageProvider,S3StorageProvider,StorageProviderAccessor,StorageResult}.cs
Components/Pages/{Home,Packages,ApiKeys,Settings,Docs,Setup,Login,Logout,Error,NotFound,Download}.razor
Components/{Layout,PackagesGrid,Shared}/, Routes.razor, App.razor
Dockerfile, appsettings.json, wwwroot/
```
