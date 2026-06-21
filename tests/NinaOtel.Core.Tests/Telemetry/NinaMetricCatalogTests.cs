using FluentAssertions;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Telemetry;

public sealed class NinaMetricCatalogTests
{
    [Fact]
    public void All_IncludesEveryInitialInfluxExporterMeasurementName()
    {
        string[] expectedMeasurementNames =
        [
            "astro_moon_altitude",
            "astro_sun_altitude",
            "camera_battery_level",
            "camera_cooler_power",
            "camera_sensor_temperature",
            "focuser_position",
            "focuser_temperature",
            "fwheel_filter",
            "guider_dec_distance",
            "guider_dec_duration",
            "guider_ra_distance",
            "guider_ra_duration",
            "guider_rms_arcsec",
            "guider_rms_dec_arcsec",
            "guider_rms_dec_pixel",
            "guider_rms_peak_arcsec",
            "guider_rms_peak_dec_arcsec",
            "guider_rms_peak_dec_pixel",
            "guider_rms_peak_pixel",
            "guider_rms_peak_ra_arcsec",
            "guider_rms_peak_ra_pixel",
            "guider_rms_pixel",
            "guider_rms_ra_arcsec",
            "guider_rms_ra_pixel",
            "image_eccentricity",
            "image_fwhm",
            "image_hfr",
            "image_hfr_std_deviation",
            "image_mad",
            "image_max_adu",
            "image_max_adu_count",
            "image_mean",
            "image_median",
            "image_min_adu",
            "image_min_adu_count",
            "image_rms_avg_arcsec",
            "image_rms_avg_dec_arcsec",
            "image_rms_avg_ra_arcsec",
            "image_rms_peak_arcsec",
            "image_rms_peak_dec_arcsec",
            "image_rms_peak_ra_arcsec",
            "image_star_count",
            "image_std_deviation",
            "mount_altitude",
            "mount_azimuth",
            "qhy_sensor_air_pressure",
            "qhy_sensor_humidity",
            "rotator_angle",
            "rotator_mechanical_angle",
            "wx_cloud_cover",
            "wx_dewpoint",
            "wx_humidity",
            "wx_pressure",
            "wx_rain_rate",
            "wx_sky_brightness",
            "wx_sky_quality",
            "wx_sky_temperature",
            "wx_star_fwhm",
            "wx_temperature",
            "wx_wind_direction",
            "wx_wind_gust",
            "wx_wind_speed",
        ];
        var catalogNames = NinaMetricCatalog.All.Select(static metric => metric.Name).ToHashSet(StringComparer.Ordinal);

        var missingMeasurementNames = expectedMeasurementNames
            .Except(catalogNames, StringComparer.Ordinal)
            .ToArray();

        missingMeasurementNames.Should().BeEmpty(
            "NinaOtel's metric catalog should preserve every initial nina-influxdb-exporter measurement name");
    }

    [Fact]
    public void All_IncludesInitialInfluxExporterEquipmentMetrics()
    {
        var names = NinaMetricCatalog.All.Select(static metric => metric.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(
            "astro_moon_altitude",
            "astro_sun_altitude",
            "camera_sensor_temperature",
            "camera_cooler_power",
            "camera_battery_level",
            "qhy_sensor_air_pressure",
            "qhy_sensor_humidity",
            "focuser_temperature",
            "focuser_position",
            "mount_altitude",
            "mount_azimuth",
            "rotator_mechanical_angle",
            "rotator_angle",
            "fwheel_filter",
            "wx_cloud_cover",
            "wx_temperature",
            "wx_wind_speed");
    }

    [Fact]
    public void All_IncludesInitialInfluxExporterGuiderAndImageMetrics()
    {
        var names = NinaMetricCatalog.All.Select(static metric => metric.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(
            "guider_rms_ra_arcsec",
            "guider_rms_dec_arcsec",
            "guider_rms_arcsec",
            "guider_rms_peak_ra_pixel",
            "guider_ra_distance",
            "guider_dec_duration",
            "image_mean",
            "image_median",
            "image_std_deviation",
            "image_hfr",
            "image_fwhm",
            "image_eccentricity",
            "image_star_count",
            "image_rms_peak_arcsec");
    }

    [Fact]
    public void All_IncludesSafetyMonitorMetrics()
    {
        var metric = NinaMetricCatalog.All.Should()
            .ContainSingle(static metric => metric.Name == "safety_issafe")
            .Subject;

        metric.Category.Should().Be("safety");
        metric.ValueKind.Should().Be("double");
        metric.ExportKind.Should().Be(NinaMetricExportKind.LiveObservableGauge);
        metric.AttributeNames.Should().Contain(
            "profile_name",
            "host_name",
            "safety_monitor_name");
        NinaMetricCatalog.IsLiveObservableGauge("safety_issafe").Should().BeTrue();
    }

    [Fact]
    public void All_ClassifiesLiveEquipmentAndDeferredImageMetrics()
    {
        var cameraTemperature = NinaMetricCatalog.All.Should()
            .ContainSingle(static metric => metric.Name == "camera_sensor_temperature")
            .Subject;
        var imageMean = NinaMetricCatalog.All.Should()
            .ContainSingle(static metric => metric.Name == "image_mean")
            .Subject;

        cameraTemperature.ExportKind.Should().Be(NinaMetricExportKind.LiveObservableGauge);
        NinaMetricCatalog.IsLiveObservableGauge("camera_sensor_temperature").Should().BeTrue();

        imageMean.ExportKind.Should().Be(NinaMetricExportKind.DeferredPointInTime);
        imageMean.AttributeNames.Should().Contain("image_file_name");
        imageMean.AttributeNames.Should().Contain(
            [
                "image_type",
                "filter_name",
                "exposure_duration_seconds",
            ]);
        NinaMetricCatalog.IsLiveObservableGauge("image_mean").Should().BeFalse();
    }

    [Fact]
    public void All_IncludesDeferredPhd2GuideSummaryMetrics()
    {
        string[] metricNames =
        [
            "phd2_guide_rms_ra_pixel",
            "phd2_guide_rms_dec_pixel",
            "phd2_guide_rms_pixel",
            "phd2_guide_sample_count",
        ];

        foreach (var metricName in metricNames)
        {
            var metric = NinaMetricCatalog.All.Should()
                .ContainSingle(candidate => candidate.Name == metricName)
                .Subject;

            metric.Category.Should().Be("phd2");
            metric.ExportKind.Should().Be(NinaMetricExportKind.DeferredPointInTime);
            metric.AttributeNames.Should().Contain(
                "profile_name",
                "host_name",
                "addon.id",
                "source",
                "guider_name",
                "source.file",
                "phd2.session_start");
            NinaMetricCatalog.IsLiveObservableGauge(metricName).Should().BeFalse();
        }
    }

    [Fact]
    public void All_IncludesDeferredPhd2GuidePulseMetricsWithPulseAttributes()
    {
        string[] metricNames =
        [
            "phd2_guide_ra_pulse_distance_pixel",
            "phd2_guide_ra_pulse_duration_ms",
            "phd2_guide_dec_pulse_distance_pixel",
            "phd2_guide_dec_pulse_duration_ms",
        ];

        foreach (var metricName in metricNames)
        {
            var metric = NinaMetricCatalog.All.Should()
                .ContainSingle(candidate => candidate.Name == metricName)
                .Subject;

            metric.Category.Should().Be("phd2");
            metric.ExportKind.Should().Be(NinaMetricExportKind.DeferredPointInTime);
            metric.AttributeNames.Should().Contain(
                "profile_name",
                "host_name",
                "addon.id",
                "source",
                "guider_name",
                "source.file",
                "phd2.session_start",
                "phd2.ra_direction",
                "phd2.dec_direction");
            NinaMetricCatalog.GetMetricAttributeNames(metricName, NinaMetricExportKind.DeferredPointInTime)
                .Should()
                .NotBeNull()
                .And.Contain(
                    "phd2.ra_direction",
                    "phd2.dec_direction");
            metric.AttributeNames.Should().NotContain("phd2.frame");
            NinaMetricCatalog.IsLiveObservableGauge(metricName).Should().BeFalse();
        }
    }

    [Fact]
    public void All_IncludesDeferredTargetSchedulerMetricsWithStableAttributes()
    {
        string[] metricNames =
        [
            "target_scheduler_planning_run_count",
            "target_scheduler_planning_run_completed_count",
            "target_scheduler_target_selected_count",
            "target_scheduler_current_target",
            "target_scheduler_plan_started_count",
            "target_scheduler_plan_stopped_count",
            "target_scheduler_image_graded_count",
            "target_scheduler_image_grade_score",
        ];

        foreach (var metricName in metricNames)
        {
            var metric = NinaMetricCatalog.All.Should()
                .ContainSingle(candidate => candidate.Name == metricName)
                .Subject;

            metric.Category.Should().Be("target_scheduler");
            metric.ExportKind.Should().Be(NinaMetricExportKind.DeferredPointInTime);
            metric.AttributeNames.Should().Contain(
                "profile_name",
                "host_name",
                "addon.id",
                "source",
                "source.file",
                "event.kind",
                "target.name",
                "filter.name",
                "grade.status",
                "stop.reason");
            metric.AttributeNames.Should().NotContain(
                "message",
                "raw.line");
            NinaMetricCatalog.GetMetricAttributeNames(metricName, NinaMetricExportKind.DeferredPointInTime)
                .Should()
                .NotBeNull()
                .And.Contain(
                    "target.name",
                    "filter.name",
                    "grade.status",
                    "stop.reason");
            NinaMetricCatalog.IsLiveObservableGauge(metricName).Should().BeFalse();
        }
    }

    [Fact]
    public void All_IncludesDeferredNightSummaryCountMetricsWithStableAttributes()
    {
        string[] metricNames =
        [
            "night_summary_session_started_count",
            "night_summary_session_ended_count",
            "night_summary_report_started_count",
            "night_summary_report_delivered_count",
            "night_summary_report_failed_count",
            "night_summary_autofocus_completed_count",
            "night_summary_meridian_flip_count",
        ];

        foreach (var metricName in metricNames)
        {
            var metric = NinaMetricCatalog.All.Should()
                .ContainSingle(candidate => candidate.Name == metricName)
                .Subject;

            metric.Category.Should().Be("night_summary");
            metric.ExportKind.Should().Be(NinaMetricExportKind.DeferredPointInTime);
            metric.AttributeNames.Should().Contain(
                "profile_name",
                "host_name",
                "addon.id",
                "source",
                "source.file",
                "event.kind",
                "session.id");
            metric.AttributeNames.Should().NotContain(
                "message",
                "raw.line");
            NinaMetricCatalog.GetMetricAttributeNames(metricName, NinaMetricExportKind.DeferredPointInTime)
                .Should()
                .NotBeNull()
                .And.Contain(
                    "addon.id",
                    "source",
                    "source.file",
                    "event.kind",
                    "session.id");
            NinaMetricCatalog.IsLiveObservableGauge(metricName).Should().BeFalse();
        }
    }

    [Fact]
    public void SwitchReadOnlyGaugeName_UsesInfluxExporterSwitchMetricPattern()
    {
        NinaMetricCatalog.SwitchReadOnlyGaugeName(23).Should().Be("switch_ro_sw23");
    }

    [Fact]
    public void GetLiveObservableGaugeAttributeNames_ForDynamicSwitchMetrics_IncludesSwitchChannelName()
    {
        var attributeNames = NinaMetricCatalog.GetLiveObservableGaugeAttributeNames("switch_ro_sw23");

        attributeNames.Should().NotBeNull();
        attributeNames.Should().Contain(
            "profile_name",
            "host_name",
            "switch_name",
            "switch_id",
            "switch_channel_name");
    }
}
