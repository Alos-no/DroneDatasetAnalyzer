using System.Globalization;
using System.Text;

namespace DroneDatasetAnalyzer;

/// <summary>
/// Generates a comprehensive Markdown mission report from analysis results.
/// Output format produces sections for: equipment, location, mission overview
/// (with capture group summary), elevation/terrain (global), per-group detail
/// (altitude/GSD, overlap, gimbal), per-day flight tables, camera summary,
/// and methodology notes.
/// </summary>
public static class ReportWriter
{
  /// <summary>
  /// Writes the complete mission report in Markdown format to the specified writer.
  /// </summary>
  public static void WriteReport(MissionReport report, TextWriter writer)
  {
    // Report title
    writer.WriteLine("# DJI Mission Report");
    writer.WriteLine();
    writer.WriteLine($"*Generated {DateTime.Now:yyyy-MM-dd HH:mm} by DroneDatasetAnalyzer*");
    writer.WriteLine();
    writer.WriteLine("---");
    writer.WriteLine();

    int section = 1;

    WriteEquipmentSection(ref section, report, writer);
    WriteLocationSection(ref section, report, writer);
    WriteMissionOverviewSection(ref section, report, writer);
    WriteElevationSection(ref section, report, writer);

    // Per-group detail sections (one section per capture group)
    foreach (var group in report.CaptureGroups)
      WriteCaptureGroupSection(ref section, group, report, writer);

    WriteFlightTablesSection(ref section, report, writer);
    WriteCameraSummarySection(ref section, report, writer);
    WriteMethodologySection(ref section, report, writer);
  }


  #region Section writers

  /// <summary>Equipment identification.</summary>
  private static void WriteEquipmentSection(ref int section, MissionReport report, TextWriter w)
  {
    var eq = report.Equipment;

    w.WriteLine($"## {section++}. Equipment");
    w.WriteLine();
    w.WriteLine("| Parameter | Value |");
    w.WriteLine("|-----------|-------|");
    // Avoid "DJI DJI" when ProductName already starts with "DJI"
    string platform = eq.ProductName.StartsWith("DJI", StringComparison.OrdinalIgnoreCase)
      ? eq.ProductName
      : $"DJI {eq.ProductName}";
    w.WriteLine($"| **Platform** | {platform} |");
    w.WriteLine($"| **Drone S/N** | {eq.DroneSerialNumber} |");
    w.WriteLine($"| **Camera S/N** | {eq.CameraSerialNumber} |");

    if (eq.SensorWidthMm > 0)
      w.WriteLine($"| **Sensor** | {eq.SensorWidthMm:F1} x {eq.SensorHeightMm:F1} mm ({eq.Megapixels:F1} MP) |");

    w.WriteLine($"| **Resolution** | {eq.ImageWidth} x {eq.ImageHeight} px |");
    w.WriteLine($"| **Focal Length** | {eq.FocalLengthMm:F1} mm actual / {eq.FocalLength35mm} mm equiv |");

    if (eq.CalibratedFocalLengthPx > 0)
      w.WriteLine($"| **Calibrated FL** | {eq.CalibratedFocalLengthPx:F1} px |");

    w.WriteLine($"| **Shutter** | {eq.ShutterType} |");
    w.WriteLine();
  }

  /// <summary>Geographic location summary.</summary>
  private static void WriteLocationSection(ref int section, MissionReport report, TextWriter w)
  {
    var loc = report.Location;

    w.WriteLine($"## {section++}. Location");
    w.WriteLine();
    w.WriteLine("| Parameter | Value |");
    w.WriteLine("|-----------|-------|");
    w.WriteLine($"| **Center** | {loc.CenterLatitude:F6}°N, {loc.CenterLongitude:F6}°E |");
    w.WriteLine($"| **Bounding Box** | N {loc.BboxNorth:F6}° / S {loc.BboxSouth:F6}° / E {loc.BboxEast:F6}° / W {loc.BboxWest:F6}° |");

    if (loc.CoverageAreaHectares > 0)
      w.WriteLine($"| **Coverage Area** | ~{loc.CoverageAreaHectares:F1} ha ({loc.CoverageAreaHectares / 100:F2} km²) |");

    if (loc.UtcOffset != null)
    {
      string sign = loc.UtcOffset.Value >= TimeSpan.Zero ? "+" : "";
      w.WriteLine($"| **Timezone** | UTC{sign}{loc.UtcOffset.Value.Hours}:{loc.UtcOffset.Value.Minutes:D2} (from filename vs XMP UTC) |");
    }

    w.WriteLine();
  }

  /// <summary>Mission overview with summary statistics and capture group table.</summary>
  private static void WriteMissionOverviewSection(ref int section, MissionReport report, TextWriter w)
  {
    w.WriteLine($"## {section++}. Mission Overview");
    w.WriteLine();
    w.WriteLine("| Parameter | Value |");
    w.WriteLine("|-----------|-------|");
    w.WriteLine($"| **Total Photos** | {report.TotalPhotos:N0} |");
    w.WriteLine($"| **Capture Days** | {report.CaptureDays} ({string.Join(", ", report.Dates.Select(d => d.ToString("yyyy-MM-dd")))}) |");
    w.WriteLine($"| **Flights** | {report.Flights.Count} |");
    w.WriteLine($"| **Capture Groups** | {report.CaptureGroups.Count} |");
    w.WriteLine($"| **Total Capture Time** | {FormatTimeSpan(report.TotalCaptureTime)} |");

    // RTK status from sampled data
    var rtkFlags = report.Flights
      .SelectMany(f => f.SampledPhotos)
      .Where(p => p.RtkFlag != null)
      .Select(p => p.RtkFlag!.Value)
      .Distinct()
      .ToList();

    if (rtkFlags.Count > 0)
    {
      string rtkDesc = string.Join(", ", rtkFlags.Select(DescribeRtkFlag));
      w.WriteLine($"| **RTK Status** | {rtkDesc} |");
    }

    w.WriteLine();

    // Capture group summary table
    if (report.CaptureGroups.Count > 1)
    {
      w.WriteLine("### Capture Groups");
      w.WriteLine();
      w.WriteLine("| Group | Flights | Photos | Altitude | GSD | Fwd Overlap | Side Overlap |");
      w.WriteLine("|-------|---------|--------|----------|-----|-------------|--------------|");

      foreach (var group in report.CaptureGroups)
      {
        string altStr = group.BandAltitude != null
          ? $"{group.BandAltitude:F0} m"
          : group.MinAltitude != null && group.MaxAltitude != null
            ? $"{group.MinAltitude:F0}–{group.MaxAltitude:F0} m"
            : "—";

        string gsdStr = group.Overlap != null
          ? $"{group.Overlap.GsdMeters * 100:F2} cm/px"
          : "—";

        string fwdStr = group.Overlap != null
          ? $"{group.Overlap.ForwardOverlap * 100:F0}%"
          : "—";

        string sideStr = group.Overlap != null
          ? $"{group.Overlap.SideOverlap * 100:F0}%"
          : "—";

        w.WriteLine($"| {group.Label} | {group.Flights.Count} | {group.TotalPhotos:N0} | {altStr} | {gsdStr} | {fwdStr} | {sideStr} |");
      }

      w.WriteLine();
    }
  }

  /// <summary>Global elevation/terrain section (shared across all groups).</summary>
  private static void WriteElevationSection(ref int section, MissionReport report, TextWriter w)
  {
    if (report.Elevation == null)
      return;

    var el = report.Elevation;

    w.WriteLine($"## {section++}. Elevation & Terrain");
    w.WriteLine();
    w.WriteLine("| Parameter | Value |");
    w.WriteLine("|-----------|-------|");
    w.WriteLine($"| **AGL (above terrain)** | {el.MeanAgl:F1} m mean, {el.MedianAgl:F1} m median (range {el.MinAgl:F1}–{el.MaxAgl:F1} m) |");
    w.WriteLine($"| **Ground Elevation** | {el.MeanGroundElevation:F1} m MSL (EGM96) |");
    w.WriteLine($"| **Drone Altitude** | {el.MeanDroneAltitudeMsl:F1} m MSL (EGM96) |");
    w.WriteLine();
    w.WriteLine("*AGL computed from `DJI_AbsoluteAltitude - SRTM_GroundElevation`. Both use EGM96 geoid datum.*");
    w.WriteLine();
  }

  /// <summary>Per-capture-group section with altitude/GSD, overlap, and gimbal detail.</summary>
  private static void WriteCaptureGroupSection(
    ref int section,
    CaptureGroup group,
    MissionReport report,
    TextWriter w)
  {
    w.WriteLine($"## {section++}. {group.Label}");
    w.WriteLine();

    // Summary line
    string flightIndexes = string.Join(", ", group.Flights.Select(f => $"#{f.Index}"));
    w.WriteLine($"*{group.TotalPhotos:N0} photos across {group.Flights.Count} flight(s) ({flightIndexes})*");
    w.WriteLine();

    // ── Altitude & GSD ──
    if (group.Overlap != null)
    {
      var ov = group.Overlap;

      w.WriteLine("### Altitude & GSD");
      w.WriteLine();
      w.WriteLine("| Parameter | Value |");
      w.WriteLine("|-----------|-------|");
      w.WriteLine($"| **Flight Altitude (relative)** | {ov.PrimaryAltitude:F1} m above takeoff |");
      w.WriteLine($"| **GSD** | {ov.GsdMeters * 100:F2} cm/px |");
      w.WriteLine($"| **Footprint** | {ov.FootprintWidthMeters:F1} x {ov.FootprintHeightMeters:F1} m |");
      w.WriteLine($"| **Ground Speed** | {ov.MeanGroundSpeed:F1} m/s |");
      w.WriteLine();
      w.WriteLine($"*GSD = altitude / calibrated_focal_length_px = {ov.PrimaryAltitude:F1} / " +
        $"{report.Equipment.CalibratedFocalLengthPx:F1} = {ov.GsdMeters * 100:F2} cm/px*");
      w.WriteLine();

      // ── Overlap ──
      w.WriteLine("### Overlap & Coverage");
      w.WriteLine();
      w.WriteLine("| Parameter | Value |");
      w.WriteLine("|-----------|-------|");
      w.WriteLine($"| **Forward Overlap** | {ov.ForwardOverlap * 100:F0}% |");
      w.WriteLine($"| **Side Overlap** | {ov.SideOverlap * 100:F0}% |");
      w.WriteLine($"| **Forward Spacing** | {ov.ForwardSpacingMeters:F1} m between nadir shots |");
      w.WriteLine($"| **Side Spacing** | {ov.SideSpacingMeters:F1} m between flight lines |");
      w.WriteLine($"| **Footprint** | {ov.FootprintWidthMeters:F1} m (W) x {ov.FootprintHeightMeters:F1} m (H) |");
      w.WriteLine($"| **Samples (fwd/side)** | {ov.ForwardSampleCount} / {ov.SideSampleCount} measurement pairs |");
      w.WriteLine();
    }
    else if (group.MinAltitude != null && group.MaxAltitude != null)
    {
      // Unclassified group — show altitude range only
      w.WriteLine("### Altitude");
      w.WriteLine();
      w.WriteLine("| Parameter | Value |");
      w.WriteLine("|-----------|-------|");
      w.WriteLine($"| **Altitude Range** | {group.MinAltitude:F0}–{group.MaxAltitude:F0} m |");
      w.WriteLine();
      w.WriteLine("*Overlap not computed for varying-altitude flights.*");
      w.WriteLine();
    }

    // ── Gimbal ──
    var gim = group.Gimbal;
    w.WriteLine("### Gimbal Configuration");
    w.WriteLine();
    w.WriteLine("| Parameter | Value |");
    w.WriteLine("|-----------|-------|");

    if (gim.HasSmartOblique)
    {
      w.WriteLine($"| **Mode** | Smart Oblique |");
      w.WriteLine($"| **Configured Angle** | {Math.Abs(gim.ObliqueAngle):F0}° from vertical |");
      w.WriteLine($"| **Forward Look** | pitch {gim.MeanForwardPitch:F1}° (roll~0°) — {gim.ObliqueForwardCount} shots |");
      w.WriteLine($"| **Backward Look** | pitch {gim.MeanBackwardPitch:F1}° (roll~180°) — {gim.ObliqueBackwardCount} shots |");
      w.WriteLine($"| **Nadir** | {gim.NadirCount} shots ({100.0 * gim.NadirCount / Math.Max(1, gim.TotalSampled):F0}%) |");
    }
    else
    {
      w.WriteLine($"| **Mode** | Fixed |");
      w.WriteLine($"| **Mean Pitch** | {gim.MeanPitch:F1}° |");
      w.WriteLine($"| **Nadir** | {gim.NadirCount} shots ({100.0 * gim.NadirCount / Math.Max(1, gim.TotalSampled):F0}%) |");
    }

    w.WriteLine($"| **Total Sampled** | {gim.TotalSampled} photos |");
    w.WriteLine();

    if (!string.IsNullOrEmpty(gim.Explanation))
    {
      w.WriteLine($"> {gim.Explanation}");
      w.WriteLine();
    }
  }

  /// <summary>Per-day flight tables with camera settings per flight. Includes group column.</summary>
  private static void WriteFlightTablesSection(ref int section, MissionReport report, TextWriter w)
  {
    w.WriteLine($"## {section++}. Flight Details");
    w.WriteLine();

    // Group flights by date
    var flightsByDate = report.Flights
      .GroupBy(f => DateOnly.FromDateTime(f.StartTime))
      .OrderBy(g => g.Key);

    bool hasMultipleGroups = report.CaptureGroups.Count > 1;

    foreach (var dayGroup in flightsByDate)
    {
      var flights = dayGroup.ToList();
      int dayPhotos = flights.Sum(f => f.PhotoCount);
      var dayDuration = TimeSpan.FromSeconds(flights.Sum(f => f.Duration.TotalSeconds));

      w.WriteLine($"### {dayGroup.Key:yyyy-MM-dd} — {flights.Count} flights, {dayPhotos:N0} photos, {FormatTimeSpan(dayDuration)}");
      w.WriteLine();

      // Table header — include Group column when there are multiple capture groups
      if (hasMultipleGroups)
      {
        w.WriteLine("| # | Group | Time | Duration | Photos | Alt (m) | Speed (m/s) | Shutter | f/ | ISO | Gap |");
        w.WriteLine("|---|-------|------|----------|--------|---------|-------------|---------|-----|-----|-----|");
      }
      else
      {
        w.WriteLine("| # | Time | Duration | Photos | Alt (m) | Speed (m/s) | Shutter | f/ | ISO | Gap |");
        w.WriteLine("|---|------|----------|--------|---------|-------------|---------|-----|-----|-----|");
      }

      foreach (var flight in flights)
      {
        var sampled = flight.SampledPhotos;

        // Time range
        string timeRange = $"{flight.StartTime:HH:mm}–{flight.EndTime:HH:mm}";

        // Altitude range from sampled relative altitudes
        string altStr = FormatRangeFromSamples(sampled, p => p.RelativeAltitude, "F0");

        // Ground speed
        string speedStr = FormatRangeFromSamples(sampled, p => p.GroundSpeed, "F1");

        // Exposure time formatted as fraction
        string shutterStr = FormatShutterRange(sampled);

        // Aperture
        string fStr = FormatRangeFromSamples(sampled, p => p.FNumber, "F1");

        // ISO
        string isoStr = FormatRangeFromSamples(sampled, p => (double?)p.Iso, "F0");

        // Gap before this flight
        string gapStr = flight.GapBefore != null ? FormatTimeSpan(flight.GapBefore.Value) : "—";

        if (hasMultipleGroups)
        {
          string groupLabel = flight.CaptureGroupLabel ?? "—";

          w.WriteLine($"| {flight.Index} | {groupLabel} | {timeRange} | {FormatTimeSpan(flight.Duration)} | " +
            $"{flight.PhotoCount:N0} | {altStr} | {speedStr} | {shutterStr} | {fStr} | {isoStr} | {gapStr} |");
        }
        else
        {
          w.WriteLine($"| {flight.Index} | {timeRange} | {FormatTimeSpan(flight.Duration)} | " +
            $"{flight.PhotoCount:N0} | {altStr} | {speedStr} | {shutterStr} | {fStr} | {isoStr} | {gapStr} |");
        }
      }

      w.WriteLine();
    }
  }

  /// <summary>Camera settings summary across all flights.</summary>
  private static void WriteCameraSummarySection(ref int section, MissionReport report, TextWriter w)
  {
    var allSamples = report.Flights.SelectMany(f => f.SampledPhotos).ToList();

    w.WriteLine($"## {section++}. Camera Settings Summary");
    w.WriteLine();

    // Exposure time distribution
    var shutters = allSamples.Where(p => p.ExposureTime != null).Select(p => p.ExposureTime!.Value).ToList();

    if (shutters.Count > 0)
    {
      var shutterGroups = shutters
        .GroupBy(s => FormatShutter(s))
        .OrderByDescending(g => g.Count())
        .Take(5);

      w.WriteLine("**Shutter Speed Distribution:**");

      foreach (var group in shutterGroups)
        w.WriteLine($"- {group.Key}: {group.Count()} samples ({100.0 * group.Count() / shutters.Count:F0}%)");

      w.WriteLine();
    }

    // ISO distribution
    var isos = allSamples.Where(p => p.Iso != null).Select(p => p.Iso!.Value).ToList();

    if (isos.Count > 0)
    {
      var isoGroups = isos
        .GroupBy(i => i)
        .OrderByDescending(g => g.Count())
        .Take(5);

      w.WriteLine("**ISO Distribution:**");

      foreach (var group in isoGroups)
        w.WriteLine($"- ISO {group.Key}: {group.Count()} samples ({100.0 * group.Count() / isos.Count:F0}%)");

      w.WriteLine();
    }

    // Aperture
    var apertures = allSamples.Where(p => p.FNumber != null).Select(p => p.FNumber!.Value).Distinct().ToList();

    if (apertures.Count > 0)
    {
      w.Write("**Aperture:** ");
      w.WriteLine(apertures.Count == 1
        ? $"f/{apertures[0]:F1} (fixed)"
        : $"f/{apertures.Min():F1} – f/{apertures.Max():F1}");
      w.WriteLine();
    }

    // Sensor temperature range
    var temps = allSamples.Where(p => p.SensorTemperature != null).Select(p => p.SensorTemperature!.Value).ToList();

    if (temps.Count > 0)
    {
      w.WriteLine($"**Sensor Temperature:** {temps.Min():F0}–{temps.Max():F0}°C");
      w.WriteLine();
    }
  }

  /// <summary>Methodology notes.</summary>
  private static void WriteMethodologySection(ref int section, MissionReport report, TextWriter w)
  {
    w.WriteLine($"## {section++}. Methodology Notes");
    w.WriteLine();
    w.WriteLine("- **Filename Parsing**: Timestamps extracted from DJI filename convention (`DJI_YYYYMMDDHHMMSS_NNNN_V.jpg`) for zero-I/O timeline construction.");
    w.WriteLine("- **Flight Segmentation**: Raw segments split at >30s gaps, merged if gap <120s and DJI sequence number doesn't reset to 1 (indicating a power cycle).");
    w.WriteLine("- **Capture Group Classification**: Flights classified by altitude stability (std dev ≤ 10 m = stable). Stable flights grouped by median altitude rounded to nearest 5 m. Groups below 30 photos merged into a varying-altitude catch-all group.");
    w.WriteLine("- **Metadata Sampling**: EXIF and DJI XMP read from evenly-spaced sample photos per flight. Raw binary EXIF parsing and regex XMP extraction from first 128KB of each JPEG.");
    w.WriteLine("- **Forward Overlap**: Median haversine distance between consecutive nadir shots on the same heading, divided by footprint height.");
    w.WriteLine("- **Side Overlap**: Median cross-track distance at heading-reversal points (flight line changes), divided by footprint width.");
    w.WriteLine("- **GSD**: `altitude / calibrated_focal_length_px` using DJI's calibrated focal length from XMP metadata.");

    if (report.Elevation != null)
    {
      w.WriteLine("- **AGL (Above Ground Level)**: `DJI_AbsoluteAltitude - SRTM_GroundElevation`. Both use EGM96 geoid datum — no CRS transform needed. Ground elevation from Open-Meteo SRTM API.");
      w.WriteLine($"  - Note: EGM96 geoid undulation at this location is approximately +29 m. Ellipsoidal height = MSL altitude + geoid undulation.");
    }

    w.WriteLine("- **Gimbal Correlation**: Forward-look oblique (roll~0°) and backward-look oblique (roll~180°) are the same physical angle from vertical. Pitch values differ because `90° - |forward_pitch| ≈ |backward_pitch|`.");
    w.WriteLine();
    w.WriteLine("---");
    w.WriteLine();
    w.WriteLine("*Report generated by [DroneDatasetAnalyzer](https://github.com/Alos-no/DroneDatasetAnalyzer) — a zero-dependency .NET tool for DJI drone dataset analysis.*");
  }

  #endregion


  #region Formatting helpers

  /// <summary>
  /// Formats a TimeSpan as "H:MM:SS" or "M:SS" depending on duration.
  /// </summary>
  private static string FormatTimeSpan(TimeSpan ts)
  {
    if (ts.TotalHours >= 1)
      return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";

    return $"{ts.Minutes}:{ts.Seconds:D2}";
  }

  /// <summary>
  /// Formats an exposure time as a human-readable fraction (e.g., "1/2000") or decimal seconds.
  /// </summary>
  private static string FormatShutter(double exposureTime)
  {
    if (exposureTime <= 0)
      return "—";

    if (exposureTime >= 1)
      return $"{exposureTime:F1}s";

    // Express as 1/N fraction
    int denominator = (int)Math.Round(1.0 / exposureTime);

    return $"1/{denominator}";
  }

  /// <summary>
  /// Formats the shutter speed range across sampled photos in a flight.
  /// If all the same, returns a single value. Otherwise returns "min–max".
  /// </summary>
  private static string FormatShutterRange(List<PhotoMetadata> samples)
  {
    var exposures = samples
      .Where(p => p.ExposureTime != null)
      .Select(p => p.ExposureTime!.Value)
      .ToList();

    if (exposures.Count == 0)
      return "—";

    double min = exposures.Min();
    double max = exposures.Max();

    if (Math.Abs(min - max) < 0.000001)
      return FormatShutter(min);

    // Show range as "1/fastest – 1/slowest"
    return $"{FormatShutter(max)}–{FormatShutter(min)}";
  }

  /// <summary>
  /// Extracts a numeric property from sampled photos and formats as a range or single value.
  /// </summary>
  private static string FormatRangeFromSamples(
    List<PhotoMetadata> samples,
    Func<PhotoMetadata, double?> selector,
    string format)
  {
    var values = samples
      .Select(selector)
      .Where(v => v != null)
      .Select(v => v!.Value)
      .ToList();

    if (values.Count == 0)
      return "—";

    double min = values.Min();
    double max = values.Max();

    // If range is very small (< 5% of mean), show single value
    double mean = values.Average();

    if (mean == 0 || Math.Abs(max - min) / Math.Abs(mean) < 0.05)
      return mean.ToString(format, CultureInfo.InvariantCulture);

    return $"{min.ToString(format, CultureInfo.InvariantCulture)}–{max.ToString(format, CultureInfo.InvariantCulture)}";
  }

  /// <summary>
  /// Describes a DJI RTK flag value as human-readable text.
  /// </summary>
  private static string DescribeRtkFlag(int flag) => flag switch
  {
    0 => "No RTK",
    16 => "RTK Float",
    34 => "RTK Fixed",
    50 => "RTK Fixed (w/ base station)",
    _ => $"RTK code {flag}",
  };

  #endregion
}
