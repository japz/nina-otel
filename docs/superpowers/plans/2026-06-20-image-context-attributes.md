# NinaOtel Image Context Attributes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add structured image context attributes for image type, filter, and exposure duration to existing image metrics, logs, and spans.

**Architecture:** Use the existing `ImageTelemetryCollector.CreateAttributes` base attribute map so every image metric and image-save log/span receives the same context. Keep exposure span behavior unchanged except that it can reuse the same duration attribute value. Add the attributes to `NinaMetricCatalog.ImageAttributes` so deferred point-in-time image metrics preserve them through OTLP serialization.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, existing NINA image-save metadata contracts.

---

## Target File Structure

- Modify: `src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs`
  - Add `image_type`, `filter_name`, and `exposure_duration_seconds` to the image metric attribute allow-list.
- Modify: `src/NinaOtel.Plugin/Telemetry/ImageTelemetryCollector.cs`
  - Add the three attributes to the shared image base attributes when values are available.
  - Use finite, non-negative exposure duration only.
- Modify: `tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs`
  - Assert image metric attributes include the new context keys.
- Modify: `tests/NinaOtel.Plugin.Tests/Telemetry/ImageTelemetryCollectorTests.cs`
  - Assert complete image metrics, image log, image-save span, and exposure spans carry the new attributes.
  - Assert sparse/invalid metadata does not invent empty or invalid values.

## Non-Goals For This Slice

- No new metrics.
- No new collector.
- No optional add-ons.
- No sequencer instruction equivalent.
- No changes to image log body text.
- No changes to span names or trace state behavior.

## Task 1: Image Metric Catalog Attributes

**Files:**
- Modify: `tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs`
- Modify: `src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs`

- [ ] **Step 1: Write failing catalog assertions**

In `All_ClassifiesLiveEquipmentAndDeferredImageMetrics`, after `imageMean.AttributeNames.Should().Contain("image_file_name");`, add:

```csharp
imageMean.AttributeNames.Should().Contain(
    "image_type",
    "filter_name",
    "exposure_duration_seconds");
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~NinaMetricCatalogTests --no-restore -v:minimal
```

Expected locally: the test project may be blocked by `NU1301`. Expected in CI before implementation: assertion fails because the attributes are missing.

- [ ] **Step 2: Implement catalog allow-list update**

In `NinaMetricCatalog.ImageAttributes`, append:

```csharp
"image_type",
"filter_name",
"exposure_duration_seconds",
```

Run the focused catalog test command again. Expected in GitHub CI: pass.

## Task 2: Image Collector Attributes

**Files:**
- Modify: `tests/NinaOtel.Plugin.Tests/Telemetry/ImageTelemetryCollectorTests.cs`
- Modify: `src/NinaOtel.Plugin/Telemetry/ImageTelemetryCollector.cs`

- [ ] **Step 1: Write failing complete-image assertions**

In `ImageSaved_WhenSnapshotIsComplete_PublishesImageStatisticsStarMetricsRmsMetricsAndAttributes`, add these checks to the `metrics.Should().OnlyContain` predicate:

```csharp
Equals(record.Attributes["image_type"], "LIGHT") &&
Equals(record.Attributes["filter_name"], "L") &&
Equals(record.Attributes["exposure_duration_seconds"], 120.5) &&
```

In `ImageSaved_WhenSnapshotIsComplete_PublishesReferenceImageLog`, add:

```csharp
log.Attributes.Should().Contain("image_type", "LIGHT");
log.Attributes.Should().Contain("filter_name", "L");
log.Attributes.Should().Contain("exposure_duration_seconds", 120.5);
```

In `ImageSaved_PublishesCompletedImageSaveSpanWithMetadataAndTimingContext`, add:

```csharp
span.Attributes.Should().Contain("image_type", "LIGHT");
span.Attributes.Should().Contain("filter_name", "L");
span.Attributes.Should().Contain("exposure_duration_seconds", 120.5);
```

In `ImageSaved_WhenExposureDurationIsAvailable_PublishesExposureStartAndStopSpans`, add:

```csharp
startSpan.Attributes.Should().Contain("image_type", "LIGHT");
startSpan.Attributes.Should().Contain("filter_name", "L");
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --filter FullyQualifiedName~ImageTelemetryCollectorTests --no-restore -v:minimal
```

Expected locally: the test project may be blocked by `NU1301`. Expected in CI before implementation: assertions fail because the attributes are missing from the base map.

- [ ] **Step 2: Write sparse/invalid metadata assertions**

In `ImageSaved_WhenMetadataIsSparse_PublishesSafeReferenceImageLog`, add:

```csharp
log.Attributes.Should().NotContainKey("image_type");
log.Attributes.Should().NotContainKey("filter_name");
log.Attributes.Should().NotContainKey("exposure_duration_seconds");
```

In `ImageSaved_WhenExposureDurationIsNotValid_DoesNotPublishExposureSpan`, after the existing assertions, add:

```csharp
sink.Records.Should().NotContain(record => record.Attributes.ContainsKey("exposure_duration_seconds"));
```

- [ ] **Step 3: Implement collector base attributes**

In `ImageTelemetryCollector.CreateAttributes`, after existing metadata attributes, add:

```csharp
AddIfPresent(attributes, "image_type", args.MetaData?.Image?.ImageType);
AddIfPresent(attributes, "filter_name", args.Filter);
if (TryNormalizeExposureDuration(args.Duration, out var exposureDurationSeconds))
{
    attributes["exposure_duration_seconds"] = exposureDurationSeconds;
}
```

Add helper:

```csharp
private static bool TryNormalizeExposureDuration(double durationSeconds, out double normalized)
{
    normalized = durationSeconds;
    return double.IsFinite(durationSeconds) && durationSeconds >= 0;
}
```

Update `TryCreateExposureWindow` to reuse the same helper:

```csharp
if (!TryNormalizeExposureDuration(args.Duration, out durationSeconds) || durationSeconds <= 0)
{
    return false;
}
```

Run the focused image collector test command again. Expected in GitHub CI: pass.

## Task 3: Verification And Commit

**Files:**
- Modified files from Tasks 1 and 2.

- [ ] **Step 1: Run formatting/diff check**

Run:

```bash
git diff --check
```

Expected: exit code `0`.

- [ ] **Step 2: Run local build checks**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Core/NinaOtel.Core.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build src/NinaOtel.Plugin/NinaOtel.Plugin.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-restore -v:minimal
DOTNET=$(mise which dotnet); "$DOTNET" build tests/NinaOtel.Plugin.Tests/NinaOtel.Plugin.Tests.csproj --no-restore -v:minimal
```

Expected locally: core may build; plugin/test builds may report `NU1301`. Do not claim local success unless the command exits `0`.

- [ ] **Step 3: Commit behavior**

Run:

```bash
git add src/NinaOtel.Core/Telemetry/NinaMetricCatalog.cs src/NinaOtel.Plugin/Telemetry/ImageTelemetryCollector.cs tests/NinaOtel.Core.Tests/Telemetry/NinaMetricCatalogTests.cs tests/NinaOtel.Plugin.Tests/Telemetry/ImageTelemetryCollectorTests.cs
git commit -m "Add image context attributes to telemetry"
```

## Self-Review Checklist

- Spec coverage: the plan adds the three requested context attributes to image metric allow-list and collector output.
- Placeholder scan: no TODO/TBD placeholders.
- Type consistency: attribute keys are identical in tests, collector, and catalog.
- Scope control: no new telemetry signals, add-ons, or UI changes in this slice.
