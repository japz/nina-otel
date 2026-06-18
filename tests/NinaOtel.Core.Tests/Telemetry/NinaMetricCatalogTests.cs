using FluentAssertions;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Telemetry;

public sealed class NinaMetricCatalogTests
{
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
        NinaMetricCatalog.IsLiveObservableGauge("image_mean").Should().BeFalse();
    }

    [Fact]
    public void SwitchReadOnlyGaugeName_UsesInfluxExporterSwitchMetricPattern()
    {
        NinaMetricCatalog.SwitchReadOnlyGaugeName(23).Should().Be("switch_ro_sw23");
    }
}
