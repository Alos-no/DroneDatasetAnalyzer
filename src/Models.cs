using System.Text.RegularExpressions;

namespace DroneDatasetAnalyzer;

/// <summary>
/// Lightweight record parsed from a DJI filename (zero I/O).
/// Format: DJI_YYYYMMDDHHMMSS_NNNN_V.jpg
/// </summary>
/// <param name="FileName">Original filename (no directory).</param>
/// <param name="FilePath">Full path to the file on disk.</param>
/// <param name="LocalTimestamp">Capture time parsed from the filename (camera-local timezone).</param>
/// <param name="SequenceNumber">DJI per-power-cycle sequence counter (resets to 0001 on reboot).</param>
public readonly record struct DjiFileInfo(
  string FileName,
  string FilePath,
  DateTime LocalTimestamp,
  int SequenceNumber);

/// <summary>
/// Full metadata extracted from a single photo's EXIF and DJI XMP data.
/// Fields are nullable because XMP/EXIF data is only read for sampled photos.
/// </summary>
public sealed class PhotoMetadata
{
  /// <summary>Original filename (no directory).</summary>
  public required string FileName { get; init; }

  /// <summary>Full path to the file on disk.</summary>
  public required string FilePath { get; init; }

  /// <summary>Capture time from filename (camera-local timezone).</summary>
  public required DateTime LocalTimestamp { get; init; }

  /// <summary>DJI sequence counter from filename.</summary>
  public required int SequenceNumber { get; init; }


  #region XMP: drone-dji namespace fields

  /// <summary>Altitude relative to takeoff point (meters). Constant within a flight.</summary>
  public double? RelativeAltitude { get; set; }

  /// <summary>Altitude above mean sea level in EGM96 datum (meters).</summary>
  public double? AbsoluteAltitude { get; set; }

  /// <summary>GPS latitude (decimal degrees, WGS84).</summary>
  public double? Latitude { get; set; }

  /// <summary>GPS longitude (decimal degrees, WGS84).</summary>
  public double? Longitude { get; set; }

  /// <summary>Camera gimbal pitch in degrees. -90 = nadir, -45 = typical oblique, 0 = horizon.</summary>
  public double? GimbalPitch { get; set; }

  /// <summary>Camera gimbal yaw in degrees (0-360, magnetic north reference).</summary>
  public double? GimbalYaw { get; set; }

  /// <summary>Camera gimbal roll in degrees. 0 = upright, ±180 = inverted (backward look).</summary>
  public double? GimbalRoll { get; set; }

  /// <summary>Drone body yaw / heading in degrees (0-360).</summary>
  public double? FlightYaw { get; set; }

  /// <summary>Drone ground speed along east-west axis (m/s, positive = east).</summary>
  public double? FlightXSpeed { get; set; }

  /// <summary>Drone ground speed along north-south axis (m/s, positive = north).</summary>
  public double? FlightYSpeed { get; set; }

  /// <summary>Camera sensor temperature in Celsius.</summary>
  public double? SensorTemperature { get; set; }

  /// <summary>White balance correlated color temperature in Kelvin.</summary>
  public int? WhiteBalanceCct { get; set; }

  /// <summary>RTK fix status: 0=none, 16=float, 34=fixed, 50=fixed w/ base.</summary>
  public int? RtkFlag { get; set; }

  /// <summary>Laser rangefinder target distance in meters (0 if not used).</summary>
  public double? LrfTargetDistance { get; set; }

  /// <summary>Calibrated focal length in pixels (includes lens distortion correction).</summary>
  public double? CalibratedFocalLength { get; set; }

  /// <summary>Shutter type: "Electronic" or "Mechanical".</summary>
  public string? ShutterType { get; set; }

  /// <summary>DJI product model name (e.g., "M4E").</summary>
  public string? ProductName { get; set; }

  /// <summary>Drone serial number.</summary>
  public string? DroneSerialNumber { get; set; }

  /// <summary>Camera serial number.</summary>
  public string? CameraSerialNumber { get; set; }

  /// <summary>Image source identifier (e.g., "DJI_DNG").</summary>
  public string? ImageSource { get; set; }

  /// <summary>UTC time at the moment of exposure.</summary>
  public DateTime? UtcAtExposure { get; set; }

  /// <summary>Cumulative shutter actuation count.</summary>
  public int? ShutterCount { get; set; }

  #endregion


  #region EXIF camera settings

  /// <summary>Exposure time in seconds (e.g., 0.0005 = 1/2000s).</summary>
  public double? ExposureTime { get; set; }

  /// <summary>Lens aperture f-number (e.g., 2.8).</summary>
  public double? FNumber { get; set; }

  /// <summary>ISO sensitivity.</summary>
  public int? Iso { get; set; }

  /// <summary>Actual focal length in millimeters.</summary>
  public double? FocalLengthMm { get; set; }

  /// <summary>35mm equivalent focal length.</summary>
  public int? FocalLength35mm { get; set; }

  /// <summary>Image width in pixels.</summary>
  public int? ImageWidth { get; set; }

  /// <summary>Image height in pixels.</summary>
  public int? ImageHeight { get; set; }

  #endregion


  #region Computed properties

  /// <summary>Ground speed magnitude in m/s (from FlightXSpeed and FlightYSpeed).</summary>
  public double? GroundSpeed =>
    FlightXSpeed is not null && FlightYSpeed is not null
      ? Math.Sqrt(FlightXSpeed.Value * FlightXSpeed.Value + FlightYSpeed.Value * FlightYSpeed.Value)
      : null;

  #endregion
}

/// <summary>
/// Aggregated data for one continuous flight (between power cycles).
/// </summary>
public sealed class FlightInfo
{
  /// <summary>1-based flight index.</summary>
  public int Index { get; set; }

  /// <summary>All photos in this flight (lightweight filename-parsed data).</summary>
  public List<DjiFileInfo> Photos { get; } = [];

  /// <summary>Photos with full XMP+EXIF metadata (sampled subset).</summary>
  public List<PhotoMetadata> SampledPhotos { get; } = [];

  /// <summary>Short label of the capture group this flight was classified into (e.g., "110 m" or "Mixed").</summary>
  public string? CaptureGroupLabel { get; set; }

  /// <summary>Time gap before this flight (null for first flight).</summary>
  public TimeSpan? GapBefore { get; set; }

  /// <summary>Capture time of the first photo in this flight.</summary>
  public DateTime StartTime => Photos[0].LocalTimestamp;

  /// <summary>Capture time of the last photo in this flight.</summary>
  public DateTime EndTime => Photos[^1].LocalTimestamp;

  /// <summary>Total duration of this flight.</summary>
  public TimeSpan Duration => EndTime - StartTime;

  /// <summary>Number of photos in this flight.</summary>
  public int PhotoCount => Photos.Count;
}

/// <summary>
/// Results of the gimbal angle and smart oblique analysis.
/// </summary>
public sealed class GimbalAnalysis
{
  /// <summary>True configured oblique angle (degrees, e.g., -45).</summary>
  public double ObliqueAngle { get; set; }

  /// <summary>Number of nadir shots (pitch &lt;= -80°) in sample.</summary>
  public int NadirCount { get; set; }

  /// <summary>Number of forward-looking oblique shots (pitch ~ -45°, roll ~ 0°).</summary>
  public int ObliqueForwardCount { get; set; }

  /// <summary>Number of backward-looking oblique shots (pitch ~ -55°, roll ~ ±180°).</summary>
  public int ObliqueBackwardCount { get; set; }

  /// <summary>Number of near-horizon shots (pitch &gt; -30°).</summary>
  public int HorizonCount { get; set; }

  /// <summary>Total number of photos analyzed.</summary>
  public int TotalSampled { get; set; }

  /// <summary>Mean forward-look oblique pitch in degrees.</summary>
  public double MeanForwardPitch { get; set; }

  /// <summary>Mean backward-look oblique pitch in degrees (typically -(90 - |ObliqueAngle|)).</summary>
  public double MeanBackwardPitch { get; set; }

  /// <summary>Mean gimbal pitch across all sampled photos (degrees, negative = downward).</summary>
  public double MeanPitch { get; set; }

  /// <summary>True if the gimbal was operating in smart oblique mode (forward + backward oblique shots detected).</summary>
  public bool HasSmartOblique => ObliqueForwardCount > 0 && ObliqueBackwardCount > 0;

  /// <summary>Human-readable explanation of the oblique correlation.</summary>
  public string Explanation { get; set; } = "";
}

/// <summary>
/// Forward and side overlap percentages with supporting geometry.
/// </summary>
public sealed class OverlapAnalysis
{
  /// <summary>Forward (along-track) overlap percentage (0-1).</summary>
  public double ForwardOverlap { get; set; }

  /// <summary>Side (cross-track) overlap percentage (0-1).</summary>
  public double SideOverlap { get; set; }

  /// <summary>Forward spacing between consecutive nadir shots (meters).</summary>
  public double ForwardSpacingMeters { get; set; }

  /// <summary>Side spacing between adjacent flight lines (meters).</summary>
  public double SideSpacingMeters { get; set; }

  /// <summary>Ground footprint width at primary altitude (meters).</summary>
  public double FootprintWidthMeters { get; set; }

  /// <summary>Ground footprint height at primary altitude (meters).</summary>
  public double FootprintHeightMeters { get; set; }

  /// <summary>Ground sample distance (meters/pixel) at primary altitude.</summary>
  public double GsdMeters { get; set; }

  /// <summary>Primary flight altitude above ground (meters).</summary>
  public double PrimaryAltitude { get; set; }

  /// <summary>Average ground speed of nadir shots (m/s).</summary>
  public double MeanGroundSpeed { get; set; }

  /// <summary>Number of nadir photo pairs used for forward overlap computation.</summary>
  public int ForwardSampleCount { get; set; }

  /// <summary>Number of line-change pairs used for side overlap computation.</summary>
  public int SideSampleCount { get; set; }
}

/// <summary>
/// Elevation and AGL analysis from terrain API data.
/// </summary>
public sealed class ElevationAnalysis
{
  /// <summary>Number of photos with valid AGL measurements.</summary>
  public int SampleCount { get; set; }

  /// <summary>Minimum above-ground-level altitude (meters).</summary>
  public double MinAgl { get; set; }

  /// <summary>Maximum above-ground-level altitude (meters).</summary>
  public double MaxAgl { get; set; }

  /// <summary>Mean AGL (meters).</summary>
  public double MeanAgl { get; set; }

  /// <summary>Median AGL (meters).</summary>
  public double MedianAgl { get; set; }

  /// <summary>Standard deviation of AGL (meters).</summary>
  public double StdDevAgl { get; set; }

  /// <summary>Mean ground elevation above MSL (meters, EGM96 datum).</summary>
  public double MeanGroundElevation { get; set; }

  /// <summary>Mean drone altitude above MSL (meters, EGM96).</summary>
  public double MeanDroneAltitudeMsl { get; set; }
}

/// <summary>
/// Equipment identification from the first sampled photo.
/// </summary>
public sealed class EquipmentInfo
{
  /// <summary>DJI product name (e.g., "M4E" for Mavic 4 Enterprise).</summary>
  public string ProductName { get; set; } = "Unknown";

  /// <summary>Drone serial number.</summary>
  public string DroneSerialNumber { get; set; } = "Unknown";

  /// <summary>Camera serial number.</summary>
  public string CameraSerialNumber { get; set; } = "Unknown";

  /// <summary>Actual focal length in mm.</summary>
  public double FocalLengthMm { get; set; }

  /// <summary>35mm equivalent focal length.</summary>
  public int FocalLength35mm { get; set; }

  /// <summary>Calibrated focal length in pixels.</summary>
  public double CalibratedFocalLengthPx { get; set; }

  /// <summary>Image width in pixels.</summary>
  public int ImageWidth { get; set; }

  /// <summary>Image height in pixels.</summary>
  public int ImageHeight { get; set; }

  /// <summary>Sensor physical width in mm (derived from actual/equiv focal length ratio).</summary>
  public double SensorWidthMm { get; set; }

  /// <summary>Sensor physical height in mm (derived from aspect ratio).</summary>
  public double SensorHeightMm { get; set; }

  /// <summary>Total megapixels.</summary>
  public double Megapixels { get; set; }

  /// <summary>Shutter type string.</summary>
  public string ShutterType { get; set; } = "Unknown";
}

/// <summary>
/// Geographic location summary of the mission.
/// </summary>
public sealed class LocationInfo
{
  /// <summary>Center latitude of the coverage area.</summary>
  public double CenterLatitude { get; set; }

  /// <summary>Center longitude of the coverage area.</summary>
  public double CenterLongitude { get; set; }

  /// <summary>Bounding box north edge.</summary>
  public double BboxNorth { get; set; }

  /// <summary>Bounding box south edge.</summary>
  public double BboxSouth { get; set; }

  /// <summary>Bounding box east edge.</summary>
  public double BboxEast { get; set; }

  /// <summary>Bounding box west edge.</summary>
  public double BboxWest { get; set; }

  /// <summary>Approximate coverage area in hectares.</summary>
  public double CoverageAreaHectares { get; set; }

  /// <summary>UTC offset detected from filename vs XMP UTC comparison.</summary>
  public TimeSpan? UtcOffset { get; set; }
}

/// <summary>
/// The complete mission analysis result.
/// </summary>
public sealed class MissionReport
{
  /// <summary>Equipment identification.</summary>
  public EquipmentInfo Equipment { get; set; } = new();

  /// <summary>Geographic location summary.</summary>
  public LocationInfo Location { get; set; } = new();

  /// <summary>Per-flight breakdown.</summary>
  public List<FlightInfo> Flights { get; } = [];

  /// <summary>Capture groups classified by altitude band, each with independent analysis.</summary>
  public List<CaptureGroup> CaptureGroups { get; } = [];

  /// <summary>Elevation and AGL analysis (null if API unavailable).</summary>
  public ElevationAnalysis? Elevation { get; set; }

  /// <summary>Total photo count across all flights.</summary>
  public int TotalPhotos { get; set; }

  /// <summary>Number of distinct capture days.</summary>
  public int CaptureDays { get; set; }

  /// <summary>Distinct capture dates in chronological order.</summary>
  public List<DateOnly> Dates { get; } = [];

  /// <summary>Total capture time across all flights.</summary>
  public TimeSpan TotalCaptureTime { get; set; }
}

/// <summary>
/// A group of flights classified by altitude band for independent analysis.
/// Classified groups share a consistent flight altitude; the unclassified group
/// contains flights with varying altitude or too few photos to form a distinct band.
/// </summary>
public sealed class CaptureGroup
{
  /// <summary>Display label (e.g., "Capture at 110 m" or "Varying altitude").</summary>
  public required string Label { get; init; }

  /// <summary>Band altitude in meters, rounded to nearest 5m (null for the unclassified group).</summary>
  public double? BandAltitude { get; init; }

  /// <summary>True if this is the unclassified catch-all group (varying altitude / below threshold).</summary>
  public bool IsUnclassified { get; init; }

  /// <summary>Flights in this capture group.</summary>
  public List<FlightInfo> Flights { get; } = [];

  /// <summary>Total photo count across all flights in this group.</summary>
  public int TotalPhotos => Flights.Sum(f => f.PhotoCount);

  /// <summary>Overlap and coverage analysis (null for unclassified groups or insufficient data).</summary>
  public OverlapAnalysis? Overlap { get; set; }

  /// <summary>Gimbal and smart oblique analysis.</summary>
  public GimbalAnalysis Gimbal { get; set; } = new();

  /// <summary>Minimum relative altitude across sampled photos.</summary>
  public double? MinAltitude { get; set; }

  /// <summary>Maximum relative altitude across sampled photos.</summary>
  public double? MaxAltitude { get; set; }
}

/// <summary>
/// Configuration options for the analysis pipeline.
/// </summary>
public sealed class AnalysisOptions
{
  /// <summary>Number of photos to sample per flight for metadata reading (default 9).</summary>
  public int SamplesPerFlight { get; set; } = 9;

  /// <summary>Number of consecutive photos to read for overlap analysis (default 500).</summary>
  public int OverlapBlockSize { get; set; } = 500;

  /// <summary>Output file path for the markdown report (null = stdout).</summary>
  public string? OutputPath { get; set; }

  /// <summary>Whether to skip the elevation API query.</summary>
  public bool SkipElevation { get; set; }
}

/// <summary>
/// Static utility for parsing DJI filename conventions.
/// </summary>
public static partial class DjiFilenameParser
{
  /// <summary>
  /// Regex matching DJI photo filenames: DJI_YYYYMMDDHHMMSS_NNNN_X.jpg
  /// Group 1: full timestamp (14 digits), Group 2: sequence number.
  /// </summary>
  [GeneratedRegex(@"^DJI_(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})_(\d+)_\w+\.\w+$", RegexOptions.IgnoreCase)]
  private static partial Regex DjiFilenamePattern();

  /// <summary>
  /// Attempts to parse a DJI filename into its timestamp and sequence number.
  /// Returns null if the filename does not match the DJI convention.
  /// </summary>
  public static DjiFileInfo? TryParse(string filePath)
  {
    var fileName = Path.GetFileName(filePath);
    var match = DjiFilenamePattern().Match(fileName);

    if (!match.Success)
      return null;

    // Parse timestamp components from the capture groups
    int year = int.Parse(match.Groups[1].ValueSpan);
    int month = int.Parse(match.Groups[2].ValueSpan);
    int day = int.Parse(match.Groups[3].ValueSpan);
    int hour = int.Parse(match.Groups[4].ValueSpan);
    int minute = int.Parse(match.Groups[5].ValueSpan);
    int second = int.Parse(match.Groups[6].ValueSpan);
    int seq = int.Parse(match.Groups[7].ValueSpan);

    var timestamp = new DateTime(year, month, day, hour, minute, second);

    return new DjiFileInfo(fileName, filePath, timestamp, seq);
  }
}

/// <summary>
/// Math helpers used across analysis modules.
/// </summary>
public static class MathHelpers
{
  /// <summary>Earth radius in meters for haversine calculations.</summary>
  private const double EarthRadiusMeters = 6_371_000;

  /// <summary>
  /// Haversine distance between two GPS coordinates in meters.
  /// </summary>
  public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
  {
    double dLat = DegreesToRadians(lat2 - lat1);
    double dLon = DegreesToRadians(lon2 - lon1);
    double lat1Rad = DegreesToRadians(lat1);
    double lat2Rad = DegreesToRadians(lat2);

    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
               Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
               Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

    return EarthRadiusMeters * c;
  }

  /// <summary>
  /// Computes the median of a list of doubles.
  /// </summary>
  public static double Median(List<double> values)
  {
    if (values.Count == 0)
      return double.NaN;

    var sorted = values.OrderBy(v => v).ToList();
    int mid = sorted.Count / 2;

    return sorted.Count % 2 == 0
      ? (sorted[mid - 1] + sorted[mid]) / 2.0
      : sorted[mid];
  }

  /// <summary>
  /// Computes the population standard deviation of a list of doubles.
  /// </summary>
  public static double StdDev(List<double> values)
  {
    if (values.Count < 2)
      return 0;

    double mean = values.Average();
    double sumSq = values.Sum(v => (v - mean) * (v - mean));

    return Math.Sqrt(sumSq / values.Count);
  }

  /// <summary>
  /// Normalizes an angle to the range [-180, 180].
  /// </summary>
  public static double NormalizeAngle(double degrees)
  {
    degrees %= 360;

    if (degrees > 180) degrees -= 360;
    if (degrees < -180) degrees += 360;

    return degrees;
  }

  /// <summary>
  /// Converts degrees to radians.
  /// </summary>
  public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
