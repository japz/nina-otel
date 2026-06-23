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
            "image_type",
            "filter_name",
            "exposure_duration_seconds",
        ]);

    private static readonly HashSet<string> CoreEquipmentCategories =
        new(StringComparer.Ordinal)
        {
            "astrometric",
            "camera",
            "dome",
            "filter_wheel",
            "flat_device",
            "focuser",
            "guider",
            "mount",
            "rotator",
            "safety",
            "switch",
            "weather",
        };

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

            Phd2Metric("phd2_guide_rms_ra_pixel", "PHD2 GuideLog session RA RMS in pixels.", "double"),
            Phd2Metric("phd2_guide_rms_dec_pixel", "PHD2 GuideLog session declination RMS in pixels.", "double"),
            Phd2Metric("phd2_guide_rms_pixel", "PHD2 GuideLog session combined RMS in pixels.", "double"),
            Phd2Metric("phd2_guide_sample_count", "PHD2 GuideLog guide sample count.", "integer"),
            Phd2Metric(
                "phd2_guide_ra_pulse_distance_pixel",
                "PHD2 GuideLog RA guide pulse correction distance in pixels.",
                "double",
                "phd2.ra_direction",
                "phd2.dec_direction"),
            Phd2Metric(
                "phd2_guide_ra_pulse_duration_ms",
                "PHD2 GuideLog RA guide pulse duration in milliseconds.",
                "double",
                "phd2.ra_direction",
                "phd2.dec_direction"),
            Phd2Metric(
                "phd2_guide_dec_pulse_distance_pixel",
                "PHD2 GuideLog declination guide pulse correction distance in pixels.",
                "double",
                "phd2.ra_direction",
                "phd2.dec_direction"),
            Phd2Metric(
                "phd2_guide_dec_pulse_duration_ms",
                "PHD2 GuideLog declination guide pulse duration in milliseconds.",
                "double",
                "phd2.ra_direction",
                "phd2.dec_direction"),

            TargetSchedulerMetric("target_scheduler_planning_run_count", "Target Scheduler planning runs started.", "integer"),
            TargetSchedulerMetric("target_scheduler_planning_run_completed_count", "Target Scheduler planning runs completed.", "integer"),
            TargetSchedulerMetric("target_scheduler_target_selected_count", "Target Scheduler target selections.", "integer"),
            TargetSchedulerMetric("target_scheduler_current_target", "Current Target Scheduler target state.", "integer"),
            TargetSchedulerMetric("target_scheduler_plan_started_count", "Target Scheduler plans started.", "integer"),
            TargetSchedulerMetric("target_scheduler_plan_stopped_count", "Target Scheduler plans stopped.", "integer"),
            TargetSchedulerMetric("target_scheduler_image_graded_count", "Target Scheduler image grading events.", "integer"),
            TargetSchedulerMetric("target_scheduler_image_grade_score", "Target Scheduler parsed image grade score.", "double"),

            NightSummaryMetric("night_summary_session_started_count", "Night Summary session start breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_session_ended_count", "Night Summary session end breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_report_started_count", "Night Summary report generation start breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_report_delivered_count", "Night Summary report delivery completion breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_report_failed_count", "Night Summary report failure breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_autofocus_completed_count", "Night Summary autofocus completion breadcrumbs.", "integer"),
            NightSummaryMetric("night_summary_meridian_flip_count", "Night Summary meridian flip completion breadcrumbs.", "integer"),

            Metric("mount_altitude", "mount", "Mount altitude in degrees.", "double", "mount_name"),
            Metric("mount_azimuth", "mount", "Mount azimuth in degrees.", "double", "mount_name"),

            Metric("onstepx_tracking_enabled", "onstepx", "OnStepX tracking state as 1 for tracking and 0 for not tracking.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_goto_active", "onstepx", "OnStepX go-to state as 1 for active and 0 for inactive.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_parked", "onstepx", "OnStepX parked state as 1 for parked and 0 for not parked.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_parking", "onstepx", "OnStepX parking state as 1 for parking and 0 otherwise.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_park_failed", "onstepx", "OnStepX park failure state as 1 for failed and 0 otherwise.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_home", "onstepx", "OnStepX home state as 1 for at home and 0 otherwise.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_homing", "onstepx", "OnStepX homing state as 1 for homing and 0 otherwise.", "double", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_error_code", "onstepx", "OnStepX status error code reported by controller status.", "integer", "mount_name", "onstepx.host", "onstepx.port", "mount.type", "pier.side"),
            Metric("onstepx_tracking_rate_hz", "onstepx", "OnStepX tracking rate in hertz.", "double", "mount_name", "onstepx.host", "onstepx.port"),

            Metric("rotator_mechanical_angle", "rotator", "Rotator mechanical angle in degrees.", "double", "rotator_name"),
            Metric("rotator_angle", "rotator", "Rotator sky angle in degrees.", "double", "rotator_name"),

            Metric("safety_issafe", "safety", "Safety monitor safe state as 1 for safe and 0 for unsafe.", "double", "safety_monitor_name"),

            Metric("fwheel_filter", "filter_wheel", "Current filter wheel position.", "integer", "filter_wheel_name", "filter_name"),

            WeatherMetric("wx_cloud_cover", "Cloud cover in percent.", "double"),
            WeatherMetric("wx_dewpoint", "Dewpoint in degrees Celsius.", "double"),
            WeatherMetric("wx_humidity", "Relative humidity in percent.", "double"),
            WeatherMetric("wx_pressure", "Air pressure in hectopascals.", "double"),
            WeatherMetric("wx_rain_rate", "Rain rate in millimeters per hour.", "double"),
            WeatherMetric("wx_sky_brightness", "Sky brightness in lux.", "double"),
            WeatherMetric("wx_sky_quality", "Sky quality in magnitudes per square arcsecond.", "double"),
            WeatherMetric("wx_sky_temperature", "Sky temperature in degrees Celsius.", "double"),
            WeatherMetric("wx_star_fwhm", "Measured star FWHM.", "double"),
            WeatherMetric("wx_temperature", "Ambient air temperature in degrees Celsius.", "double"),
            WeatherMetric("wx_wind_direction", "Wind direction in azimuthal degrees.", "double"),
            WeatherMetric("wx_wind_gust", "Wind gust speed in meters per second.", "double"),
            WeatherMetric("wx_wind_speed", "Wind speed in meters per second.", "double"),

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

    public static bool IsCoreEquipmentMetric(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return false;
        }

        if (IsSwitchReadOnlyGaugeName(metricName))
        {
            return true;
        }

        return DefinitionsByName.TryGetValue(metricName, out var metric) &&
            metric.ExportKind == NinaMetricExportKind.LiveObservableGauge &&
            CoreEquipmentCategories.Contains(metric.Category);
    }

    public static bool IsImageMetric(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return false;
        }

        return DefinitionsByName.TryGetValue(metricName, out var metric) &&
            string.Equals(metric.Category, "image", StringComparison.Ordinal);
    }

    internal static bool TryGetExportKind(string metricName, out NinaMetricExportKind exportKind)
    {
        if (!string.IsNullOrWhiteSpace(metricName) &&
            DefinitionsByName.TryGetValue(metricName, out var metric))
        {
            exportKind = metric.ExportKind;
            return true;
        }

        if (IsSwitchReadOnlyGaugeName(metricName))
        {
            exportKind = NinaMetricExportKind.LiveObservableGauge;
            return true;
        }

        exportKind = default;
        return false;
    }

    internal static IReadOnlySet<string>? GetLiveObservableGaugeAttributeNames(string metricName)
        => GetMetricAttributeNames(metricName, NinaMetricExportKind.LiveObservableGauge);

    internal static IReadOnlySet<string>? GetMetricAttributeNames(string metricName, NinaMetricExportKind exportKind)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return null;
        }

        if (DefinitionsByName.TryGetValue(metricName, out var metric))
        {
            return metric.ExportKind == exportKind
                ? new HashSet<string>(metric.AttributeNames, StringComparer.Ordinal)
                : null;
        }

        return exportKind == NinaMetricExportKind.LiveObservableGauge &&
            IsSwitchReadOnlyGaugeName(metricName)
            ? new HashSet<string>(
                GlobalAttributes.Concat(["switch_name", "switch_id", "name", "switch_channel_name"]),
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

    private static NinaMetricDefinition WeatherMetric(
        string name,
        string description,
        string valueKind) =>
        Metric(name, "weather", description, valueKind, "wx_driver_name", "wx_device_name");

    private static NinaMetricDefinition Phd2Metric(
        string name,
        string description,
        string valueKind,
        params string[] attributes) =>
        Metric(
            name,
            "phd2",
            description,
            valueKind,
            ["addon.id", "source", "guider_name", "source.file", "phd2.session_start", .. attributes],
            NinaMetricExportKind.DeferredPointInTime);

    private static NinaMetricDefinition TargetSchedulerMetric(
        string name,
        string description,
        string valueKind) =>
        Metric(
            name,
            "target_scheduler",
            description,
            valueKind,
            [
                "addon.id",
                "source",
                "source.file",
                "event.kind",
                "target.name",
                "filter.name",
                "grade.status",
                "stop.reason",
            ],
            NinaMetricExportKind.DeferredPointInTime);

    private static NinaMetricDefinition NightSummaryMetric(
        string name,
        string description,
        string valueKind) =>
        Metric(
            name,
            "night_summary",
            description,
            valueKind,
            [
                "addon.id",
                "source",
                "source.file",
                "event.kind",
                "session.id",
            ],
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
