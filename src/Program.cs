namespace DroneDatasetAnalyzer;

/// <summary>
/// DroneDatasetAnalyzer — A standalone .NET tool for analyzing DJI drone photo datasets.
///
/// Takes a directory of DJI drone photos and produces a comprehensive mission report
/// including equipment identification, flight timeline, camera settings, altitude/GSD,
/// forward and side overlap, gimbal/oblique configuration, and terrain elevation analysis.
///
/// Usage:
///   DroneDatasetAnalyzer &lt;directory&gt; [options]
///
/// Options:
///   -o, --output &lt;path&gt;          Output file path (default: stdout + MISSION-REPORT.md in dataset dir)
///   -s, --samples &lt;n&gt;            Samples per flight for metadata reading (default: 9)
///   -b, --overlap-block &lt;n&gt;      Consecutive photos for overlap analysis (default: 200)
///   --skip-elevation             Skip the elevation API query (faster, no AGL data)
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
    var (directory, options) = ParseArguments(args);

    if (directory == null)
    {
      PrintUsage();
      return 1;
    }

    // Validate the directory exists and contains files
    if (!Directory.Exists(directory))
    {
      Console.Error.WriteLine($"Error: Directory not found: {directory}");
      return 1;
    }

    try
    {
      var startTime = DateTime.Now;

      // Run the full analysis pipeline
      var report = await MissionAnalyzer.AnalyzeAsync(
        directory,
        options,
        log: msg => Console.WriteLine($"  {msg}"));

      var elapsed = DateTime.Now - startTime;
      Console.WriteLine();
      Console.WriteLine($"Analysis complete in {elapsed.TotalSeconds:F1}s");
      Console.WriteLine();

      // Determine output destination
      string outputPath = options.OutputPath
        ?? Path.Combine(directory, "MISSION-REPORT.md");

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
  /// Parses command-line arguments into a directory path and analysis options.
  /// </summary>
  private static (string? Directory, AnalysisOptions Options) ParseArguments(string[] args)
  {
    string? directory = null;
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
          return (null, options);

        default:
          // First non-option argument is the directory path
          if (!args[i].StartsWith('-'))
            directory = args[i];
          break;
      }
    }

    return (directory, options);
  }

  /// <summary>
  /// Prints a concise summary to the console after analysis.
  /// </summary>
  private static void WriteSummary(MissionReport report)
  {
    Console.WriteLine("═══ MISSION SUMMARY ═══");
    string platform = report.Equipment.ProductName.StartsWith("DJI", StringComparison.OrdinalIgnoreCase)
      ? report.Equipment.ProductName
      : $"DJI {report.Equipment.ProductName}";
    Console.WriteLine($"  Platform:     {platform}");
    Console.WriteLine($"  Photos:       {report.TotalPhotos:N0} across {report.CaptureDays} day(s)");
    Console.WriteLine($"  Flights:      {report.Flights.Count} ({report.BatterySwaps} battery swaps)");
    Console.WriteLine($"  Altitude:     {report.Overlap.PrimaryAltitude:F0} m (relative)");
    Console.WriteLine($"  GSD:          {report.Overlap.GsdMeters * 100:F2} cm/px");
    Console.WriteLine($"  Overlap:      {report.Overlap.ForwardOverlap * 100:F0}% forward, {report.Overlap.SideOverlap * 100:F0}% side");
    Console.WriteLine($"  Gimbal:       Smart Oblique at {Math.Abs(report.Gimbal.ObliqueAngle):F0}°");

    if (report.Elevation != null)
      Console.WriteLine($"  AGL:          {report.Elevation.MeanAgl:F1} m mean ({report.Elevation.MinAgl:F1}-{report.Elevation.MaxAgl:F1} m range)");

    Console.WriteLine($"  Capture Time: {FormatTimeSpan(report.TotalCaptureTime)}");
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
    Console.WriteLine("Usage: DroneDatasetAnalyzer <directory> [options]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <directory>                 Path to directory containing DJI drone photos");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -o, --output <path>         Output report path (default: MISSION-REPORT.md in dataset dir)");
    Console.WriteLine("  -s, --samples <n>           Samples per flight (default: 9)");
    Console.WriteLine("  -b, --overlap-block <n>     Consecutive photos for overlap (default: 200)");
    Console.WriteLine("  --skip-elevation            Skip elevation API query");
    Console.WriteLine("  -h, --help                  Show this help");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  DroneDatasetAnalyzer \"N:\\Datasets\\DJI_Mission_001\"");
    Console.WriteLine("  DroneDatasetAnalyzer \"C:\\Photos\" -o report.md --samples 15");
    Console.WriteLine("  DroneDatasetAnalyzer \"D:\\Flight\" --skip-elevation");
  }
}
