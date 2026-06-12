namespace DroneDatasetAnalyzer;

/// <summary>
/// Orchestrates the full DJI dataset analysis pipeline:
/// 1. Parse DJI filenames for timestamps and sequence numbers (zero I/O)
/// 2. Segment photos into flights by timestamp gaps and sequence resets
/// 3. Sample N photos per flight for XMP + EXIF metadata extraction
/// 4. Read a consecutive block of photos for overlap analysis
/// 5. Analyze gimbal angles and smart oblique correlation
/// 6. Calculate forward and side overlap from GPS positions
/// 7. Query ground elevation API for AGL analysis
/// 8. Compile everything into a <see cref="MissionReport"/>
/// </summary>
public static class MissionAnalyzer
{
  #region Public API

  /// <summary>
  /// Runs the complete analysis pipeline on a directory of DJI drone photos.
  /// </summary>
  /// <param name="directory">Path to the directory containing JPEG photos.</param>
  /// <param name="options">Analysis configuration.</param>
  /// <param name="log">Progress callback for console output.</param>
  /// <returns>Complete mission report with all analysis results.</returns>
  public static async Task<MissionReport> AnalyzeAsync(
    string directory,
    AnalysisOptions options,
    Action<string> log)
  {
    // Step 1: List all JPEG files and parse DJI filenames (zero I/O beyond directory listing)
    log("Scanning directory for DJI photos...");
    var allFiles = ParseDjiFilenames(directory);
    log($"  Found {allFiles.Count:N0} DJI photos");

    if (allFiles.Count == 0)
      throw new InvalidOperationException("No DJI photos found in the specified directory.");

    // Step 2: Segment into flights based on timestamp gaps and sequence resets
    log("Segmenting into flights...");
    var flights = SegmentIntoFlights(allFiles);
    log($"  Identified {flights.Count} flights across {flights.Select(f => f.StartTime.Date).Distinct().Count()} days");

    // Step 3: Read metadata from sampled photos per flight
    log($"Reading metadata ({options.SamplesPerFlight} samples/flight)...");
    int totalSampled = 0;

    for (int i = 0; i < flights.Count; i++)
    {
      SampleFlightMetadata(flights[i], options.SamplesPerFlight);
      totalSampled += flights[i].SampledPhotos.Count;
      log($"  Flight {i + 1}/{flights.Count}: read {flights[i].SampledPhotos.Count} photos ({flights[i].PhotoCount:N0} total)");
    }

    // Step 4: Read a consecutive block for overlap analysis
    log($"Reading consecutive block for overlap analysis ({options.OverlapBlockSize} photos)...");
    var overlapBlock = ReadConsecutiveBlockForOverlap(flights, options.OverlapBlockSize, log);
    log($"  Read {overlapBlock.Count} consecutive photos for overlap computation");

    // Collect all sampled photos across flights for aggregate analyses
    var allSamples = flights.SelectMany(f => f.SampledPhotos).ToList();

    // Step 5: Extract equipment info from the first available sample
    log("Extracting equipment info...");
    var equipment = ExtractEquipmentInfo(allSamples);

    // Step 6: Compute geographic location summary
    log("Computing location summary...");
    var location = ComputeLocationInfo(allSamples);

    // Step 7: Analyze gimbal angles and smart oblique correlation
    log("Analyzing gimbal configuration...");
    var gimbal = AnalyzeGimbal(allSamples);

    // Step 8: Calculate forward and side overlap
    log("Calculating overlap...");
    var overlap = CalculateOverlap(overlapBlock, allSamples);

    // Step 9: Query elevation API for AGL analysis
    ElevationAnalysis? elevation = null;

    if (!options.SkipElevation)
    {
      log("Querying ground elevation API...");
      elevation = await AnalyzeElevationAsync(allSamples, log);
    }

    // Step 10: Compile the complete mission report
    log("Compiling report...");
    var dates = flights.Select(f => DateOnly.FromDateTime(f.StartTime)).Distinct().OrderBy(d => d).ToList();

    var report = new MissionReport
    {
      Equipment = equipment,
      Location = location,
      Gimbal = gimbal,
      Overlap = overlap,
      Elevation = elevation,
      TotalPhotos = allFiles.Count,
      CaptureDays = dates.Count,
      TotalCaptureTime = TimeSpan.FromSeconds(flights.Sum(f => f.Duration.TotalSeconds)),
    };

    report.Flights.AddRange(flights);
    report.Dates.AddRange(dates);

    // Battery swaps: count transitions between flights on the same day
    int swaps = 0;
    for (int i = 1; i < flights.Count; i++)
    {
      if (flights[i].StartTime.Date == flights[i - 1].StartTime.Date)
        swaps++;
    }
    report.BatterySwaps = swaps;

    return report;
  }

  #endregion


  #region Step 1: Filename parsing

  /// <summary>
  /// Lists all JPEG files in the directory and parses DJI filename conventions
  /// to extract timestamps and sequence numbers. Sorted by (timestamp, sequence).
  /// </summary>
  private static List<DjiFileInfo> ParseDjiFilenames(string directory)
  {
    // Enumerate all JPEG files in the directory (non-recursive).
    // Use a single "*.jpg" pattern; on Windows (NTFS), this is case-insensitive
    // and already matches *.JPG. Deduplication via HashSet handles any edge cases.
    var jpegFiles = Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
      .Concat(Directory.EnumerateFiles(directory, "*.jpeg", SearchOption.TopDirectoryOnly));
    var deduplicatedFiles = new HashSet<string>(jpegFiles, StringComparer.OrdinalIgnoreCase);

    var parsed = new List<DjiFileInfo>();

    foreach (var filePath in deduplicatedFiles)
    {
      var info = DjiFilenameParser.TryParse(filePath);

      if (info.HasValue)
        parsed.Add(info.Value);
    }

    // Sort by timestamp first, then by sequence number for photos within the same second
    parsed.Sort((a, b) =>
    {
      int cmp = a.LocalTimestamp.CompareTo(b.LocalTimestamp);
      return cmp != 0 ? cmp : a.SequenceNumber.CompareTo(b.SequenceNumber);
    });

    return parsed;
  }

  #endregion


  #region Step 2: Flight segmentation

  /// <summary>
  /// Groups photos into flights by detecting timestamp gaps and DJI sequence resets.
  ///
  /// Algorithm:
  /// 1. Split into raw segments at gaps &gt; 30 seconds
  /// 2. Merge adjacent segments into a flight if gap &lt; 120s AND the sequence counter
  ///    didn't reset to 1 (a reset indicates a power cycle = new flight)
  /// </summary>
  private static List<FlightInfo> SegmentIntoFlights(List<DjiFileInfo> photos)
  {
    if (photos.Count == 0)
      return [];

    // Phase 1: Split into raw segments at timestamp gaps > 30 seconds
    var rawSegments = new List<List<DjiFileInfo>>();
    var currentSegment = new List<DjiFileInfo> { photos[0] };

    for (int i = 1; i < photos.Count; i++)
    {
      double gapSeconds = (photos[i].LocalTimestamp - photos[i - 1].LocalTimestamp).TotalSeconds;

      if (gapSeconds > 30)
      {
        rawSegments.Add(currentSegment);
        currentSegment = [photos[i]];
      }
      else
      {
        currentSegment.Add(photos[i]);
      }
    }

    rawSegments.Add(currentSegment);

    // Phase 2: Merge brief-pause segments into flights
    // A pause is a battery swap if:
    //   - Gap >= 120 seconds, OR
    //   - The DJI sequence counter resets to 1 (indicating a power cycle)
    var flights = new List<FlightInfo>();
    var mergedPhotos = new List<DjiFileInfo>(rawSegments[0]);

    for (int i = 1; i < rawSegments.Count; i++)
    {
      var prevSegment = rawSegments[i - 1];
      var currSegment = rawSegments[i];

      double gapSeconds = (currSegment[0].LocalTimestamp - prevSegment[^1].LocalTimestamp).TotalSeconds;
      bool sequenceContinuous = currSegment[0].SequenceNumber > 1;

      if (gapSeconds < 120 && sequenceContinuous)
      {
        // Brief pause mid-flight; merge into current flight
        mergedPhotos.AddRange(currSegment);
      }
      else
      {
        // Battery swap or power cycle; start new flight
        flights.Add(CreateFlightInfo(mergedPhotos, flights.Count + 1));
        mergedPhotos = new List<DjiFileInfo>(currSegment);
      }
    }

    // Add the last flight
    flights.Add(CreateFlightInfo(mergedPhotos, flights.Count + 1));

    // Compute inter-flight gap durations
    for (int i = 1; i < flights.Count; i++)
      flights[i].GapBefore = flights[i].StartTime - flights[i - 1].EndTime;

    return flights;
  }

  /// <summary>
  /// Creates a <see cref="FlightInfo"/> from a list of merged photo segments.
  /// </summary>
  private static FlightInfo CreateFlightInfo(List<DjiFileInfo> photos, int index)
  {
    var flight = new FlightInfo { Index = index };
    flight.Photos.AddRange(photos);

    return flight;
  }

  #endregion


  #region Step 3: Metadata sampling

  /// <summary>
  /// Reads full EXIF + XMP metadata from evenly-spaced sample photos within a flight.
  /// Uses ~samplesPerFlight photos spaced at even intervals across the flight.
  /// </summary>
  private static void SampleFlightMetadata(FlightInfo flight, int samplesPerFlight)
  {
    // Select sample indices evenly distributed across the flight
    var sampleIndices = SelectSampleIndices(flight.PhotoCount, samplesPerFlight);

    foreach (int idx in sampleIndices)
    {
      var fileInfo = flight.Photos[idx];
      var metadata = JpegMetadataReader.ReadFromFile(fileInfo);

      if (metadata != null)
        flight.SampledPhotos.Add(metadata);
    }
  }

  /// <summary>
  /// Selects evenly-spaced sample indices from a range [0, count).
  /// Always includes the first and last elements.
  /// </summary>
  private static List<int> SelectSampleIndices(int count, int sampleCount)
  {
    if (count <= sampleCount)
      return Enumerable.Range(0, count).ToList();

    var indices = new List<int>(sampleCount);

    for (int i = 0; i < sampleCount; i++)
    {
      // Map sample index [0..sampleCount-1] to photo index [0..count-1]
      int photoIndex = (int)((long)i * (count - 1) / (sampleCount - 1));
      indices.Add(photoIndex);
    }

    return indices.Distinct().ToList();
  }

  #endregion


  #region Step 4: Consecutive block for overlap

  /// <summary>
  /// Reads a consecutive block of photos from the middle of the longest primary-altitude flight.
  /// This block is used for computing forward and side overlap (requires consecutive GPS positions).
  /// </summary>
  private static List<PhotoMetadata> ReadConsecutiveBlockForOverlap(
    List<FlightInfo> flights,
    int blockSize,
    Action<string> log)
  {
    // Find the primary (most common) relative altitude across all sampled data
    var allSamples = flights.SelectMany(f => f.SampledPhotos).ToList();
    double primaryAlt = FindPrimaryAltitude(allSamples);

    // Find the longest flight at the primary altitude
    var primaryFlights = flights
      .Where(f => f.SampledPhotos.Any(p =>
        p.RelativeAltitude != null &&
        Math.Abs(p.RelativeAltitude.Value - primaryAlt) < 15))
      .OrderByDescending(f => f.PhotoCount)
      .ToList();

    if (primaryFlights.Count == 0)
    {
      log("  Warning: No flights found at primary altitude. Using longest flight.");
      primaryFlights = flights.OrderByDescending(f => f.PhotoCount).Take(1).ToList();
    }

    var targetFlight = primaryFlights[0];

    // Read a block from ~25% into the flight (not the dead center).
    // Edge-of-coverage lines are shorter, so reading near the start catches more
    // flight-line transitions than reading from the exact middle of a long line.
    int startIdx = Math.Max(0, (targetFlight.PhotoCount - blockSize) / 4);
    int endIdx = Math.Min(targetFlight.PhotoCount, startIdx + blockSize);
    int actualBlockSize = endIdx - startIdx;

    var block = new List<PhotoMetadata>(actualBlockSize);
    int readSuccessCount = 0;

    for (int i = startIdx; i < endIdx; i++)
    {
      var fileInfo = targetFlight.Photos[i];
      var metadata = JpegMetadataReader.ReadFromFile(fileInfo);

      if (metadata != null)
      {
        block.Add(metadata);
        readSuccessCount++;
      }

      // Progress feedback every 50 photos
      if ((i - startIdx + 1) % 50 == 0)
        log($"  Read {i - startIdx + 1}/{actualBlockSize} photos...");
    }

    return block;
  }

  /// <summary>
  /// Finds the most common relative altitude (rounded to 5m) across all sampled photos.
  /// </summary>
  private static double FindPrimaryAltitude(List<PhotoMetadata> samples)
  {
    var altitudes = samples
      .Where(p => p.RelativeAltitude != null)
      .Select(p => Math.Round(p.RelativeAltitude!.Value / 5) * 5)
      .ToList();

    if (altitudes.Count == 0)
      return 100; // Default fallback

    return altitudes
      .GroupBy(a => a)
      .OrderByDescending(g => g.Count())
      .First()
      .Key;
  }

  #endregion


  #region Step 5: Equipment info

  /// <summary>
  /// Extracts equipment identification from the first available sampled photo.
  /// </summary>
  private static EquipmentInfo ExtractEquipmentInfo(List<PhotoMetadata> samples)
  {
    var info = new EquipmentInfo();

    // Use the first sample that has product info
    var reference = samples.FirstOrDefault(s => s.ProductName != null)
                    ?? samples.FirstOrDefault();

    if (reference == null)
      return info;

    info.ProductName = reference.ProductName ?? "Unknown";
    info.DroneSerialNumber = reference.DroneSerialNumber ?? "Unknown";
    info.CameraSerialNumber = reference.CameraSerialNumber ?? "Unknown";
    info.FocalLengthMm = reference.FocalLengthMm ?? 0;
    info.FocalLength35mm = reference.FocalLength35mm ?? 0;
    info.CalibratedFocalLengthPx = reference.CalibratedFocalLength ?? 0;
    info.ImageWidth = reference.ImageWidth ?? 0;
    info.ImageHeight = reference.ImageHeight ?? 0;
    info.ShutterType = reference.ShutterType ?? "Unknown";
    info.Megapixels = (double)(info.ImageWidth * info.ImageHeight) / 1_000_000.0;

    // Derive sensor dimensions from actual/equiv focal length ratio
    // sensor_width_mm = actual_fl_mm * (36mm / 35mm_equiv_fl)
    if (info.FocalLengthMm > 0 && info.FocalLength35mm > 0)
    {
      info.SensorWidthMm = info.FocalLengthMm * 36.0 / info.FocalLength35mm;
      double aspectRatio = info.ImageWidth > 0 && info.ImageHeight > 0
        ? (double)info.ImageHeight / info.ImageWidth
        : 0.75;
      info.SensorHeightMm = info.SensorWidthMm * aspectRatio;
    }

    return info;
  }

  #endregion


  #region Step 6: Location info

  /// <summary>
  /// Computes the geographic center, bounding box, and coverage area from GPS positions.
  /// </summary>
  private static LocationInfo ComputeLocationInfo(List<PhotoMetadata> samples)
  {
    var gps = samples.Where(s => s.Latitude != null && s.Longitude != null).ToList();
    var info = new LocationInfo();

    if (gps.Count == 0)
      return info;

    // Bounding box
    info.BboxNorth = gps.Max(p => p.Latitude!.Value);
    info.BboxSouth = gps.Min(p => p.Latitude!.Value);
    info.BboxEast = gps.Max(p => p.Longitude!.Value);
    info.BboxWest = gps.Min(p => p.Longitude!.Value);

    // Center
    info.CenterLatitude = (info.BboxNorth + info.BboxSouth) / 2;
    info.CenterLongitude = (info.BboxEast + info.BboxWest) / 2;

    // Coverage area (approximate rectangle in hectares)
    double widthM = MathHelpers.HaversineMeters(
      info.CenterLatitude, info.BboxWest,
      info.CenterLatitude, info.BboxEast);
    double heightM = MathHelpers.HaversineMeters(
      info.BboxSouth, info.CenterLongitude,
      info.BboxNorth, info.CenterLongitude);
    info.CoverageAreaHectares = widthM * heightM / 10_000.0;

    // Detect UTC offset by comparing filename timestamp vs XMP UTC exposure time
    var tzSample = samples.FirstOrDefault(s => s.UtcAtExposure != null);

    if (tzSample != null)
    {
      var offset = tzSample.LocalTimestamp - tzSample.UtcAtExposure!.Value;
      // Round to nearest 30 minutes for clean timezone representation
      info.UtcOffset = TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes / 30) * 30);
    }

    return info;
  }

  #endregion


  #region Step 7: Gimbal analysis

  /// <summary>
  /// Analyzes gimbal angles across all sampled photos to determine the smart oblique configuration.
  ///
  /// DJI Smart Oblique captures photos at multiple orientations per waypoint:
  /// - Nadir (pitch ~ -90°, looking straight down)
  /// - Forward oblique (pitch ~ -45°, roll ~ 0°, looking forward at configured angle)
  /// - Backward oblique (pitch ~ -55°, roll ~ ±180°, looking backward — same physical angle as forward)
  /// - Side obliques (pitch ~ -45° or -55°, at various relative yaw angles)
  ///
  /// The backward look at -55° is NOT a different angle setting — it's the same -45° oblique
  /// viewed from behind: physical_angle = 90° - |pitch| → both -45° and -55° give 45° from vertical.
  /// </summary>
  private static GimbalAnalysis AnalyzeGimbal(List<PhotoMetadata> samples)
  {
    var withGimbal = samples.Where(s => s.GimbalPitch != null).ToList();

    if (withGimbal.Count == 0)
      return new GimbalAnalysis();

    // Classify shots by gimbal pitch ranges
    var nadir = withGimbal.Where(p => p.GimbalPitch <= -80).ToList();
    var oblique = withGimbal.Where(p => p.GimbalPitch is >= -60 and < -30).ToList();
    var horizon = withGimbal.Where(p => p.GimbalPitch >= -30).ToList();
    var other = withGimbal.Where(p => p.GimbalPitch is > -80 and < -60).ToList();

    // Correlate oblique shots with look direction via roll angle
    // Roll ~ 0° = forward/sideways look, Roll ~ ±180° = backward look (camera inverted)
    var forwardLook = oblique.Where(p => Math.Abs(p.GimbalRoll ?? 0) < 90).ToList();
    var backwardLook = oblique.Where(p => Math.Abs(p.GimbalRoll ?? 0) > 90).ToList();

    // The true configured angle is the forward look pitch (e.g., -45°)
    double forwardPitch = forwardLook.Count > 0
      ? forwardLook.Average(p => p.GimbalPitch!.Value)
      : oblique.Count > 0 ? oblique.Average(p => p.GimbalPitch!.Value) : -45;

    double backwardPitch = backwardLook.Count > 0
      ? backwardLook.Average(p => p.GimbalPitch!.Value)
      : -55;

    // Build the correlation explanation
    string explanation;

    if (forwardLook.Count > 0 && backwardLook.Count > 0)
    {
      double realAngle = Math.Abs(forwardPitch);
      double complementAngle = 90 - realAngle;

      explanation = $"Smart Oblique with {realAngle:F0}° from vertical setting. " +
        $"Forward-looking shots at pitch {forwardPitch:F1}° (roll~0°) and backward-looking " +
        $"shots at pitch {backwardPitch:F1}° (roll~180°) are the same physical {realAngle:F0}° angle. " +
        $"Complement: 90° - {realAngle:F0}° = {complementAngle:F0}° matches the backward pitch of ~{backwardPitch:F1}°.";
    }
    else
    {
      explanation = "Unable to correlate oblique directions (insufficient data with roll information).";
    }

    return new GimbalAnalysis
    {
      ObliqueAngle = forwardPitch,
      NadirCount = nadir.Count,
      ObliqueForwardCount = forwardLook.Count,
      ObliqueBackwardCount = backwardLook.Count,
      HorizonCount = horizon.Count + other.Count,
      TotalSampled = withGimbal.Count,
      MeanForwardPitch = forwardPitch,
      MeanBackwardPitch = backwardPitch,
      Explanation = explanation,
    };
  }

  #endregion


  #region Step 8: Overlap calculation

  /// <summary>
  /// Calculates forward (along-track) and side (cross-track) overlap percentages.
  ///
  /// Forward overlap: Computed from the median distance between consecutive nadir shots
  /// on the same flight line, divided by the ground footprint height.
  ///
  /// Side overlap: Computed from the median cross-track distance between adjacent flight lines,
  /// divided by the ground footprint width.
  ///
  /// Both require the consecutive photo block with GPS positions and gimbal angles.
  /// </summary>
  private static OverlapAnalysis CalculateOverlap(
    List<PhotoMetadata> overlapBlock,
    List<PhotoMetadata> allSamples)
  {
    var result = new OverlapAnalysis();

    // Determine primary altitude and sensor parameters
    var altitudes = allSamples
      .Where(p => p.RelativeAltitude != null)
      .Select(p => p.RelativeAltitude!.Value)
      .ToList();

    result.PrimaryAltitude = altitudes.Count > 0
      ? altitudes.GroupBy(a => Math.Round(a / 5) * 5)
          .OrderByDescending(g => g.Count())
          .First().Key
      : 100;

    // Get calibrated focal length in pixels
    double focalPx = allSamples
      .FirstOrDefault(p => p.CalibratedFocalLength != null)?.CalibratedFocalLength ?? 3725;
    int imgW = allSamples.FirstOrDefault(p => p.ImageWidth != null)?.ImageWidth ?? 5280;
    int imgH = allSamples.FirstOrDefault(p => p.ImageHeight != null)?.ImageHeight ?? 3956;

    // Ground sample distance (meters/pixel)
    result.GsdMeters = result.PrimaryAltitude / focalPx;

    // Ground footprint dimensions at primary altitude
    result.FootprintWidthMeters = result.PrimaryAltitude / focalPx * imgW;
    result.FootprintHeightMeters = result.PrimaryAltitude / focalPx * imgH;

    // Average ground speed from sampled data
    var speeds = allSamples
      .Where(p => p.GroundSpeed != null && p.GroundSpeed > 1)
      .Select(p => p.GroundSpeed!.Value)
      .ToList();
    result.MeanGroundSpeed = speeds.Count > 0 ? speeds.Average() : 8.0;

    // Filter overlap block to nadir shots with GPS data
    var nadir = overlapBlock
      .Where(p => p.GimbalPitch != null && p.GimbalPitch <= -80 &&
                  p.Latitude != null && p.Longitude != null &&
                  p.FlightYaw != null)
      .ToList();

    // --- Forward overlap ---
    // Measure distance between consecutive nadir shots on the same flight line (same heading)
    var forwardDistances = new List<double>();

    for (int i = 0; i < nadir.Count - 1; i++)
    {
      // Check that consecutive nadir shots are on the same heading (within 20°)
      double yawDiff = Math.Abs(MathHelpers.NormalizeAngle(
        nadir[i].FlightYaw!.Value - nadir[i + 1].FlightYaw!.Value));

      if (yawDiff < 20)
      {
        double dist = MathHelpers.HaversineMeters(
          nadir[i].Latitude!.Value, nadir[i].Longitude!.Value,
          nadir[i + 1].Latitude!.Value, nadir[i + 1].Longitude!.Value);

        // Filter out unreasonable distances (stationary or jumping)
        if (dist > 1 && dist < 200)
          forwardDistances.Add(dist);
      }
    }

    if (forwardDistances.Count > 0)
    {
      result.ForwardSpacingMeters = MathHelpers.Median(forwardDistances);
      result.ForwardOverlap = Math.Clamp(
        1.0 - result.ForwardSpacingMeters / result.FootprintHeightMeters, 0, 1);
      result.ForwardSampleCount = forwardDistances.Count;
    }

    // --- Side overlap ---
    // Detect flight-line changes: consecutive nadir shots where heading flips ~180°
    var crossTrackDistances = new List<double>();

    for (int i = 0; i < nadir.Count - 1; i++)
    {
      double yawFlip = Math.Abs(MathHelpers.NormalizeAngle(
        nadir[i].FlightYaw!.Value - nadir[i + 1].FlightYaw!.Value));

      // Heading reversal > 120° indicates a line change
      if (yawFlip > 120)
      {
        // Compute cross-track (perpendicular) component of the displacement
        double bearingRad = MathHelpers.DegreesToRadians(nadir[i].FlightYaw!.Value);
        double dLat = nadir[i + 1].Latitude!.Value - nadir[i].Latitude!.Value;
        double dLon = (nadir[i + 1].Longitude!.Value - nadir[i].Longitude!.Value)
                      * Math.Cos(MathHelpers.DegreesToRadians(nadir[i].Latitude!.Value));

        // Flight direction unit vector
        double fx = Math.Sin(bearingRad);
        double fy = Math.Cos(bearingRad);

        // Cross-track distance = |displacement × flight_direction| (2D cross product)
        double crossTrack = Math.Abs(dLon * fy - dLat * fx);
        double crossTrackMeters = crossTrack * 111_320; // degrees to meters at equator-ish

        // Filter unreasonable values
        if (crossTrackMeters > 5 && crossTrackMeters < 300)
          crossTrackDistances.Add(crossTrackMeters);
      }
    }

    if (crossTrackDistances.Count > 0)
    {
      result.SideSpacingMeters = MathHelpers.Median(crossTrackDistances);
      result.SideOverlap = Math.Clamp(
        1.0 - result.SideSpacingMeters / result.FootprintWidthMeters, 0, 1);
      result.SideSampleCount = crossTrackDistances.Count;
    }
    else
    {
      // Fallback: estimate side spacing from cross-track clustering of ALL sampled GPS data.
      // When the consecutive block sits entirely on one flight line, this method detects
      // line spacing by projecting all nadir positions perpendicular to the flight direction
      // and finding consistent gaps between clusters.
      EstimateSideOverlapFromClustering(allSamples, result);
    }

    return result;
  }

  /// <summary>
  /// Fallback side-overlap estimator: projects all sampled nadir GPS positions
  /// perpendicular to the dominant flight direction and measures the spacing
  /// between cross-track clusters (each cluster = one flight line).
  ///
  /// This works even with sparse samples (9 per flight × 20 flights = 180 positions)
  /// because the flight lines form clear linear bands when projected cross-track.
  /// </summary>
  private static void EstimateSideOverlapFromClustering(
    List<PhotoMetadata> allSamples,
    OverlapAnalysis result)
  {
    // Collect nadir positions at the primary altitude with flight heading data
    var nadir = allSamples
      .Where(p => p.GimbalPitch != null && p.GimbalPitch <= -80 &&
                  p.Latitude != null && p.Longitude != null &&
                  p.FlightYaw != null && p.RelativeAltitude != null &&
                  Math.Abs(p.RelativeAltitude.Value - result.PrimaryAltitude) < 15)
      .ToList();

    if (nadir.Count < 6)
      return;

    // Compute dominant flight direction: take the median flight yaw
    // (circular median via quadrant-aware averaging)
    var yaws = nadir.Select(p => p.FlightYaw!.Value).ToList();
    double dominantBearing = ComputeDominantBearing(yaws);
    double bearingRad = MathHelpers.DegreesToRadians(dominantBearing);

    // Reference point for local coordinate conversion
    double refLat = nadir[0].Latitude!.Value;
    double refLon = nadir[0].Longitude!.Value;
    double cosLat = Math.Cos(MathHelpers.DegreesToRadians(refLat));

    // Project each nadir position onto the perpendicular (cross-track) axis
    // Perpendicular to bearing: rotate bearing by 90°
    double perpBearingRad = bearingRad + Math.PI / 2;
    double perpX = Math.Sin(perpBearingRad);
    double perpY = Math.Cos(perpBearingRad);

    var projections = nadir.Select(p =>
    {
      // Convert GPS delta to local meters
      double dx = (p.Longitude!.Value - refLon) * cosLat * 111_320;
      double dy = (p.Latitude!.Value - refLat) * 111_320;

      // Dot product with perpendicular axis gives cross-track displacement
      return dx * perpX + dy * perpY;
    })
    .OrderBy(v => v)
    .ToList();

    // Detect gaps between cross-track clusters.
    // Within a flight line, nadir positions cluster within ~5m cross-track.
    // Between lines, the gap is the line spacing (typically 30-60m).
    // Use a threshold of 10m to distinguish within-line noise from between-line gaps.
    double gapThreshold = result.FootprintWidthMeters * 0.08; // 8% of footprint = ~12m
    gapThreshold = Math.Max(gapThreshold, 10); // at least 10m

    var lineGaps = new List<double>();

    for (int i = 0; i < projections.Count - 1; i++)
    {
      double gap = projections[i + 1] - projections[i];

      if (gap > gapThreshold)
        lineGaps.Add(gap);
    }

    if (lineGaps.Count >= 2)
    {
      // Use median to filter outliers (e.g., edge-of-area spacing)
      result.SideSpacingMeters = MathHelpers.Median(lineGaps);
      result.SideOverlap = Math.Clamp(
        1.0 - result.SideSpacingMeters / result.FootprintWidthMeters, 0, 1);
      result.SideSampleCount = lineGaps.Count;
    }
  }

  /// <summary>
  /// Computes the dominant flight bearing from a list of yaw angles (degrees).
  /// Handles the 0°/360° wrap-around by normalizing all angles relative to the most common quadrant,
  /// then picks the dominant direction (most yaws are either ~N-S or ~E-W along flight lines).
  /// </summary>
  private static double ComputeDominantBearing(List<double> yaws)
  {
    if (yaws.Count == 0)
      return 0;

    // Normalize to [0, 360) and group into 10° bins
    var normalized = yaws.Select(y => ((y % 360) + 360) % 360).ToList();

    // Collapse reciprocal headings: yaw and yaw+180 are the same flight line direction
    var collapsed = normalized.Select(y => y >= 180 ? y - 180 : y).ToList();

    // Find the modal 10° bin
    double modalBearing = collapsed
      .GroupBy(y => Math.Round(y / 10) * 10)
      .OrderByDescending(g => g.Count())
      .First()
      .Key;

    return modalBearing;
  }

  #endregion


  #region Step 9: Elevation / AGL analysis

  /// <summary>
  /// Queries ground elevation for sampled GPS positions and computes AGL statistics.
  /// DJI AbsoluteAltitude is MSL (EGM96 datum), matching the SRTM data from Open-Meteo.
  /// AGL = AbsoluteAltitude - GroundElevation (both in same datum, no transform needed).
  /// </summary>
  private static async Task<ElevationAnalysis?> AnalyzeElevationAsync(
    List<PhotoMetadata> samples,
    Action<string> log)
  {
    // Filter to photos with GPS and absolute altitude data
    var gpsPhotos = samples
      .Where(s => s.Latitude != null && s.Longitude != null && s.AbsoluteAltitude != null)
      .ToList();

    if (gpsPhotos.Count == 0)
    {
      log("  No GPS data available for elevation analysis.");
      return null;
    }

    // Sample up to 40 photos for the elevation query (spread evenly)
    var elevSample = gpsPhotos.Count <= 40
      ? gpsPhotos
      : SelectSampleIndices(gpsPhotos.Count, 40).Select(i => gpsPhotos[i]).ToList();

    var coords = elevSample
      .Select(p => (p.Latitude!.Value, p.Longitude!.Value))
      .ToList();

    // Query ground elevations from the Open-Meteo API
    var groundElevations = await ElevationClient.GetGroundElevationsAsync(coords, log);

    if (groundElevations == null)
    {
      log("  Elevation API unavailable. Skipping AGL analysis.");
      return null;
    }

    // Compute AGL for each sample point
    var agls = new List<double>();
    var validGroundElevs = new List<double>();
    var validDroneAlts = new List<double>();

    for (int i = 0; i < elevSample.Count && i < groundElevations.Length; i++)
    {
      double groundElev = groundElevations[i];

      // Skip NaN results and likely water (elevation near zero or negative)
      if (double.IsNaN(groundElev) || groundElev < 2)
        continue;

      double droneAlt = elevSample[i].AbsoluteAltitude!.Value;
      double agl = droneAlt - groundElev;

      // Sanity check: AGL should be positive and reasonable for a drone
      if (agl > 10 && agl < 1000)
      {
        agls.Add(agl);
        validGroundElevs.Add(groundElev);
        validDroneAlts.Add(droneAlt);
      }
    }

    if (agls.Count == 0)
    {
      log("  No valid AGL measurements computed.");
      return null;
    }

    log($"  Computed AGL for {agls.Count} points: mean {agls.Average():F1}m, " +
        $"range {agls.Min():F1}-{agls.Max():F1}m");

    return new ElevationAnalysis
    {
      SampleCount = agls.Count,
      MinAgl = agls.Min(),
      MaxAgl = agls.Max(),
      MeanAgl = agls.Average(),
      MedianAgl = MathHelpers.Median(agls),
      StdDevAgl = MathHelpers.StdDev(agls),
      MeanGroundElevation = validGroundElevs.Average(),
      MeanDroneAltitudeMsl = validDroneAlts.Average(),
    };
  }

  #endregion
}
