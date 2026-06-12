namespace DroneDatasetAnalyzer;

/// <summary>
/// DroneDatasetAnalyzer — A standalone .NET tool for analyzing DJI drone photo datasets.
///
/// Takes a directory of DJI drone photos and produces a comprehensive mission report
/// including equipment identification, flight timeline, camera settings, altitude/GSD,
/// forward and side overlap, gimbal/oblique configuration, and terrain elevation analysis.
///
/// Usage:
///   DroneDatasetAnalyzer &lt;directory&gt; [directory2 ...] [options]
///
/// Options:
///   -o, --output &lt;path&gt;          Output file path (default: stdout + MISSION-REPORT.md in first dir)
///   -s, --samples &lt;n&gt;            Samples per flight for metadata reading (default: 9)
///   -b, --overlap-block &lt;n&gt;      Consecutive photos for overlap analysis (default: 500)
///   --skip-elevation             Skip the elevation API query (faster, no AGL data)
///
/// Multiple directories are supported. All directories are searched recursively for
/// JPEG files matching the DJI naming convention. Photos from all directories are
/// merged into a single timeline and analyzed together.
///
/// Requirements:
///   - DJI drone photos with standard naming: DJI_YYYYMMDDHHMMSS_NNNN_V.jpg
///   - Photos must contain EXIF GPS data and DJI XMP metadata
///   - Internet connection for elevation API (unless --skip-elevation)
///
/// Zero external NuGet dependencies. Uses only .NET BCL for EXIF/XMP parsing,
/// HTTP client for elevation API, and System.Text.Json for JSON parsing.
/// </summary>
internal static class Program
{
  /// <summary>Application entry point.</summary>
  static async Task<int> Main(string[] args)
  {
    // Print banner
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          DJI Dataset Analyzer v1.0              ║");
    Console.WriteLine("║  Comprehensive DJI drone mission analysis tool  ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    // Parse command-line arguments
    var (directories, options) = ParseArguments(args);

    if (directories.Length == 0)
    {
      PrintUsage();
      return 1;
    }

    // Validate all directories exist
    foreach (var dir in directories)
    {
      if (!Directory.Exists(dir))
      {
        Console.Error.WriteLine($"Error: Directory not found: {dir}");
        return 1;
      }
    }

    try
    {
      var startTime = DateTime.Now;

      // Run the full analysis pipeline
      var report = await MissionAnalyzer.AnalyzeAsync(
        directories,
        options,
        log: msg => Console.WriteLine($"  {msg}"));

      var elapsed = DateTime.Now - startTime;
      Console.WriteLine();
      Console.WriteLine($"Analysis complete in {elapsed.TotalSeconds:F1}s");
      Console.WriteLine();

      // Determine output destination (default: MISSION-REPORT.md in the first directory)
      string outputPath = options.OutputPath
        ?? Path.Combine(directories[0], "MISSION-REPORT.md");

      // Write the report to file
      using (var writer = new StreamWriter(outputPath, append: false, encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
      {
        ReportWriter.WriteReport(report, writer);
      }

      Console.WriteLine($"Report written to: {outputPath}");

      // Also write a summary to stdout
      Console.WriteLine();
      WriteSummary(report);

      return 0;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine();
      Console.Error.WriteLine($"Error: {ex.Message}");

      if (ex.InnerException != null)
        Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");

      return 2;
    }
  }

  /// <summary>
  /// Parses command-line arguments into directory paths and analysis options.
  /// All non-option positional arguments are treated as input directories.
  /// </summary>
  private static (string[] Directories, AnalysisOptions Options) ParseArguments(string[] args)
  {
    var directories = new List<string>();
    var options = new AnalysisOptions();

    for (int i = 0; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "-o" or "--output" when i + 1 < args.Length:
          options.OutputPath = args[++i];
          break;

        case "-s" or "--samples" when i + 1 < args.Length:
          if (int.TryParse(args[++i], out int samples) && samples > 0)
            options.SamplesPerFlight = samples;
          break;

        case "-b" or "--overlap-block" when i + 1 < args.Length:
          if (int.TryParse(args[++i], out int block) && block > 0)
            options.OverlapBlockSize = block;
          break;

        case "--skip-elevation":
          options.SkipElevation = true;
          break;

        case "-h" or "--help":
          return ([], options);

        default:
          // Non-option arguments are directory paths
          if (!args[i].StartsWith('-'))
            directories.Add(args[i]);
          break;
      }
    }

    return ([.. directories], options);
  }

  /// <summary>
  /// Prints a concise summary to the console after analysis.
  /// Shows global stats, then per-capture-group breakdown.
  /// </summary>
  private static void WriteSummary(MissionReport report)
  {
    Console.WriteLine("═══ MISSION SUMMARY ═══");
    string platform = report.Equipment.ProductName.StartsWith("DJI", StringComparison.OrdinalIgnoreCase)
      ? report.Equipment.ProductName
      : $"DJI {report.Equipment.ProductName}";
    Console.WriteLine($"  Platform:       {platform}");
    Console.WriteLine($"  Photos:         {report.TotalPhotos:N0} across {report.CaptureDays} day(s)");
    Console.WriteLine($"  Flights:        {report.Flights.Count}");
    Console.WriteLine($"  Capture Groups: {report.CaptureGroups.Count}");

    if (report.Elevation != null)
      Console.WriteLine($"  AGL:            {report.Elevation.MeanAgl:F1} m mean ({report.Elevation.MinAgl:F1}-{report.Elevation.MaxAgl:F1} m range)");

    Console.WriteLine($"  Capture Time:   {FormatTimeSpan(report.TotalCaptureTime)}");

    // Per-group breakdown
    foreach (var group in report.CaptureGroups)
    {
      Console.WriteLine();
      Console.WriteLine($"  ── {group.Label} ({group.TotalPhotos:N0} photos, {group.Flights.Count} flight(s)) ──");

      if (group.Overlap != null)
      {
        Console.WriteLine($"     Altitude: {group.Overlap.PrimaryAltitude:F0} m  |  GSD: {group.Overlap.GsdMeters * 100:F2} cm/px");
        Console.WriteLine($"     Overlap:  {group.Overlap.ForwardOverlap * 100:F0}% forward, {group.Overlap.SideOverlap * 100:F0}% side");
      }
      else if (group.MinAltitude != null && group.MaxAltitude != null)
      {
        Console.WriteLine($"     Altitude: {group.MinAltitude:F0}–{group.MaxAltitude:F0} m range");
      }

      if (group.Gimbal.HasSmartOblique)
        Console.WriteLine($"     Gimbal:   Smart Oblique at {Math.Abs(group.Gimbal.ObliqueAngle):F0}°");
      else
        Console.WriteLine($"     Gimbal:   Fixed pitch {group.Gimbal.MeanPitch:F1}°");
    }

    Console.WriteLine();
  }

  /// <summary>Formats a TimeSpan for display.</summary>
  private static string FormatTimeSpan(TimeSpan ts)
  {
    if (ts.TotalHours >= 1)
      return $"{(int)ts.TotalHours}h {ts.Minutes}m";

    return $"{ts.Minutes}m {ts.Seconds}s";
  }

  /// <summary>Prints usage instructions.</summary>
  private static void PrintUsage()
  {
    Console.WriteLine("Usage: DroneDatasetAnalyzer <directory> [directory2 ...] [options]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <directory>                 One or more directories containing DJI drone photos.");
    Console.WriteLine("                              Each directory is searched recursively for JPEG files.");
    Console.WriteLine("                              Photos from all directories are merged and analyzed together.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -o, --output <path>         Output report path (default: MISSION-REPORT.md in first dir)");
    Console.WriteLine("  -s, --samples <n>           Samples per flight (default: 9)");
    Console.WriteLine("  -b, --overlap-block <n>     Consecutive photos for overlap (default: 500)");
    Console.WriteLine("  --skip-elevation            Skip elevation API query");
    Console.WriteLine("  -h, --help                  Show this help");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  DroneDatasetAnalyzer \"D:\\Flights\\2026-05-03_Site\\M4E\"");
    Console.WriteLine("  DroneDatasetAnalyzer \"D:\\Flight1\" \"D:\\Flight2\" \"D:\\Flight3\" -o report.md");
    Console.WriteLine("  DroneDatasetAnalyzer \"D:\\Flights\\Site\" --skip-elevation --samples 15");
  }
}
