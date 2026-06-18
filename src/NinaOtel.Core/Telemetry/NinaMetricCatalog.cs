using System.Collections.ObjectModel;

namespace NinaOtel.Core.Telemetry;

public enum NinaMetricExportKind
{
    LiveObservableGauge,
    DeferredPointInTime,
}

public sealed record NinaMetricDefinition(
    string Name,
    string Category,
    string Description,
    string ValueKind,
    IReadOnlyList<string> AttributeNames,
    NinaMetricExportKind ExportKind = NinaMetricExportKind.LiveObservableGauge);

public static class NinaMetricCatalog
{
    private static readonly IReadOnlyList<string> GlobalAttributes =
        Array.AsReadOnly(["profile_name", "host_name"]);

    private static readonly IReadOnlyList<string> ImageAttributes =
        Array.AsReadOnly(
        [
            "image_file_name",
            "target_name",
            "sequence_title",
            "camera_name",
            "readout_mode",
        ]);

    public static IReadOnlyList<NinaMetricDefinition> All { get; } =
        Array.AsReadOnly(
        [
            Metric("astro_moon_altitude", "astrometric", "Moon altitude in degrees.", "double"),
            Metric("astro_sun_altitude", "astrometric", "Sun altitude in degrees.", "double"),

            Metric("camera_sensor_temperature", "camera", "Camera sensor temperature in degrees Celsius.", "double", "camera_name"),
            Metric("camera_cooler_power", "camera", "Camera cooler power in percent.", "double", "camera_name"),
            Metric("camera_battery_level", "camera", "Camera battery charge level.", "integer", "camera_name"),
            Metric("qhy_sensor_air_pressure", "camera", "QHY sensor chamber air pressure.", "double", "camera_name"),
            Metric("qhy_sensor_humidity", "camera", "QHY sensor chamber humidity.", "double", "camera_name"),

            Metric("focuser_temperature", "focuser", "Focuser temperature in degrees Celsius.", "double", "focuser_name"),
            Metric("focuser_position", "focuser", "Focuser position in steps.", "integer", "focuser_name"),

            Metric("guider_rms_ra_arcsec", "guider", "RA RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_dec_arcsec", "guider", "Declination RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_arcsec", "guider", "Combined RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_ra_pixel", "guider", "RA RMS in pixels.", "double", "guider_name"),
            Metric("guider_rms_dec_pixel", "guider", "Declination RMS in pixels.", "double", "guider_name"),
            Metric("guider_rms_pixel", "guider", "Combined RMS in pixels.", "double", "guider_name"),
            Metric("guider_rms_peak_ra_arcsec", "guider", "Peak RA RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_peak_dec_arcsec", "guider", "Peak declination RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_peak_arcsec", "guider", "Combined peak RMS in arcseconds.", "double", "guider_name"),
            Metric("guider_rms_peak_ra_pixel", "guider", "Peak RA RMS in pixels.", "double", "guider_name"),
            Metric("guider_rms_peak_dec_pixel", "guider", "Peak declination RMS in pixels.", "double", "guider_name"),
            Metric("guider_rms_peak_pixel", "guider", "Combined peak RMS in pixels.", "double", "guider_name"),
            Metric("guider_ra_distance", "guider", "Guide step RA distance.", "double", "guider_name"),
            Metric("guider_ra_duration", "guider", "Guide step RA duration.", "double", "guider_name"),
            Metric("guider_dec_distance", "guider", "Guide step declination distance.", "double", "guider_name"),
            Metric("guider_dec_duration", "guider", "Guide step declination duration.", "double", "guider_name"),

            Metric("mount_altitude", "mount", "Mount altitude in degrees.", "double", "mount_name"),
            Metric("mount_azimuth", "mount", "Mount azimuth in degrees.", "double", "mount_name"),

            Metric("rotator_mechanical_angle", "rotator", "Rotator mechanical angle in degrees.", "double", "rotator_name"),
            Metric("rotator_angle", "rotator", "Rotator sky angle in degrees.", "double", "rotator_name"),

            Metric("safety_issafe", "safety", "Safety monitor safe state as 1 for safe and 0 for unsafe.", "double", "safety_monitor_name"),

            Metric("fwheel_filter", "filter_wheel", "Current filter wheel position.", "integer", "filter_wheel_name", "filter_name"),

            Metric("wx_cloud_cover", "weather", "Cloud cover in percent.", "double", "wx_device_name"),
            Metric("wx_dewpoint", "weather", "Dewpoint in degrees Celsius.", "double", "wx_device_name"),
            Metric("wx_humidity", "weather", "Relative humidity in percent.", "double", "wx_device_name"),
            Metric("wx_pressure", "weather", "Air pressure in hectopascals.", "double", "wx_device_name"),
            Metric("wx_rain_rate", "weather", "Rain rate in millimeters per hour.", "double", "wx_device_name"),
            Metric("wx_sky_brightness", "weather", "Sky brightness in lux.", "double", "wx_device_name"),
            Metric("wx_sky_quality", "weather", "Sky quality in magnitudes per square arcsecond.", "double", "wx_device_name"),
            Metric("wx_sky_temperature", "weather", "Sky temperature in degrees Celsius.", "double", "wx_device_name"),
            Metric("wx_star_fwhm", "weather", "Measured star FWHM.", "double", "wx_device_name"),
            Metric("wx_temperature", "weather", "Ambient air temperature in degrees Celsius.", "double", "wx_device_name"),
            Metric("wx_wind_direction", "weather", "Wind direction in azimuthal degrees.", "double", "wx_device_name"),
            Metric("wx_wind_gust", "weather", "Wind gust speed in meters per second.", "double", "wx_device_name"),
            Metric("wx_wind_speed", "weather", "Wind speed in meters per second.", "double", "wx_device_name"),

            ImageMetric("image_eccentricity", "Average star eccentricity.", "double"),
            ImageMetric("image_fwhm", "Average star full width at half maximum.", "double"),
            ImageMetric("image_hfr", "Average star half flux radius.", "double"),
            ImageMetric("image_hfr_std_deviation", "Standard deviation of measured star HFR.", "double"),
            ImageMetric("image_mad", "Pixel value mean absolute deviation.", "double"),
            ImageMetric("image_max_adu", "Maximum pixel ADU value.", "integer"),
            ImageMetric("image_max_adu_count", "Count of pixels at maximum ADU.", "integer"),
            ImageMetric("image_mean", "Pixel mean value.", "double"),
            ImageMetric("image_median", "Pixel median value.", "double"),
            ImageMetric("image_min_adu", "Minimum pixel ADU value.", "integer"),
            ImageMetric("image_min_adu_count", "Count of pixels at minimum ADU.", "integer"),
            ImageMetric("image_rms_avg_ra_arcsec", "Average RA guiding RMS during exposure.", "double"),
            ImageMetric("image_rms_avg_dec_arcsec", "Average declination guiding RMS during exposure.", "double"),
            ImageMetric("image_rms_avg_arcsec", "Combined average guiding RMS during exposure.", "double"),
            ImageMetric("image_rms_peak_ra_arcsec", "Peak RA guiding RMS during exposure.", "double"),
            ImageMetric("image_rms_peak_dec_arcsec", "Peak declination guiding RMS during exposure.", "double"),
            ImageMetric("image_rms_peak_arcsec", "Combined peak guiding RMS during exposure.", "double"),
            ImageMetric("image_star_count", "Detected star count.", "integer"),
            ImageMetric("image_std_deviation", "Pixel value standard deviation.", "double"),
        ]);

    private static readonly IReadOnlyDictionary<string, NinaMetricDefinition> DefinitionsByName =
        All.ToDictionary(static metric => metric.Name, StringComparer.Ordinal);

    public static string SwitchReadOnlyGaugeName(int switchId) => $"switch_ro_sw{switchId}";

    public static bool IsLiveObservableGauge(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return false;
        }

        return DefinitionsByName.TryGetValue(metricName, out var metric)
            ? metric.ExportKind == NinaMetricExportKind.LiveObservableGauge
            : IsSwitchReadOnlyGaugeName(metricName);
    }

    internal static IReadOnlySet<string>? GetLiveObservableGaugeAttributeNames(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return null;
        }

        if (DefinitionsByName.TryGetValue(metricName, out var metric))
        {
            return metric.ExportKind == NinaMetricExportKind.LiveObservableGauge
                ? new HashSet<string>(metric.AttributeNames, StringComparer.Ordinal)
                : null;
        }

        return IsSwitchReadOnlyGaugeName(metricName)
            ? new HashSet<string>(
                GlobalAttributes.Concat(["switch_name", "switch_id", "switch_channel_name"]),
                StringComparer.Ordinal)
            : null;
    }

    private static NinaMetricDefinition Metric(
        string name,
        string category,
        string description,
        string valueKind,
        params string[] attributes) =>
        Metric(name, category, description, valueKind, (IReadOnlyList<string>)attributes);

    private static NinaMetricDefinition Metric(
        string name,
        string category,
        string description,
        string valueKind,
        IReadOnlyList<string> attributes,
        NinaMetricExportKind exportKind = NinaMetricExportKind.LiveObservableGauge)
    {
        var allAttributes = GlobalAttributes.Concat(attributes).Distinct(StringComparer.Ordinal).ToArray();
        return new NinaMetricDefinition(
            name,
            category,
            description,
            valueKind,
            new ReadOnlyCollection<string>(allAttributes),
            exportKind);
    }

    private static NinaMetricDefinition ImageMetric(
        string name,
        string description,
        string valueKind) =>
        Metric(
            name,
            "image",
            description,
            valueKind,
            ImageAttributes,
            NinaMetricExportKind.DeferredPointInTime);

    private static bool IsSwitchReadOnlyGaugeName(string metricName)
    {
        const string Prefix = "switch_ro_sw";

        if (!metricName.StartsWith(Prefix, StringComparison.Ordinal) || metricName.Length == Prefix.Length)
        {
            return false;
        }

        return metricName.AsSpan(Prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
