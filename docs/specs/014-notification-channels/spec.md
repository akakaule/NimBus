# Feature Specification: Notification Channels — Webhook, Teams, Email

Feature Branch: `014-notification-channels`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed
Input: User description (GitHub issue #10, backlog reference `docs/backlog.md#notification-channels`): "The existing `NimBus.Extensions.Notifications` framework can detect failures, but ops teams have no production-ready channel to send the alerts to. They want to wake up to a Teams message or PagerDuty page when a session blocks, not discover it the next morning in the WebApp. Notification Channels supplies three production channels (webhook, Teams, email) plus severity-based routing and rate limiting to prevent notification storms when something cascades. Trigger sources: failed messages, dead-letters, session blocks."

## Problem

`NimBus.Extensions.Notifications` already exists and already does the *detection* half of the job. The package today provides:

- `INotificationChannel.SendAsync(Notification notification, CancellationToken cancellationToken = default)` — the channel contract (`src/NimBus.Extensions.Notifications/INotificationChannel.cs:10`).
- A `Notification` model carrying `Severity`, `Title`, `Message`, `EventId`, `EventTypeId`, `MessageId`, `CorrelationId`, and `ErrorDetails` (`INotificationChannel.cs:21`).
- A `NotificationSeverity` enum with exactly four members — `Information`, `Warning`, `Error`, `Critical` (`INotificationChannel.cs:64`).
- `NotificationOptions` with four lifecycle toggles — `NotifyOnFailure`, `NotifyOnDeadLetter`, `NotifyOnReceived`, `NotifyOnCompleted` (`src/NimBus.Extensions.Notifications/NotificationOptions.cs`).
- `NotificationLifecycleObserver : IMessageLifecycleObserver`, which translates lifecycle callbacks into `Notification` instances and fans them out to every registered `INotificationChannel`, swallowing channel exceptions so a failing channel never breaks message processing (`src/NimBus.Extensions.Notifications/NotificationLifecycleObserver.cs:98`).
- `ConsoleNotificationChannel`, the only shipped channel — it colour-prints to stdout (`src/NimBus.Extensions.Notifications/ConsoleNotificationChannel.cs`).
- A builder entry point `INimBusBuilder.AddNotifications(configureOptions, configureChannels)` that registers the options singleton, registers either the caller-supplied channels or the console default, and wires the observer via `builder.AddLifecycleObserver<NotificationLifecycleObserver>()` (`src/NimBus.Extensions.Notifications/NotificationsExtension.cs:55`).

What is missing is everything *downstream of detection*: a production place to send the alert, a way to keep a `Critical` going to PagerDuty while `Warning` only goes to a chat channel, and a brake so that a cascading outage that dead-letters 5,000 messages in a minute does not emit 5,000 Teams cards. Four concrete gaps:

1. **No production channels.** The console channel is dev-only. An operator wiring NimBus into production has to hand-write `INotificationChannel` implementations for webhook / Teams / email, including HTTP client lifetime, payload shaping, and severity gating — the same boilerplate every team re-invents.
2. **No severity routing.** Today every registered channel receives *every* notification the observer emits. There is no `MinSeverity` knob on a channel and no router; an `Information`-level "message received" notification (`NotificationLifecycleObserver.cs:28`) would page on-call exactly as loudly as a `Critical` dead-letter. The observer's `SendToAllChannels` (`NotificationLifecycleObserver.cs:98`) iterates all channels unconditionally.
3. **No storm control.** The observer emits one notification per lifecycle event, synchronously, with no aggregation or rate limit. A poison message that re-delivers, or a downstream outage that fails an entire session's worth of messages, produces an unbounded burst.
4. **Session blocks are not even a trigger yet.** The issue lists "session blocks" as a trigger source, but `SessionBlockedException` is caught and silently swallowed in the core handler (`src/NimBus.Core/Messages/MessageHandler.cs:83-85`), and `IMessageLifecycleObserver` has no session-block callback — only `OnMessageReceived` / `OnMessageCompleted` / `OnMessageFailed` / `OnMessageDeadLettered` (`src/NimBus.Core/Extensions/IMessageLifecycleObserver.cs:12`). So "notify on session block" requires either a new lifecycle hook or mapping the block onto an existing callback. This spec resolves that below.

This feature *extends* the existing package. It does not replace the `INotificationChannel` contract, the `Notification` model, or the `NotificationSeverity` enum — three new channels plug into the same `SendAsync` contract, and a new `INotificationRouter` sits between the observer and the channels to do per-channel severity filtering and rate limiting.

## Scope

In scope:
- Three new `INotificationChannel` implementations in `NimBus.Extensions.Notifications`: `WebhookChannel`, `TeamsChannel`, `EmailChannel`. Each implements the existing `INotificationChannel.SendAsync(...)` contract unchanged.
- A new `INotificationRouter` + `NotificationRouter` that the `NotificationLifecycleObserver` delegates to instead of iterating channels directly. The router owns per-channel `MinSeverity` filtering and the global rate limit / batching window.
- A per-channel options base carrying `MinSeverity` (defaulting to `NotificationSeverity.Warning`), plus channel-specific options (`WebhookChannelOptions`, `TeamsChannelOptions`, `EmailChannelOptions`).
- HTTP delivery for `WebhookChannel` and `TeamsChannel` via `IHttpClientFactory` typed clients, matching the codebase's existing typed-client pattern (`src/NimBus.WebApp/Startup.cs:389`).
- `EmailChannel` with two providers: `EmailProvider.SendGrid` (HTTPS API) and `EmailProvider.Smtp` (`System.Net.Mail.SmtpClient`).
- A fluent registration surface (`AddWebhook`, `AddTeams`, `AddEmail`, `WithRateLimit`) that composes onto the existing `AddNotifications` plumbing. See the API-shape resolution under Open / Resolved Questions — the issue's `services.AddNimBusNotifications(n => { … })` does NOT exist today; the real entry point is `INimBusBuilder.AddNotifications(...)`.
- Global rate limiting / batching using `System.Threading.RateLimiting.TokenBucketRateLimiter` (no new dependency — it ships in the shared framework).
- Configurable payload templates per channel (a string template with `{Severity}`, `{Title}`, `{Message}`, `{EventId}`, etc. placeholders that resolve against the `Notification`'s public properties).
- Deduplication on `(EventId, Severity)` within a configurable window (default 5 minutes) so a re-delivered failure event does not re-notify.
- A new session-block trigger so "session blocks" becomes a real trigger source (see FR-080).
- Unit tests with mocked channels; an integration test against a local in-process webhook receiver.
- Documentation in `docs/notifications.md`.

Out of scope:
- Changing the `INotificationChannel.SendAsync` contract, the `Notification` model fields, or the `NotificationSeverity` enum members. Channels are additive.
- A WebApp UI for managing channels. Channels are configured in code / `IConfiguration` at startup. (The issue notes this "completes the WebApp Alerting checkbox" — that is satisfied by *the platform being able to alert*, not by a new WebApp screen; there is no Alerting page today and none is added here. See Assumptions.)
- First-class PagerDuty / Opsgenie channels. Both expose generic incoming webhooks; the `WebhookChannel` with a custom template covers them. (Resolved Question.)
- Persisting a notification history / audit of what was alerted. Notifications are fire-and-forget; the durable record of the underlying event lives in the message store and the WebApp's blocked / dead-letter listings already (`src/NimBus.MessageStore.SqlServer/Schema/0007_BlockedInvalid.sql`).
- Retry of a permanently-failing channel beyond the single inline attempt. The observer already swallows channel exceptions (`NotificationLifecycleObserver.cs:106`); a failed send is logged and dropped.
- Per-endpoint or per-event-type routing rules. Routing is by severity only in v1.

## User Scenarios & Testing

### User Story 1 - On-call is alerted when a session blocks (Priority: P1)

As an ops engineer, I want a Teams message and a PagerDuty page the moment a session blocks or a message is dead-lettered, so I respond in minutes rather than discovering it the next morning in the WebApp.

Why this priority: This is the entire reason the feature exists. Detection without delivery is the status quo the issue is filed against.

Independent Test: Register a `TeamsChannel` (pointed at a Teams incoming-webhook stub) and a `WebhookChannel` (pointed at a PagerDuty Events-API stub). Force a dead-letter and a session block. Assert one Adaptive Card POST hit the Teams stub and one PagerDuty-shaped POST hit the webhook stub, both for a `Critical` notification.

Acceptance Scenarios:

1. Given a `TeamsChannel` with `MinSeverity = Critical` and a message that gets dead-lettered, When `NotificationLifecycleObserver.OnMessageDeadLettered` fires (which builds a `Critical` notification per `NotificationLifecycleObserver.cs:85`), Then the channel receives exactly one `SendAsync` call and posts an Adaptive Card to the configured `ConnectorUrl`.
2. Given a `WebhookChannel` with `MinSeverity = Warning` and a session block, When the new session-block trigger (FR-080) emits a `Critical` notification, Then the channel POSTs a JSON body shaped by its configured template to the configured `Url`.
3. Given both channels are registered and a single dead-letter occurs, When the observer routes the notification, Then both channels receive it (both thresholds are satisfied by `Critical`).

---

### User Story 2 - Severity routing keeps low-value events off the pager (Priority: P1)

As an ops engineer, I want `Information`/`Warning` notifications to go only to a chat webhook while `Critical` notifications also page email/PagerDuty, so the pager stays quiet for noise.

Why this priority: Without per-channel `MinSeverity`, every channel gets every notification (today's `SendToAllChannels` behaviour). That makes production alerting unusable — the first noisy week trains on-call to ignore the pager.

Independent Test: Register a webhook with `MinSeverity = Warning` and an email channel with `MinSeverity = Critical`. Emit an `Error`-level "message failed" notification (the observer builds these at `Error` severity per `NotificationLifecycleObserver.cs:66`). Assert the webhook fired and the email channel did NOT.

Acceptance Scenarios:

1. Given a webhook channel with `MinSeverity = Warning` and an email channel with `MinSeverity = Critical`, When an `Error`-severity notification is routed, Then the webhook channel's `SendAsync` is invoked and the email channel's is not.
2. Given the same registration, When a `Critical` notification is routed, Then both channels' `SendAsync` are invoked.
3. Given a channel with `MinSeverity = Critical`, When an `Information` notification is routed (e.g. `NotifyOnReceived` enabled at `NotificationOptions.cs:21`), Then that channel is skipped entirely — no HTTP/SMTP work is performed.

---

### User Story 3 - Rate limiting prevents a notification storm (Priority: P1)

As an ops engineer, I want a cascading failure that dead-letters thousands of messages to produce a bounded number of alerts, not one per message, so the alert channel survives the incident and so the underlying cause is still readable.

Why this priority: A storm that buries the channel is worse than no alert — the signal is lost in the noise, and some webhook receivers (Teams, PagerDuty) rate-limit the *sender*, dropping later legitimate alerts.

Independent Test: Configure `WithRateLimit(maxPerMinute: 10, burstCapacity: 20)`. Emit 100 distinct `Critical` notifications within one second through the router. Assert at most 20 are delivered to the channel (the burst), the rest are suppressed, and a single "N notifications suppressed" summary is emitted when tokens replenish.

Acceptance Scenarios:

1. Given `WithRateLimit(maxPerMinute: 10, burstCapacity: 20)` and 100 notifications emitted in a 1-second burst, When the router processes them, Then no more than `burstCapacity` (20) are delivered immediately and subsequent ones are suppressed until tokens replenish at `maxPerMinute`.
2. Given notifications are being suppressed by the rate limiter, When the rate window clears, Then a single aggregate notification is emitted summarising the count and severities suppressed (so the storm is visible without flooding).
3. Given the same failure event re-delivers within the dedup window, When the router sees a notification with an `(EventId, Severity)` pair already seen in the last 5 minutes, Then it is dropped without consuming a rate-limit token (Resolved Question — dedupe on `(EventId, Status)` within 5 minutes).

---

### User Story 4 - Webhook channel with a configurable JSON template (Priority: P2)

As an integrator, I want to POST notifications to any HTTP endpoint with a JSON body I control, so I can target PagerDuty, Opsgenie, a Slack incoming webhook, or an internal incident bot without a bespoke channel.

Why this priority: The webhook channel is the universal escape hatch; it subsumes PagerDuty/Opsgenie (Resolved Question) and any future system.

Independent Test: Register a `WebhookChannel` with a template `{"summary":"{Title}","severity":"{Severity}","event":"{EventId}"}` pointed at an in-process receiver. Trigger a notification. Assert the receiver got the templated body with placeholders substituted.

Acceptance Scenarios:

1. Given a webhook channel with a custom JSON template, When a notification is routed to it, Then it POSTs `application/json` with every `{Placeholder}` replaced by the corresponding `Notification` property (`{Title}`, `{Message}`, `{Severity}`, `{EventId}`, `{EventTypeId}`, `{MessageId}`, `{CorrelationId}`, `{ErrorDetails}`).
2. Given a webhook channel with no template configured, When a notification is routed, Then it POSTs a default JSON serialisation of the `Notification`.
3. Given the configured `Url` returns a 500, When the POST fails, Then the exception is caught and logged and message processing is unaffected (the observer's existing `try/catch` at `NotificationLifecycleObserver.cs:102` still wraps the call).

---

### User Story 5 - Teams channel via Adaptive Card incoming webhook (Priority: P2)

As an ops engineer who lives in Teams, I want alerts to arrive as a formatted Adaptive Card in a channel, colour-coded by severity, so the team sees them in context.

Why this priority: Teams is the most-requested production sink in the issue and the canonical "wake up to a Teams message" use case.

Independent Test: Register a `TeamsChannel` with a `ConnectorUrl` pointed at a stub. Trigger a `Critical` notification. Assert the POST body is a valid Adaptive Card JSON with the title, message, and a severity-coloured header.

Acceptance Scenarios:

1. Given a `TeamsChannel` with a configured `ConnectorUrl`, When a `Critical` notification is routed, Then it POSTs an Adaptive Card whose body contains `Title`, `Message`, and the event/correlation ids as facts.
2. Given an `Error` vs `Warning` vs `Critical` notification, When the card is built, Then the card's accent/colour reflects the severity (mirroring the console channel's colour mapping at `ConsoleNotificationChannel.cs:15`).
3. Given the `ConnectorUrl` is unset, When the channel is registered, Then registration throws a clear configuration error at startup (fail fast), not at first send.

---

### User Story 6 - Email channel: SendGrid and SMTP (Priority: P3)

As an ops engineer without a chat/incident tool, I want critical alerts emailed to an on-call distribution list via SendGrid or our SMTP relay, so the lowest-common-denominator channel still works.

Why this priority: Email is the fallback for shops without Teams/PagerDuty. Lower priority because most production users will reach for webhook/Teams first, but it closes the "three production channels" acceptance criterion.

Independent Test: Register an `EmailChannel` with `Provider = SendGrid` and a stubbed HTTP client. Trigger a `Critical` notification. Assert one SendGrid `mail/send` POST with the configured `From`/`To` and a body derived from the notification. Repeat with `Provider = Smtp` against a fake SMTP server.

Acceptance Scenarios:

1. Given `EmailChannel` with `Provider = SendGrid`, `ApiKey`, `From`, and `To`, When a `Critical` notification is routed, Then a SendGrid send API call is made with bearer auth and the configured addresses.
2. Given `EmailChannel` with `Provider = Smtp` and SMTP host/port/credentials, When a notification is routed, Then a message is sent via `SmtpClient` to each `To` address.
3. Given neither `ApiKey` (SendGrid) nor SMTP host (Smtp) is configured for the selected provider, When the channel is registered, Then registration fails fast with a configuration error.

---

## Edge Cases

- **Channel throws on send.** The existing observer wraps each `SendAsync` in `try/catch` and swallows (`NotificationLifecycleObserver.cs:102-109`). The router preserves this: a failing channel is logged and skipped; sibling channels and message processing are unaffected.
- **No channels registered.** `AddNotifications` falls back to `ConsoleNotificationChannel` when no channels are configured (`NotificationsExtension.cs:38`). With the new fluent surface, if a caller calls `AddNotifications` but registers zero channels and zero `WithRateLimit`, the console default still applies (back-compat).
- **All channels gated above the emitted severity.** A `Warning` notification with every channel at `MinSeverity = Critical` results in zero sends and zero HTTP/SMTP work. The router short-circuits before the rate limiter so it does not consume a token.
- **Rate limiter exhausted mid-storm.** Notifications beyond `burstCapacity` are suppressed; the count is accumulated and a single summary notification is emitted on the next replenished token (FR-050). The summary itself is subject to dedup so two near-simultaneous summaries collapse.
- **Dedup collision across severities.** Dedup key is `(EventId, Severity)`, not `EventId` alone — a message that first `Fails` (`Error`, `NotificationLifecycleObserver.cs:66`) and is later dead-lettered (`Critical`, `NotificationLifecycleObserver.cs:85`) produces two distinct notifications because the severity differs, which is correct (escalation is meaningful).
- **`EventId` is null/empty.** Some lifecycle contexts may carry no `EventId` (`MessageLifecycleContext.EventId` is a plain init-only string, `IMessageLifecycleObserver.cs:45`). Dedup falls back to `(MessageId, Severity)`, and if both are empty the notification bypasses dedup (always delivered) — never throws.
- **Teams Adaptive Card exceeds the ~28 KB incoming-webhook limit.** `ErrorDetails` is a full `exception.ToString()` (see `NotificationLifecycleObserver.cs:73`) and can be large. The Teams card truncates `Message` + `ErrorDetails` to a safe size and appends an ellipsis marker.
- **SendGrid/SMTP transient failure.** Caught and logged like any channel failure; no retry in v1. The durable record remains in the message store / WebApp listings.
- **HTTPS connector URL with an expired cert.** The HTTP send fails, is caught and logged; not fatal.
- **Session-block trigger fires on a benign block.** Session blocks are normal back-pressure in some designs; the session-block trigger is gated by a new `NotifyOnSessionBlock` option (default `false`) so it is opt-in and does not surprise existing users (FR-080).
- **Concurrency.** The observer fans out per message and parallel handler contexts exist; the router's rate limiter and dedup cache MUST be thread-safe (`TokenBucketRateLimiter` is; the dedup cache uses a concurrent structure).

## Requirements

### Functional Requirements

#### Channel contract reuse

- FR-001: All three new channels MUST implement the existing, unchanged contract:
  ```csharp
  public interface INotificationChannel
  {
      Task SendAsync(Notification notification, CancellationToken cancellationToken = default);
  }
  ```
  No new method is added to `INotificationChannel`. Severity gating is the router's job, NOT the channel's `SendAsync` (FR-030), so existing custom channels (and `ConsoleNotificationChannel`) keep working unchanged.
- FR-002: The channels MUST consume the existing `Notification` model as-is (`Severity`, `Title`, `Message`, `EventId`, `EventTypeId`, `MessageId`, `CorrelationId`, `ErrorDetails` — `INotificationChannel.cs:21-61`). No field is added or removed.
- FR-003: The channels MUST honour the existing four-member `NotificationSeverity` enum (`Information`, `Warning`, `Error`, `Critical` — `INotificationChannel.cs:64`). No new severity member is introduced.

#### Per-channel options

- FR-010: A `NotificationChannelOptions` base MUST expose `NotificationSeverity MinSeverity { get; set; }` defaulting to `NotificationSeverity.Warning`. Concrete option types derive from it:
  ```csharp
  public abstract class NotificationChannelOptions
  {
      public NotificationSeverity MinSeverity { get; set; } = NotificationSeverity.Warning;
      public string Template { get; set; } // optional payload template; null = channel default
  }

  public sealed class WebhookChannelOptions : NotificationChannelOptions
  {
      public string Url { get; set; }
  }

  public sealed class TeamsChannelOptions : NotificationChannelOptions
  {
      public string ConnectorUrl { get; set; }
  }

  public sealed class EmailChannelOptions : NotificationChannelOptions
  {
      public EmailProvider Provider { get; set; } = EmailProvider.SendGrid;
      public string ApiKey { get; set; }       // SendGrid
      public string SmtpHost { get; set; }      // Smtp
      public int SmtpPort { get; set; } = 587;  // Smtp
      public string SmtpUser { get; set; }      // Smtp
      public string SmtpPassword { get; set; }  // Smtp
      public string From { get; set; }
      public string[] To { get; set; }
  }

  public enum EmailProvider { SendGrid, Smtp }
  ```
- FR-011: Each channel MUST validate its required options at registration time (fail fast), not on first send: `WebhookChannelOptions.Url`, `TeamsChannelOptions.ConnectorUrl`, and the email provider-specific fields MUST be non-empty or registration throws an informative exception.
- FR-012: Options MUST be bindable from `IConfiguration`, matching the codebase convention (`cfg.GetValue<string>("AppInsights:ApiKey")` at `src/NimBus.WebApp/Startup.cs:393`). Callers wire secrets via `configuration["Teams:WebhookUrl"]` / `configuration["SendGrid:ApiKey"]` exactly as the issue's example shows.

#### Routing

- FR-020: A new `INotificationRouter` MUST be introduced:
  ```csharp
  public interface INotificationRouter
  {
      Task RouteAsync(Notification notification, CancellationToken cancellationToken = default);
  }
  ```
- FR-021: `NotificationLifecycleObserver` MUST delegate to `INotificationRouter.RouteAsync(...)` instead of iterating channels directly. The current private `SendToAllChannels` (`NotificationLifecycleObserver.cs:98`) is replaced by a single `await _router.RouteAsync(notification, cancellationToken)`. The observer keeps its existing exception-swallowing guarantee around the router call.
- FR-022: When no `INotificationRouter` is registered (back-compat for callers using the old `configureChannels` lambda without the new surface, `NotificationsExtension.cs:33`), the observer MUST fall back to the legacy fan-out-to-all-channels behaviour so existing code is not broken.
- FR-030: `NotificationRouter` MUST filter per channel by `MinSeverity`: a channel receives a notification only when `notification.Severity >= channel.Options.MinSeverity` (enum ordinal comparison; `Information(0) < Warning(1) < Error(2) < Critical(3)` per `INotificationChannel.cs:64`).
- FR-031: The router MUST short-circuit before consuming a rate-limit token when NO channel passes the severity filter (Edge Case: all channels gated above the emitted severity).

#### Rate limiting & batching

- FR-040: A `WithRateLimit(int maxPerMinute, int burstCapacity)` configuration MUST establish a global token-bucket limit shared across all channels, implemented with `System.Threading.RateLimiting.TokenBucketRateLimiter`:
  ```csharp
  new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
  {
      TokenLimit = burstCapacity,
      TokensPerPeriod = maxPerMinute,
      ReplenishmentPeriod = TimeSpan.FromMinutes(1),
      AutoReplenishment = true,
      QueueLimit = 0,
  });
  ```
  `System.Threading.RateLimiting` ships in the shared framework, so no new NuGet dependency is added (NFR-003).
- FR-041: When the limiter has no available token (`RateLimitLease.IsAcquired == false`), the router MUST suppress the notification (not block, not queue) and increment a suppressed-count accumulator keyed by severity.
- FR-042: Dedup: the router MUST drop a notification whose `(EventId, Severity)` (falling back to `(MessageId, Severity)` when `EventId` is empty) was seen within the dedup window (default 5 minutes) WITHOUT consuming a rate-limit token. (Resolved Question — dedupe on `(EventId, Status)` within 5 minutes; `Status` maps to `Severity` in the real model.)
- FR-043: The dedup window MUST be configurable; default 5 minutes. The dedup cache MUST evict entries older than the window so it does not grow unbounded.
- FR-050: Batching: when rate-limit suppression is active, the router MUST accumulate suppressed notifications and, on the next replenished token, emit a single summary `Notification` (`Severity` = max suppressed severity, `Title` = "N notifications suppressed", `Message` listing counts per severity). This makes a storm visible without flooding.
- FR-051: The summary notification MUST itself pass through dedup and the severity filter, so it is delivered exactly once per storm window and only to channels whose `MinSeverity` it satisfies.
- FR-052: If `WithRateLimit` is never called, the router MUST NOT rate-limit (unbounded, current behaviour) but MUST still apply severity filtering and dedup. Rate limiting is opt-in; severity routing is always on once the router is in play.

#### Templates

- FR-060: `WebhookChannel` MUST support an optional `Template` string with `{Placeholder}` tokens resolving to `Notification` properties: `{Severity}`, `{Title}`, `{Message}`, `{EventId}`, `{EventTypeId}`, `{MessageId}`, `{CorrelationId}`, `{ErrorDetails}`. Unknown placeholders are left literal; missing values resolve to empty string.
- FR-061: When `WebhookChannelOptions.Template` is null, `WebhookChannel` MUST POST a default JSON serialisation of the `Notification` with `application/json` content type.
- FR-062: `TeamsChannel` MUST build an Adaptive Card (schema `1.4`) by default; a custom `Template` overrides the card body. The card MUST surface `Title`, `Message`, and `EventId`/`CorrelationId` as facts, and colour by severity.
- FR-063: `EmailChannel` MUST use the `Template` (if set) as the email body; otherwise a default body composed from `Title`, `Message`, and `ErrorDetails`. The subject MUST be `[{Severity}] {Title}`.

#### HTTP delivery

- FR-070: `WebhookChannel` and `TeamsChannel` MUST obtain their `HttpClient` via `IHttpClientFactory` (typed or named client), mirroring the established pattern in `src/NimBus.WebApp/Startup.cs:389` (`services.AddHttpClient<TInterface, TImpl>((sp, http) => …)`). They MUST NOT `new HttpClient()` per send (socket exhaustion).
- FR-071: The `EmailChannel`'s SendGrid provider MUST also use an `IHttpClientFactory` client with bearer auth; the SMTP provider uses `System.Net.Mail.SmtpClient`.
- FR-072: All HTTP/SMTP sends MUST honour the supplied `CancellationToken` from `SendAsync` (the contract parameter at `INotificationChannel.cs:15`).

#### Registration surface

- FR-073: A fluent channel-builder MUST be provided so the registration reads close to the issue's intent. Because the real entry point is `INimBusBuilder.AddNotifications(...)` (`NotificationsExtension.cs:55,66`) and NOT `services.AddNimBusNotifications(...)`, the surface is an overload that takes a channel-builder action:
  ```csharp
  builder.AddNotifications(n =>
  {
      n.AddWebhook(opts => { opts.Url = "https://incident-bot.example.com/nimbus"; opts.MinSeverity = NotificationSeverity.Warning; });
      n.AddTeams(opts => { opts.ConnectorUrl = configuration["Teams:WebhookUrl"]; opts.MinSeverity = NotificationSeverity.Critical; });
      n.AddEmail(opts =>
      {
          opts.Provider = EmailProvider.SendGrid;
          opts.ApiKey = configuration["SendGrid:ApiKey"];
          opts.From = "alerts@example.com";
          opts.To = ["oncall@example.com"];
          opts.MinSeverity = NotificationSeverity.Critical;
      });
      n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20);
  });
  ```
  This overload sits alongside the existing `AddNotifications(configureOptions, configureChannels)` (`NotificationsExtension.cs:66`); the existing two-arg form continues to work unchanged.
- FR-074: `AddWebhook`/`AddTeams`/`AddEmail` MUST each register their channel as `INotificationChannel` AND register a corresponding `*Options` instance so the router can read each channel's `MinSeverity`. (The router resolves channels together with their options — e.g. channels register a small `ChannelRegistration(channel, options)` pair, or options are exposed on the channel instance.)
- FR-075: `WithRateLimit` MUST register the `TokenBucketRateLimiter` config consumed by `NotificationRouter`. Calling it more than once is last-wins.

#### Session-block trigger

- FR-080: "Session blocks" MUST become a real trigger source. Add a `NotifyOnSessionBlock` option (default `false`, opt-in) on `NotificationOptions` (alongside the existing `NotifyOnFailure`/`NotifyOnDeadLetter`/`NotifyOnReceived`/`NotifyOnCompleted` at `NotificationOptions.cs:11-26`). Because `SessionBlockedException` is currently swallowed at `src/NimBus.Core/Messages/MessageHandler.cs:83-85`, the handler MUST notify observers of the block before swallowing it (mirroring how `NotifyFailed` / `NotifyDeadLettered` are already invoked from the other catch blocks in `MessageHandler.cs`). The notification is built at `NotificationSeverity.Critical`.
- FR-081: If extending `IMessageLifecycleObserver` with a new method is judged too invasive (it is a public interface with default no-op methods — `IMessageLifecycleObserver.cs:12`), the fallback MUST be to map a session block onto the existing `OnMessageFailed`/`OnMessageDeadLettered` callback with a `SessionBlockedException`, so no interface change is needed. The chosen approach is recorded in Resolved/Open Questions. Either way, `MessageHandler.cs:83-85` MUST emit the lifecycle signal rather than silently swallowing.

#### Documentation & tests

- FR-090: `docs/notifications.md` MUST document the three channels, the severity-routing model, the rate-limit/dedup behaviour, the template placeholders, and the session-block opt-in. It MUST call out that the registration entry point is `AddNotifications`, not `AddNimBusNotifications`.
- FR-091: Unit tests MUST cover, with mocked `INotificationChannel`s: severity filtering (FR-030), short-circuit when all gated (FR-031), rate-limit suppression and burst (FR-040/FR-041), dedup on `(EventId, Severity)` within the window (FR-042), summary emission (FR-050), and template substitution (FR-060).
- FR-092: An integration test MUST stand up a local in-process webhook receiver (e.g. a minimal Kestrel endpoint or `HttpListener`) and assert that a routed notification produces a correctly-shaped POST. (Issue acceptance: "Integration test with a local webhook receiver.")

### Non-Functional Requirements

- NFR-001: Channel sends MUST NOT block message processing. The observer already isolates channel failures (`NotificationLifecycleObserver.cs:102-109`); the router preserves this and additionally guarantees that a slow channel honours the `CancellationToken`.
- NFR-002: The router's per-notification overhead (severity comparison + dedup lookup + token acquire) MUST be O(1) and lock-light. The dedup cache and rate limiter MUST be thread-safe for concurrent fan-out.
- NFR-003: No new NuGet dependency for the core routing/rate-limiting/HTTP path. `System.Threading.RateLimiting` and `IHttpClientFactory` are in the shared framework; `System.Net.Mail.SmtpClient` is in the BCL. SendGrid is reached via raw HTTPS (no SendGrid SDK dependency required).
- NFR-004: Large `ErrorDetails` (built from `exception?.ToString()` at `NotificationLifecycleObserver.cs:73`) MUST be truncated before transmission on size-constrained channels (Teams incoming webhook ~28 KB limit). Truncation appends a marker.
- NFR-005: Secrets (`ApiKey`, `SmtpPassword`, `ConnectorUrl`) MUST be sourced from `IConfiguration` / user-secrets / key vault, never logged. Channel send-failure logs MUST NOT include the auth header or full connector URL.
- NFR-006: The feature MUST be back-compatible: existing code calling `builder.AddNotifications()` or `builder.AddNotifications(configureOptions, configureChannels)` (`NotificationsExtension.cs:55-72`) continues to compile and behave identically (console default, fan-out-to-all). The router and per-channel `MinSeverity` engage only when the new fluent surface is used.
- NFR-007: Adding a channel MUST NOT require touching `NotificationLifecycleObserver` or `MessageHandler` (except the one-time FR-080 session-block wiring). New channels are pure `INotificationChannel` implementations.

## Key Entities

- **`INotificationChannel` / `Notification` / `NotificationSeverity`** — existing, unchanged contracts (`src/NimBus.Extensions.Notifications/INotificationChannel.cs`). All new work plugs into these.
- **`NotificationOptions`** — existing (`NotificationOptions.cs`); extended with `NotifyOnSessionBlock` (default `false`) per FR-080.
- **`NotificationLifecycleObserver`** — existing (`NotificationLifecycleObserver.cs`); modified to delegate to `INotificationRouter` (FR-021) and to emit a session-block notification (FR-080).
- **`INotificationRouter` / `NotificationRouter`** — new. Owns per-channel severity filtering, rate limiting, dedup, and the suppressed-storm summary.
- **`NotificationChannelOptions` (base) + `WebhookChannelOptions` / `TeamsChannelOptions` / `EmailChannelOptions`** — new. Carry `MinSeverity`, optional `Template`, and channel-specific config.
- **`WebhookChannel`** — new. HTTP POST with a configurable JSON template via `IHttpClientFactory`.
- **`TeamsChannel`** — new. Adaptive Card via Teams incoming webhook.
- **`EmailChannel` + `EmailProvider` enum** — new. SendGrid (HTTPS) and SMTP (`SmtpClient`).
- **`TokenBucketRateLimiter`** — `System.Threading.RateLimiting`; configured by `WithRateLimit(maxPerMinute, burstCapacity)`.
- **`ConsoleNotificationChannel`** — existing default (`ConsoleNotificationChannel.cs`); remains the no-channel-configured fallback (`NotificationsExtension.cs:38`).

## Success Criteria

### Measurable Outcomes

- SC-001: A dead-letter and a (opt-in) session block each produce exactly one delivery to every channel whose `MinSeverity` is satisfied — verified by an integration test against a local webhook receiver and mocked Teams/email channels.
- SC-002: With a webhook at `MinSeverity = Warning` and email at `MinSeverity = Critical`, an `Error`-severity notification reaches the webhook and never reaches the email channel — verified by unit test (FR-030).
- SC-003: A 100-notification burst under `WithRateLimit(maxPerMinute: 10, burstCapacity: 20)` delivers at most 20 notifications immediately and emits exactly one summary when the window clears — verified by unit test (FR-040/FR-050).
- SC-004: A failure event re-delivered within 5 minutes produces exactly one notification (the dedup drops the second) — verified by unit test (FR-042).
- SC-005: A throwing channel does not fail the underlying message and does not prevent sibling channels from receiving the notification — verified by unit test (NFR-001).
- SC-006: Existing callers (`AddNotifications()` / `AddNotifications(opts, channels)`) compile and behave identically after the change — verified by the existing notifications tests staying green (NFR-006).
- SC-007: No new NuGet `PackageReference` is added to `NimBus.Extensions.Notifications` for routing, rate limiting, HTTP, or SendGrid — verified by inspecting the `.csproj` diff (NFR-003).
- SC-008: `docs/notifications.md` exists and documents all three channels plus routing/rate-limit/dedup/session-block, with the correct `AddNotifications` entry point.

## Assumptions

- The real registration entry point is `INimBusBuilder.AddNotifications(...)` (`NotificationsExtension.cs:55,66`), NOT the `services.AddNimBusNotifications(n => { … })` shown in the issue. The fluent `n.AddWebhook/AddTeams/AddEmail/WithRateLimit` surface is delivered as a new `AddNotifications` overload taking a channel-builder action (FR-073). The issue's API is treated as illustrative intent, not a literal contract.
- The `Notification` model's `Severity` is the analogue of the issue's "Status" in the dedup key — the codebase has no `Status` field on `Notification` (`INotificationChannel.cs:21-61`); dedup uses `(EventId, Severity)` (FR-042).
- `System.Threading.RateLimiting` is available without an added package on the target framework (the codebase targets `net10.0`, where it is in the shared framework). To be verified at implementation time.
- `IHttpClientFactory` is the sanctioned HTTP pattern; the WebApp already uses typed clients (`Startup.cs:389`). Channels in the extension package will register their own typed/named clients through the same `services.AddHttpClient(...)` surface available on `builder.Services` (`INimBusBuilder.Services`, `INimBusBuilder.cs:13`).
- There is no WebApp "Alerting" page today (the `src/NimBus.WebApp/ClientApp/src/pages` listing has no alerting page). The issue's "completes WebApp Alerting checkbox" is interpreted as "the platform can now alert to production channels," satisfied at the SDK/extension layer. A WebApp UI for channel management is explicitly out of scope.
- Session-block detection currently swallows `SessionBlockedException` at `MessageHandler.cs:83-85`; this spec assumes that catch block is the correct single place to emit the new session-block lifecycle signal, consistent with how the other catch blocks already call `_lifecycleNotifier.NotifyFailed/NotifyDeadLettered` (`MessageHandler.cs:70,96,107`).
- SendGrid can be reached via its raw HTTPS `v3/mail/send` API without the SendGrid .NET SDK, keeping the dependency footprint at zero (NFR-003).

## Out of Scope

- A WebApp UI for configuring or viewing notification channels.
- First-class PagerDuty / Opsgenie channels (covered by `WebhookChannel` + template).
- Persisting a notification/alert history. The message store + WebApp blocked/dead-letter listings (`Schema/0007_BlockedInvalid.sql`) remain the durable record.
- Per-endpoint or per-event-type routing rules (routing is by severity only in v1).
- Channel-level retry/backoff beyond the single inline attempt.
- Adding new `NotificationSeverity` members or new `Notification` fields.
- Encrypting or signing notification payloads beyond transport TLS.

## Open Questions

- **Interface change vs. exception-mapping for the session-block trigger.** `IMessageLifecycleObserver` is a public interface with default no-op methods (`IMessageLifecycleObserver.cs:12`); adding `OnSessionBlocked` is source-compatible (defaulted) but is still a surface change consumers may need to know about. The alternative (FR-081) maps a block onto `OnMessageFailed` with a `SessionBlockedException`. *Leaning toward the explicit `OnSessionBlocked` method for clarity, but the final call is deferred to implementation review.*
- **Summary cadence for batched storms.** FR-050 emits one summary per cleared window. Should there instead be a periodic heartbeat summary while a storm is ongoing (e.g. every minute), so a multi-hour incident produces multiple "still failing" alerts rather than one at the end? *Default: one-summary-per-window; revisit if operators want a recurring heartbeat.*
- **Default email provider.** FR-010 defaults `EmailProvider` to `SendGrid`. Is SMTP the more common ops default for the NimBus install base? *Defaulted to SendGrid per the issue's primary example; SMTP is an equal, explicit choice — see Resolved Questions.*

## Resolved Questions

- **PagerDuty/Opsgenie OOTB?** Resolved — NO dedicated channels. Both expose generic incoming webhooks; `WebhookChannel` with a custom JSON template (FR-060) targets them. Keeps the channel count at three and avoids per-vendor maintenance.
- **Email: SendGrid primary, SMTP fallback, or both equal?** Resolved — both are equal first-class providers selected by `EmailProvider` (FR-010). `SendGrid` is the *default* value only because it is the issue's lead example; SMTP requires no API key and is the on-prem path. Neither is a "fallback" — exactly one is active per `EmailChannel` registration.
- **Acknowledgement/deduplication on redelivery.** Resolved — dedupe on `(EventId, Severity)` within a 5-minute window (FR-042/FR-043), per the issue's recommendation (its "Status" maps to the real model's `Severity`). A re-delivered failure event does not re-notify within the window.
- **Severity gating location.** Resolved — gating lives in `INotificationRouter` (FR-030), NOT in each channel's `SendAsync`, so the existing `INotificationChannel` contract is unchanged and custom channels need no awareness of routing.
- **New dependencies.** Resolved — none. `System.Threading.RateLimiting`, `IHttpClientFactory`, and `System.Net.Mail.SmtpClient` cover rate limiting, HTTP, and SMTP; SendGrid is raw HTTPS (NFR-003).
- **Back-compatibility of the registration surface.** Resolved — the new fluent overload is additive; `AddNotifications()` and `AddNotifications(configureOptions, configureChannels)` (`NotificationsExtension.cs:55-72`) keep their exact current behaviour, including the `ConsoleNotificationChannel` default and fan-out-to-all-channels when no router is configured.
