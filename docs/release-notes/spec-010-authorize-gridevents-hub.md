# Spec 010 — Authorize GridEventsHub

**Released:** 2026-05-28
**Spec:** [`docs/specs/010-authorize-gridevents-hub/spec.md`](../specs/010-authorize-gridevents-hub/spec.md)

## What changed

`NimBus.WebApp.Hubs.GridEventsHub` (mapped at `/hubs/gridevents`) is now decorated with `[Authorize]`. Anonymous WebSocket / long-polling clients can no longer negotiate or receive the endpoint-status broadcasts that the hub publishes to the management SPA.

The cookie-authentication scheme registered by `NimBus.Extensions.Identity.AddNimBusIdentity(...)` now installs a `CookieAuthenticationEvents` handler (`NimBus.Extensions.Identity.NimBusCookieAuthenticationEvents`) that returns:

- **401 Unauthorized** for anonymous requests to `/hubs/gridevents*` or `/api/*` (instead of the default 302 redirect to `/account/login`).
- **403 Forbidden** for access-denied requests on the same paths.

Other paths (the SPA shell, MVC views, login pages) continue to receive the default 302-to-login redirect.

## Why

The SignalR client cannot follow a 302 redirect to an HTML login page on a negotiate request. Without the new event handler, anonymous clients would see `negotiate failed: unexpected response` errors rather than a clean auth failure. The same change benefits the SPA's `/api/*` fetch surface: a 401 lets the SPA surface its standard "session expired" affordance instead of an opaque CORS / redirect failure.

The hub broadcast surface (counts of pending, failed, deferred, and pending-handoff messages per endpoint) was previously observable by any anonymous WebSocket client that knew the URL. The information was low-sensitivity but inconsistent with every other read path in the WebApp.

## Impact for operators

- **Browsers signed into the WebApp**: zero change. The auth cookie is sent automatically with the SignalR negotiate request and the connection succeeds as before.
- **Local development (Aspire)**: zero change. `LocalDevAuthHandler` returns an authenticated principal that satisfies `[Authorize]`.
- **Anonymous CI smoke tests** that connect to the hub without auth: will now fail with HTTP 401. Reconfigure to sign in (or remove — use the `/health` endpoint for liveness probes).
- **Automated probes** that POST to `/hubs/gridevents/negotiate` anonymously: same — switch to `/health` or use a service principal.

## Migration

No code change is required for legitimate users. No schema change, no contract change. Removing the `[Authorize]` attribute reverses the change (the cookie events are additive — they're inert for already-authenticated requests).
