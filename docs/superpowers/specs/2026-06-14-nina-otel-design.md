# NINA OpenTelemetry Plugin Design

Date: 2026-06-14
Status: Design approved for spec review

## Goal

Build a NINA plugin that exports observability data to an OpenTelemetry Collector using OTLP only. The plugin should provide the same core equipment and image telemetry as the existing NINA InfluxDB exporter, add workflow traces and filtered useful logs, and support optional add-ons for non-core integrations such as PHD2, Target Scheduler, Night Summary, and equipment-specific controller protocols.

The plugin must not block NINA startup, the NINA UI thread, or the core NinaOtel pipeline when an add-on, network connection, log tailer, equipment controller, VPN, or OTLP collector is slow or unreachable.

## Scope

In scope for v1:

- A `net8.0-windows` NINA plugin loaded through the normal NINA plugin mechanism.
- OTLP export to a user-configured OpenTelemetry Collector.
- Metrics, traces, and logs.
- Core NINA telemetry from NINA-native APIs and mediators.
- A first-party optional add-on model.
- First-party optional add-ons for PHD2, Target Scheduler, Night Summary, and OnStepX/JTW Trident.
- Filtered useful log/event export by default.
- Per-source raw forwarding as an opt-in debugging feature.
- Memory-first buffering with disk spill only when the collector/export path is unhealthy.
- OTLP auth support including headers and mTLS.

Out of scope for v1:

- Vendor-specific exporters or presets such as InfluxDB, Grafana, Elastic, or Prometheus direct export.
- Direct hard dependencies on Target Scheduler, Night Summary, PHD2, or mount-specific assemblies in the core plugin.
- Replay/import of historical nights as a primary workflow.
- Full OAuth2 client-credentials, OIDC, SigV4, or other dynamic auth providers inside the plugin. These can be handled by a local collector or proxy initially.
- Direct biometric authentication. Any "certificate fingerprint" wording refers only to certificate identification.

## Reference Findings

The InfluxDB exporter reference exports equipment, image, guider, weather, switch, and sequence event data from NINA mediators and image-save events. Its telemetry surface is a good baseline, but its direct write-per-event approach should not be copied. NinaOtel should use a shared bounded pipeline with batching, backpressure, and failure isolation.

Night Summary captures rich session and image data, including target/filter/exposure, image statistics, guiding, Target Scheduler grading, safety, autofocus, meridian flips, and timing events. For v1, Night Summary is an add-on and should be consumed through log breadcrumbs first. SQLite/report parsing can be added later behind the same add-on boundary.

NINA, Target Scheduler, and PHD2 logs have different shapes:

- NINA logs are pipe-delimited records with multiline exceptions.
- Target Scheduler logs are pipe-delimited records with multiline planning/message blocks.
- PHD2 GuideLog files are session headers plus dense CSV guide samples.
- PHD2 DebugLog files are very verbose but contain useful state, dither, settle, JSON event-server, and capture-error records.

The current OpenTelemetry .NET OTLP exporter supports endpoint, protocol, headers, timeout, compression, and mTLS certificate options on .NET 8+. The design should expose those concepts through NINA plugin settings rather than relying only on process environment variables.

## Architecture

The architecture has three layers:

1. Core NINA plugin
2. Optional add-ons
3. OTLP export pipeline

### Core NINA Plugin

The core plugin is responsible for:

- NINA plugin lifecycle and profile settings.
- OTel resource attributes and instrumentation naming.
- OTLP exporter configuration.
- Normalized telemetry contracts.
- Bounded in-memory telemetry intake.
- Memory-first, disk-on-failure buffering.
- Add-on discovery, lifecycle, isolation, status, and configuration.
- NINA-native telemetry collectors.

Core telemetry must be limited to data available through NINA itself:

- NINA mediators and equipment state.
- Image-save events.
- NINA-exposed guider metrics/state.
- Sequence lifecycle data available through NINA APIs or core NINA logs.
- NINA warning/error/fatal logs and recognized NINA workflow events.

Core must not contain Target Scheduler, Night Summary, PHD2, OnStepX, JTW Trident, or other external-plugin/hardware-specific code.

### Optional Add-Ons

Anything tied to another application, another NINA plugin, or specific hardware lives in an add-on.

First-party add-ons for v1:

- `NinaOtel.Addons.PHD2`
- `NinaOtel.Addons.TargetScheduler`
- `NinaOtel.Addons.NightSummary`
- `NinaOtel.Addons.OnStepX`

Add-ons may ship in the same repository and package, but each must be independently enableable and independently failing. Disabled or broken add-ons must not prevent core startup or core telemetry export.

Add-on responsibilities:

- Connect to or tail their own data source.
- Parse source-specific records.
- Publish normalized metrics, logs, spans, and health records through the core sink.
- Own source-specific configuration such as file paths, TCP endpoint, polling interval, raw debug forwarding, and command timeout.

Add-ons must not receive raw OTel SDK objects. They publish through a small core contract so that pipeline, buffering, and export policy remain centralized.

### OTLP Export Pipeline

The export pipeline is responsible for:

- Receiving normalized telemetry from core and add-ons.
- Converting normalized telemetry into OTel logs, metrics, and spans.
- Applying resource attributes consistently.
- Batching and exporting through OTLP.
- Detecting collector/export failures.
- Spilling to disk only during degraded export conditions.
- Reporting internal health and drop counters.

OTLP Collector is the only export target.

## Add-On Contract

Core exposes a minimal add-on API:

- `ITelemetryAddon`
  - Metadata: name, version, source type, supported config version.
  - Validate configuration.
  - Start and stop lifecycle.
  - Report enabled/disabled state.

- `ITelemetrySink`
  - Publish metric samples.
  - Publish log records.
  - Publish span lifecycle records.
  - Publish add-on health/status records.

- `IAddonContext`
  - Cancellation tokens.
  - Clock/time provider.
  - Core logger.
  - Per-add-on configuration.
  - Safe helpers for file tailing and network operations.
  - Access to the telemetry sink.

The host wraps all add-on lifecycle calls. Add-ons are expected to be well-behaved, but core still treats them as unreliable inputs.

## Non-Blocking Isolation

Non-blocking behavior is a hard requirement.

Rules:

- No synchronous TCP, file, or OTLP IO on the NINA UI thread.
- No synchronous TCP, file, or OTLP IO in the plugin constructor path.
- Core startup must not wait for add-on connections.
- Add-on `StartAsync` returns quickly after scheduling worker loops.
- Connection attempts happen inside add-on background workers.
- Every connect, read, write, query, and export operation has a timeout and cancellation token.
- Add-on retries use backoff.
- Add-on failures emit health telemetry but do not stop core or other add-ons.
- Shutdown gives add-ons a bounded stop deadline. Missed deadlines are logged and core continues unloading.

For the OnStepX/JTW Trident add-on, TCP connect and polling must be fully async with per-command deadlines. Mount communication loss is telemetry, not a plugin failure.

## Buffering and Collector Outages

The collector may be unreachable because it is across a VPN. The plugin should preserve useful telemetry during collector outages, but it should not hit disk during healthy operation.

The buffering model is:

- Healthy path:
  - Producers publish into bounded memory queues.
  - OTLP batches are exported directly from memory.
  - No disk spool writes.

- Failure detection:
  - If export fails, times out, or the collector is marked unhealthy, the pipeline enters degraded mode.
  - The current unsent batch and recent in-memory grace buffer are eligible for disk spill.

- Degraded mode:
  - New telemetry is written to a local durable spool.
  - Export attempts continue in the background with backoff.
  - Disk usage is bounded by configured max bytes and max age.

- Recovery:
  - When exports succeed, the sender drains disk spool with original timestamps.
  - Flush is rate-limited.
  - Live telemetry continues to be accepted.
  - Once the spool is empty and the collector has been stable for a configured window, the pipeline returns to healthy mode and stops disk writes.

Default spool location:

- `%LOCALAPPDATA%\NINA\NinaOtel\spool`

Suggested conservative defaults:

- Max disk usage: 1 GB
- Max age: 7 days
- Memory grace buffer: bounded by record count and/or time

Drop policy when memory or disk limits are reached:

1. Raw debug forwarding records.
2. Dense/high-frequency samples.
3. Routine info lifecycle events.
4. Routine gauge metrics that can be coalesced.
5. Normal metrics.
6. Safety-critical logs/events, workflow failure spans, mount limit hits, and motor faults are retained as long as possible.

Repeated gauge metrics should be coalesced into time buckets during long outages. Logs, spans, image facts, session facts, and safety/fault events should be preserved more faithfully.

Internal health metrics/logs should include:

- Collector reachable/unreachable.
- Current mode: healthy or degraded.
- Queued records.
- Queued bytes.
- Oldest queued timestamp.
- Dropped records by reason and priority.
- Last export error category.

## Telemetry Model

### Metrics

Core NINA metrics include:

- Camera sensor temperature, cooler power, battery level.
- Focuser position and temperature.
- Mount altitude and azimuth.
- Rotator angle and mechanical angle.
- Weather and safety values.
- Filter wheel current filter as an attribute-bearing state metric.
- Switch values.
- NINA-exposed guider RMS, RA/Dec distances, durations, and peak values.
- Image statistics such as mean, median, standard deviation, MAD, min, max, star count, HFR, FWHM, and eccentricity when available.

Metric attributes should include stable context such as profile, equipment name/type, target, filter, exposure length, binning, gain, offset, image type, and addon/source where applicable.

Add-on metrics:

- PHD2 add-on may emit aggregated guide metrics and state metrics. Dense per-frame CSV export is not enabled by default.
- Target Scheduler add-on may emit plan/target/image grading counters and current target state.
- Night Summary add-on may emit session/image summary metrics when available.
- OnStepX add-on may emit controller state, motor state, limits, voltage/current/temperature if available through the protocol.

### Traces

Traces model observing workflow.

Candidate spans:

- `nina.session`
- `nina.target`
- `nina.exposure`
- `nina.camera_download`
- `nina.image_save`
- `nina.autofocus`
- `nina.centering`
- `nina.platesolve`
- `nina.dither_settle`
- `nina.meridian_flip`
- `nina.filter_change`
- `nina.slew`
- `nina.safety_wait`

Target Scheduler and Night Summary add-ons can enrich spans when they can safely correlate records by time, target name, image id, or source-provided correlation id.

Failures should set span status and attach error details as span events and/or correlated logs.

### Logs

Default logs are filtered for useful signal:

- Warning, error, and fatal records from enabled sources.
- Recognized lifecycle events.
- Safety state changes.
- Mount/controller limit and motor faults.
- PHD2 capture errors and guiding state problems.
- Target Scheduler plan/target/image grading events.
- Night Summary session, report, safety, autofocus, and meridian flip breadcrumbs.

Raw forwarding is disabled by default. It is configurable per source/add-on as a debugging feature.

Multiline logs must be grouped before parsing/export:

- NINA and Target Scheduler continuation lines belong to the previous timestamped record.
- PHD2 DebugLog records use their own timestamp format.
- PHD2 GuideLog CSV rows are parsed as samples/events, not raw logs by default.

## First-Party Add-Ons

### PHD2 Add-On

Inputs:

- PHD2 GuideLog files.
- PHD2 DebugLog files.
- Future PHD2 API/socket integration may be added behind the same add-on.

Default exported signal:

- Guiding begins/ends.
- Settling starts/completes.
- Dither events.
- Capture errors.
- State changes that indicate guiding interruption or recovery.
- Aggregated guide quality metrics, not every raw CSV row.

### Target Scheduler Add-On

Inputs:

- Target Scheduler logs.
- Future Target Scheduler artifacts/API if available.

Default exported signal:

- Planning runs.
- Selected target and filter.
- Plan start/stop/min-expire/hard-stop.
- Published target start messages.
- Image save watcher and image grading events.
- Rejection/acceptance/grade status when parsable.
- Warnings/errors.

### Night Summary Add-On

Inputs:

- Night Summary breadcrumbs from NINA logs.
- Future Night Summary SQLite/report parsing.

Default exported signal:

- Session start/end.
- Stored camera info.
- Roof open/closed safety events.
- Autofocus events.
- Meridian flip events.
- Target Scheduler grading sync summary.
- Report generation and delivery status.

### OnStepX/JTW Trident Add-On

Inputs:

- OnStepX-compatible TCP protocol endpoint.

Default exported signal:

- Connection state.
- Controller/mount state.
- Tracking state.
- Limit state.
- Motor fault/error state.
- Communications loss and recovery.
- Additional voltage, current, temperature, and status metrics when available.

Polling must be conservative and configurable. All commands must have individual deadlines.

## OTLP Configuration and Authentication

The NINA options UI should expose:

- OTLP endpoint.
- OTLP protocol: gRPC or HTTP/protobuf.
- Timeout.
- Compression.
- Resource attributes.
- Static headers.
- Optional signal-specific endpoints/headers/timeouts if needed later.

Authentication support:

- Static headers for arbitrary collector auth.
- Helper UI for Bearer token header.
- Helper UI for Basic auth header.
- Optional bearer token from file for externally rotated tokens.
- mTLS using PEM paths:
  - Trusted CA certificate file.
  - Client certificate file.
  - Client private key file.
- Optional Windows-friendly inputs if feasible:
  - PFX/P12 client certificate file plus password.
  - Windows certificate store lookup by certificate fingerprint.

Implementation note: Windows/.NET APIs often name the certificate fingerprint field `Thumbprint`. User-facing text should use "certificate fingerprint" to avoid biometric confusion.

Secrets must be masked in the UI and logs. Stored secrets should use Windows DPAPI or an equivalent protected storage mechanism, not plain profile text.

The plugin should not expose certificate-validation bypass in normal configuration. If a bypass exists for debugging, it must be explicitly marked unsafe and excluded from recommended setup. Private CA support is the correct path for VPN, homelab, or self-signed deployments.

Full OAuth2 client-credentials, OIDC token acquisition, and SigV4 signing are deferred. Users needing those in v1 should terminate auth in a local collector, gateway, or proxy.

## Configuration UI

The plugin options UI should include:

- Main OTLP exporter settings.
- Authentication settings.
- Core telemetry category toggles:
  - Equipment.
  - Image statistics.
  - Sequence/workflow traces.
  - Filtered logs.
  - Raw forwarding debug toggles.
- Add-on list with enable/disable and status.
- Per-add-on settings:
  - PHD2 log paths and raw debug forwarding.
  - Target Scheduler log path and raw debug forwarding.
  - Night Summary log/SQLite paths and raw debug forwarding.
  - OnStepX host, port, polling interval, and command timeout.
- Buffer settings:
  - Memory queue size.
  - Disk-on-failure enabled.
  - Spool path.
  - Max spool bytes.
  - Max spool age.
  - Recovery flush rate.
- Status view:
  - Collector health.
  - Current buffer mode.
  - Add-on health.
  - Queued records/bytes.
  - Oldest queued timestamp.
  - Dropped records.
  - Last export error.

## Error Handling

Core principles:

- Fail closed for unsafe auth configuration.
- Fail open for optional telemetry add-ons.
- Never block NINA operation for telemetry.
- Prefer bounded queues and explicit drops over unbounded memory growth.
- Emit internal health telemetry for exporter/add-on failures.
- Do not log secrets, full auth headers, private key paths with passwords, or token values.

Specific behavior:

- Bad OTLP endpoint: mark collector unhealthy, enter degraded mode if telemetry is produced.
- Bad auth or TLS config: show validation error in UI when detectable; otherwise export failure enters degraded mode and reports the error class.
- Add-on parse error: count and log sample-limited parser errors; continue tailing.
- Add-on connection timeout: emit health update and retry with backoff.
- Spool full: apply drop policy and emit drop counters.
- Plugin unload: bounded drain/flush, then bounded stop for add-ons.

## Testing Strategy

Parser tests:

- NINA pipe-delimited logs with multiline exceptions.
- Target Scheduler multiline planning/message blocks.
- PHD2 GuideLog headers, CSV samples, dither, settle, guide start/end.
- PHD2 DebugLog state changes, JSON event-server records, capture errors.
- Night Summary breadcrumbs in NINA logs.

Pipeline tests:

- Bounded memory queue behavior.
- Drop priority behavior.
- Collector unreachable.
- Transition healthy -> degraded -> recovery -> healthy.
- Disk spool max bytes and max age.
- Original timestamp preservation during replay.
- Live telemetry accepted while spool drains.

Add-on isolation tests:

- Add-on connection timeout does not block core startup.
- Add-on read timeout does not block publishing.
- Add-on exception does not stop core or other add-ons.
- Add-on stop timeout does not block plugin unload beyond the configured deadline.

OTLP/auth tests:

- Static headers.
- Bearer helper.
- Basic helper.
- Token file reload.
- mTLS PEM paths.
- Invalid CA/client certificate handling.
- Secret masking.

NINA integration smoke tests:

- Plugin loads.
- Settings persist by profile.
- Core collectors can start/stop.
- Add-ons can be enabled/disabled.
- Plugin unload disposes providers, tailers, background tasks, and network clients.

## Implementation Notes

Use the current stable `NINA.Plugin` package for NINA 3.2 compatibility unless implementation scouting shows a strong reason to target a newer nightly API.

Use the current stable `OpenTelemetry.Exporter.OpenTelemetryProtocol` package available at implementation time. As of scouting, `1.16.0` is current and includes mTLS support introduced in `1.15.0`.

Prefer the official OTel SDK exporter where it fits, but keep the NinaOtel memory-first/disk-on-failure policy explicit. Do not enable always-on disk retry in healthy operation.

The normalized telemetry model should remain independent of add-on internals so future sidecar/process-isolated add-ons can be introduced without changing the core plugin.

## Approved Design Decisions

- OTLP Collector only.
- Metrics, logs, and traces are all in scope.
- Raw forwarding is opt-in only.
- Core handles NINA-native telemetry only.
- PHD2, Target Scheduler, Night Summary, and OnStepX/JTW Trident are add-ons.
- Add-ons must not block NINA or the core NinaOtel plugin.
- Collector outages use memory-first buffering and disk spill only on failure.
- mTLS and common header-based auth are in scope.
