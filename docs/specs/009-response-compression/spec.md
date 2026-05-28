# Feature Specification: Response Compression (Brotli + Gzip) for the Management WebApp

Feature Branch: `009-response-compression`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see `BH.DIS.WebApp/Startup.cs` lines 283-301, 329-331).
Input: User description: "Run Brotli and Gzip response compression in-process on the management WebApp. Aspire-hosted local stacks have no reverse proxy in front, so the SPA bundle (a few MB unzipped, ~400 KB gzipped, ~340 KB brotli'd) ships uncompressed today, and the same is true for any deployment that doesn't sit behind an Azure App Service / Application Gateway with compression enabled. In-process middleware closes the gap cheaply, picks Brotli when the client advertises it, and otherwise falls back to Gzip — all without losing the App Service compression path when that *is* in front."

## Problem

The management WebApp's static SPA bundle (Vite-built JS, CSS, source maps) and its JSON API responses ship uncompressed unless something in front of the process compresses them. Two deployments where "something in front" is absent:

1. **Local development under Aspire.** The local AppHost wires the WebApp into the dashboard without a reverse proxy. The 6 MB bundle ships raw on every page load, dwarfing the Aspire dashboard's startup chatter and making page-load profiling on slow networks deceptive.
2. **Reverse-proxy-less production-style deployments.** Some operators run NimBus behind a load balancer that does not compress, or directly via Kestrel for internal-only tooling. App Service's default IIS compression does not reliably cover `application/javascript` and never serves precompressed `.br` files from the Vite build.

ASP.NET Core's `Microsoft.AspNetCore.ResponseCompression` middleware solves both at trivial cost — it negotiates content encoding from the `Accept-Encoding` header, compresses on the fly, and is a no-op when the request already advertises an unsupported encoding or the response type is not on the configured MIME list. The DIS fork has shipped it; NimBus has not.

The change is small and self-contained: register the middleware in `ConfigureServices`, wire it before `UseStaticFiles` in `Configure`, and extend the default MIME-type list with the four content types the WebApp actually serves that are not on the defaults (`application/javascript`, `application/json`, `text/css`, `image/svg+xml`).

## Scope

In scope:
- Registering `AddResponseCompression(...)` in `NimBus.WebApp/Startup.cs` (or whatever ConfigureServices surface the WebApp uses).
- Enabling Brotli and Gzip providers, both at `CompressionLevel.Optimal`.
- Setting `EnableForHttps = true` so HTTPS responses are compressed too. (NimBus has no CRIME-class secrets in the SPA bundle; the JSON API surface returns small responses where the attack does not apply.)
- Extending the default MIME types with `application/javascript`, `application/json`, `text/css`, `image/svg+xml`.
- Wiring `app.UseResponseCompression()` *before* `app.UseSpaStaticFiles(...)` and `app.UseStaticFiles(...)` so the compressed encoding is chosen before the static-file middleware short-circuits the request.
- A short note in the README / deployment docs explaining the trade-off vs. App Service / reverse-proxy compression.

Out of scope:
- A configuration knob to disable compression. The middleware is always on; reverse-proxy operators who want their proxy to compress instead can ignore the in-process compression (it costs negligible CPU when the client does not advertise Brotli / Gzip — which is never the case in practice for modern browsers — or when the response type is not configured).
- Compressing the `/health`, `/alive`, `/ready` endpoints. These responses are short text and compression overhead is wasteful; they are not on the configured MIME list and will pass through uncompressed.
- Serving Vite-emitted precompressed `.br` files from disk (would require `Microsoft.AspNetCore.StaticFiles` `OnPrepareResponse` magic and a custom `IContentTypeProvider`). On-the-fly Brotli compression is enough and avoids the build-time precompression bookkeeping.
- Compressing SignalR hub frames. SignalR's WebSocket transport already negotiates `permessage-deflate` when available; ResponseCompression does not apply.
- Per-response compression-level overrides (e.g., Optimal for the bundle, Fastest for hot API paths). Optimal across the board is fine — the middleware's per-response cost is in the 1-5 ms range at WebApp scale.

## User Scenarios & Testing

### User Story 1 - SPA bundle ships compressed under Aspire (Priority: P1)

As a NimBus developer running the local Aspire stack, I want the SPA bundle to ship compressed so that initial page load is dominated by the actual hydration time, not the bundle download.

Why this priority: This is the canonical use case the spec targets. Aspire has no reverse proxy.

Independent Test: Run `dotnet run --project src/NimBus.AppHost`. Open the WebApp. In Chrome DevTools → Network, confirm the bundle responses have `Content-Encoding: br` (or `gzip` on browsers that don't advertise br). Compare the transferred size to the unencoded size; expect a 70-85 % reduction for typical Vite output.

Acceptance Scenarios:

1. Given the WebApp is running with the middleware enabled and a browser that advertises `Accept-Encoding: br, gzip`, When the browser requests `index-*.js`, Then the response has `Content-Encoding: br` and `Vary: Accept-Encoding`.
2. Given the same browser requests an API endpoint that returns JSON ≥ 1 KB (e.g., `GET /api/endpoints`), When the response is sent, Then it has `Content-Encoding: br` and the transferred bytes are smaller than the uncompressed body.
3. Given a browser that advertises only `Accept-Encoding: gzip`, When it requests the bundle, Then the response has `Content-Encoding: gzip`.
4. Given a client that advertises no compression (`Accept-Encoding: identity` or absent), When it requests the bundle, Then the response is uncompressed with no `Content-Encoding` header set — equivalent to today's behaviour.

---

### User Story 2 - Static-file middleware sees compressed responses (Priority: P1)

As a NimBus developer, I want `UseResponseCompression` to run before `UseSpaStaticFiles` and `UseStaticFiles` so that the SPA bundle is picked up by the middleware rather than being short-circuited by the static-file pipeline.

Why this priority: A wrong ordering silently disables compression for the largest assets. Catches a real foot-gun.

Independent Test: Inspect `Configure(...)` in `Startup.cs`. Confirm `app.UseResponseCompression()` precedes both `app.UseSpaStaticFiles(...)` and any `app.UseStaticFiles(...)` call. Confirm at runtime that the static SPA files arrive with a `Content-Encoding` header.

Acceptance Scenarios:

1. Given `app.UseResponseCompression()` is placed before `app.UseSpaStaticFiles(...)` in the pipeline, When the SPA bundle is requested, Then the response carries `Content-Encoding`.
2. Given the order is reversed (regression case — used as a guard test), When the bundle is requested, Then the response would arrive uncompressed. (This test exists only to document the constraint, not to ship reversed.)

---

### User Story 3 - JSON API endpoints are compressed (Priority: P2)

As a NimBus operator using the WebApp on a slow link, I want endpoint-list, audit-list, and messages-list JSON responses to be compressed, since the largest user-facing payloads after the bundle are these.

Why this priority: The audit-list and messages-list responses can run to 100-500 KB on a busy endpoint. Compression cuts the wire bytes by ≥ 80 %.

Independent Test: Hit `GET /api/audits/endpoint/{id}?top=500` on a populated endpoint. Confirm `Content-Encoding: br` and a substantial size reduction.

Acceptance Scenarios:

1. Given a JSON API response of ≥ 1 KB, When the response is sent, Then `Content-Encoding` is set per FR-002 negotiation.
2. Given a small JSON response (e.g., 100-byte ping), When the response is sent, Then it is compressed too (the middleware does not gate by minimum size; the per-response overhead is acceptable for the simplicity gain). Operators can later add a `MinimumResponseLength` knob if profiling shows the small-response overhead matters.

---

### User Story 4 - Health endpoints are not compressed (Priority: P2)

As an ops engineer probing `GET /health`, I do NOT want the response compressed — health checks are pinged at high frequency, are tiny, and the compression overhead is wasteful.

Why this priority: Trivial regression-protection on a hot path.

Independent Test: Hit `GET /health`. Confirm no `Content-Encoding` header on the response.

Acceptance Scenarios:

1. Given `GET /health` returns `application/json` with a short body (e.g., `"Healthy"`), When the response is sent, Then it carries no `Content-Encoding`. (The MIME list defaults plus our four entries do include `application/json`, so this scenario requires verifying that the health endpoint's actual content type isn't on the list — or that its body is below the middleware's internal compression threshold. **Note:** the .NET response-compression middleware does compress small responses by default; if health endpoints emit `application/json`, they will be compressed. This scenario captures intent — if the cost shows up in profiling, add a content-type filter or response-size threshold. Otherwise accept the compression on health checks as a small constant cost.)

---

### User Story 5 - Existing reverse-proxy-fronted deployments are unaffected (Priority: P1)

As an Azure App Service operator who has IIS-level compression on, I want the in-process middleware to be a no-op when IIS has already compressed the response, not a double-encode.

Why this priority: A double-encoded response is broken. Must verify behaviour.

Independent Test: Deploy to an App Service with default compression. Confirm responses carry exactly one `Content-Encoding` value (`br` or `gzip`), not two.

Acceptance Scenarios:

1. Given the response is being compressed by an outer layer (App Service IIS), When the in-process middleware runs, Then it does NOT double-encode the response. ASP.NET Core's middleware respects existing `Content-Encoding` headers and short-circuits when one is already set.
2. Given the outer layer strips `Accept-Encoding` from the request (some proxies do this), When the in-process middleware runs, Then it sees no advertised encoding and emits the response uncompressed. The outer layer compresses on its way out.

---

## Edge Cases

- Client advertises `Accept-Encoding: identity` (no compression desired) — middleware emits the response uncompressed. No `Content-Encoding` header.
- Client advertises a future encoding the middleware does not implement (`Accept-Encoding: zstd`) — middleware falls back to Brotli or Gzip in advertised priority order; otherwise uncompressed.
- Response is being streamed (SignalR negotiate / hub send is not via ResponseCompression — see Scope). WebSocket frames are not touched by the middleware.
- Response status is 304 Not Modified — no body, no compression. Middleware passes through.
- Response is an image (PNG, JPEG) — already-compressed; not on the MIME list; passes through uncompressed.
- The bundle file ships with a `Content-Encoding: gzip` header set explicitly by the static-file middleware (it does not today, but if it ever did) — ResponseCompression detects the existing header and does not double-encode.
- The middleware is registered but `Configure` accidentally omits `app.UseResponseCompression()` — symptom is uncompressed responses; FR-030 + the test in FR-051 catches this.
- HTTPS bundle requested over a slow connection — Brotli at Optimal can stall the first byte by 20-50 ms relative to identity. Net effect is still faster total transfer for any payload above ~10 KB, which is the dominant case.

## Requirements

### Functional Requirements

#### Registration

- FR-001: `ConfigureServices(IServiceCollection)` in `NimBus.WebApp/Startup.cs` MUST register the middleware:
  ```csharp
  services.AddResponseCompression(options =>
  {
      options.EnableForHttps = true;
      options.Providers.Add<BrotliCompressionProvider>();
      options.Providers.Add<GzipCompressionProvider>();
      options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
      {
          "application/javascript",
          "application/json",
          "text/css",
          "image/svg+xml",
      });
  });
  services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
  services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
  ```
- FR-002: Providers MUST be added in the order Brotli first, Gzip second. ASP.NET Core picks providers in registration order constrained by the request's `Accept-Encoding` quality values. Brotli ships ~15 % smaller output for typical JS/CSS at comparable CPU cost; it should win whenever the client advertises it.
- FR-003: `EnableForHttps = true` MUST be set. The WebApp serves HTTPS exclusively in production; default `false` would silently skip compression on the primary deployment surface.
- FR-004: The MIME-type list MUST extend `ResponseCompressionDefaults.MimeTypes` (which covers `text/plain`, `text/html`, `text/xml`, `application/xml`, `text/css` already on newer runtimes) with the four additions above. Concatenation, not replacement.

#### Pipeline placement

- FR-010: `Configure(IApplicationBuilder, IWebHostEnvironment)` MUST call `app.UseResponseCompression()` BEFORE:
  - `app.UseSpaStaticFiles(...)` — required so the SPA bundle is compressed.
  - `app.UseStaticFiles(...)` — required for any static-file middleware that wraps non-SPA assets.
  - `app.UseRouting()` — required so the chosen encoding is picked before any endpoint short-circuits.
- FR-011: The placement comment in `Configure` MUST briefly explain the ordering constraint, so future refactors that move middleware around do not regress.

#### Cache headers (existing behaviour preserved)

- FR-020: The existing static-file `OnPrepareResponse` callback that sets `Cache-Control: no-cache` on `index.html` and `public, max-age=31536000, immutable` on hashed assets MUST continue to work alongside compression. Compression sets `Vary: Accept-Encoding`; the existing `Cache-Control` is set on the entry headers. Caching proxies between the WebApp and the browser will key the cache on encoding correctly because of `Vary`.

#### Documentation

- FR-030: A short paragraph in `docs/getting-started.md` (or the deployment section if one exists) MUST note that the WebApp performs in-process Brotli + Gzip compression, with a sentence on what to do if the deployment already compresses upstream. (Short answer: nothing — the upstream compression takes precedence and the in-process middleware is a no-op when it sees an already-encoded response.)
- FR-031: A code comment at the registration site MUST briefly justify why the four MIME-type additions are needed, mirroring the DIS comment ("App Service's default IIS compression doesn't cover `application/javascript` reliably and never serves precompressed `.br` files emitted by the Vite build; the middleware does both.").

#### Tests

- FR-050: An integration test on the WebApp MUST verify that a request for a known SPA asset with `Accept-Encoding: br, gzip` returns `Content-Encoding: br` and a body length smaller than the uncompressed body length.
- FR-051: An integration test MUST verify that a JSON API response (e.g., `GET /api/endpoints` against a seeded test platform) carries `Content-Encoding: br` when br is advertised.
- FR-052: An integration test MUST verify that a request with `Accept-Encoding: identity` returns no `Content-Encoding` header. (Belt-and-braces — confirms the negotiation logic, not just the always-on path.)
- FR-053: An integration test MUST verify the middleware placement: `UseResponseCompression` runs before `UseSpaStaticFiles`. (One option: reorder them in a test build and assert the bundle response no longer carries `Content-Encoding`. Cheaper option: a unit-style test inspecting the registered middleware order via `IStartupFilter` introspection — implementer's choice.)

### Non-Functional Requirements

- NFR-001: The per-response compression CPU cost MUST be acceptable for the WebApp's typical workload. At Brotli Optimal on typical JS bundles, this is ~5-15 ms per MB on a modern CPU. The bundle is served only on initial load; the cost is amortised over hours of session usage. Hot API paths return ≤ 50 KB JSON, where the cost is sub-millisecond.
- NFR-002: No change to the bundle size on disk. The middleware compresses on the fly; no precompressed `.br` artefacts shipped with the deployment.
- NFR-003: No new NuGet dependency. `Microsoft.AspNetCore.ResponseCompression` is part of the `Microsoft.AspNetCore.App` shared framework; no `PackageReference` added.
- NFR-004: No public API surface change. The WebApp's HTTP contract is unaffected — clients see compressed responses when they advertise it, uncompressed otherwise, identically to any other compressed HTTP server.
- NFR-005: Memory budget per request is bounded by the Brotli encoder's working set (typically < 256 KB at Optimal level for typical body sizes). Acceptable on the WebApp's tier.
- NFR-006: Compatibility: works on `net10.0` (verified) and across the supported runtime cadence (the API has been stable since `Microsoft.AspNetCore.ResponseCompression` 2.x).

## Key Entities

- **`AddResponseCompression(...)`** — DI registration. New call in `ConfigureServices`.
- **`BrotliCompressionProvider`** — registered first. Preferred encoding when advertised.
- **`GzipCompressionProvider`** — registered second. Fallback.
- **MIME-type list** — `ResponseCompressionDefaults.MimeTypes` concatenated with `application/javascript`, `application/json`, `text/css`, `image/svg+xml`.
- **`app.UseResponseCompression()`** — new middleware call in `Configure`, placed before static-file and routing middleware.

## Success Criteria

### Measurable Outcomes

- SC-001: An initial cold WebApp page load served by a local Aspire stack transfers ≤ 25 % of the previously transferred bundle bytes. (Vite's main bundle compresses ~75 % under Brotli Optimal; CSS bundles ~80 %.)
- SC-002: `GET /api/audits/endpoint/{id}?top=500` against a populated endpoint transfers ≤ 30 % of the uncompressed JSON body bytes.
- SC-003: All integration tests (FR-050 through FR-053) pass.
- SC-004: No existing WebApp test fails as a result of the change.
- SC-005: When deployed behind an outer layer that already compresses (App Service default IIS compression), responses carry exactly one `Content-Encoding` header — verified by inspecting headers from a deployed staging environment.
- SC-006: SignalR connections continue to work (the spec does not touch the hub, but worth verifying after middleware churn).

## Assumptions

- The WebApp targets `net10.0` and consumes the `Microsoft.AspNetCore.App` shared framework; `Microsoft.AspNetCore.ResponseCompression` is therefore available without an explicit package reference. (Verified — DIS uses the same surface and ships with no added `PackageReference`.)
- The current pipeline order in `NimBus.WebApp/Startup.cs::Configure` runs `app.UseSpaStaticFiles(...)` and `app.UseStaticFiles(...)` after `app.UseHttpsRedirection()` and before `app.UseRouting()`. The new `app.UseResponseCompression()` slots in immediately after `UseHttpsRedirection`. (To be verified at implementation time against current `Startup.cs`.)
- The WebApp's response surfaces are dominated by static SPA assets and JSON API responses. The two cover ≥ 95 % of bytes by volume.
- Existing CRIME / BREACH-class concerns do not apply at WebApp scope. The SPA bundle contains no secrets; the JSON API responses do not contain attacker-controllable reflected secrets at byte resolution. `EnableForHttps = true` is therefore safe.

## Out of Scope

- A per-endpoint or per-route opt-out. The middleware runs for everything; downstream behaviour is governed by the MIME list and `Accept-Encoding`.
- Precompressed-asset serving (Vite's `vite-plugin-compression` or equivalent). On-the-fly is enough.
- Brotli dictionary configuration for the WebApp's specific corpus. Default dictionary is fine.
- Compression of SignalR frames. Use SignalR's own `permessage-deflate` (default-enabled where supported).
- A `MinimumResponseLength` knob. Add later if profiling shows the small-response overhead matters.
- A telemetry / OpenTelemetry attribute marking "this response was compressed." Compression is transparent; observability of bytes-on-the-wire belongs to the outer infrastructure layer.

## Open Questions

- **Should the middleware register a `MinimumResponseLength`?** ASP.NET Core does not set one by default. Empirically the cost on small responses is sub-millisecond. Default left at "no minimum" for v1 simplicity; revisit if a profiling pass on the health endpoints shows otherwise.

## Resolved Questions

- Brotli first, Gzip second. Resolved — Brotli compresses ~15 % smaller on the WebApp's content mix at comparable CPU.
- Both providers at `CompressionLevel.Optimal`. Resolved — the WebApp is not in a hot-path serving role; the extra CPU vs. `Fastest` is comfortably amortised.
- `EnableForHttps = true`. Resolved — the WebApp serves HTTPS exclusively in production; CRIME / BREACH concerns do not apply at this scope.
- Extend the default MIME-type list, do not replace it. Resolved — `application/javascript`, `application/json`, `text/css`, `image/svg+xml` are the documented gaps in IIS / default coverage.
- Place `UseResponseCompression` before `UseSpaStaticFiles` and `UseStaticFiles`. Resolved — static-file middleware short-circuits the request; compression must run earlier.
- Do not gate by content size. Resolved — simplicity over the marginal overhead on short responses.
