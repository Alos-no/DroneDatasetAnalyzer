using System.Globalization;
using System.Text.Json;

namespace DroneDatasetAnalyzer;

/// <summary>
/// Queries the Open-Meteo Elevation API for SRTM-based ground elevation data.
/// Returns elevations in EGM96 geoid datum (meters above mean sea level),
/// which matches DJI's AbsoluteAltitude reference frame.
///
/// API docs: https://open-meteo.com/en/docs/elevation-api
/// Rate limit: ~10,000 requests/day (free tier), batched up to 100 coords per request.
/// </summary>
public static class ElevationClient
{
  /// <summary>Maximum number of coordinates per API request.</summary>
  private const int BatchSize = 20;

  /// <summary>Delay between batched requests to respect rate limits (milliseconds).</summary>
  private const int RateLimitDelayMs = 300;

  /// <summary>Reusable HTTP client with generous timeout for slow connections.</summary>
  private static readonly HttpClient Http = new()
  {
    Timeout = TimeSpan.FromSeconds(30),
  };


  /// <summary>
  /// Queries ground elevation for a list of GPS coordinates.
  /// Returns an array of elevation values (meters above MSL, EGM96 datum) matching
  /// the input coordinates by index. Returns NaN for coordinates that failed.
  /// </summary>
  /// <param name="coordinates">GPS coordinates (latitude, longitude) to query.</param>
  /// <param name="log">Progress callback.</param>
  /// <returns>Array of ground elevations in meters, or null if the API is completely unavailable.</returns>
  public static async Task<double[]?> GetGroundElevationsAsync(
    IReadOnlyList<(double Latitude, double Longitude)> coordinates,
    Action<string> log)
  {
    if (coordinates.Count == 0)
      return [];

    var results = new double[coordinates.Count];

    // Initialize all results to NaN (failed/unknown)
    Array.Fill(results, double.NaN);

    int totalBatches = (coordinates.Count + BatchSize - 1) / BatchSize;
    int completedBatches = 0;
    bool anySuccess = false;

    for (int i = 0; i < coordinates.Count; i += BatchSize)
    {
      int batchEnd = Math.Min(i + BatchSize, coordinates.Count);
      var batch = coordinates.Skip(i).Take(batchEnd - i).ToList();

      try
      {
        var elevations = await QueryBatchAsync(batch);

        if (elevations != null)
        {
          for (int j = 0; j < elevations.Length && i + j < results.Length; j++)
            results[i + j] = elevations[j];

          anySuccess = true;
        }
      }
      catch (Exception ex)
      {
        log($"  Elevation API batch {completedBatches + 1}/{totalBatches} failed: {ex.Message}");
      }

      completedBatches++;

      // Rate-limit between batches (skip delay after the last batch)
      if (i + BatchSize < coordinates.Count)
        await Task.Delay(RateLimitDelayMs);
    }

    return anySuccess ? results : null;
  }


  /// <summary>
  /// Sends a single batched elevation request to the Open-Meteo API.
  /// </summary>
  private static async Task<double[]?> QueryBatchAsync(
    IReadOnlyList<(double Latitude, double Longitude)> batch)
  {
    // Build comma-separated coordinate lists for the query string
    string lats = string.Join(",",
      batch.Select(c => c.Latitude.ToString("F6", CultureInfo.InvariantCulture)));
    string lons = string.Join(",",
      batch.Select(c => c.Longitude.ToString("F6", CultureInfo.InvariantCulture)));

    string url = $"https://api.open-meteo.com/v1/elevation?latitude={lats}&longitude={lons}";

    var response = await Http.GetAsync(url);
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);

    // Response format: { "elevation": [12.3, 45.6, ...] }
    if (doc.RootElement.TryGetProperty("elevation", out var elevArray))
    {
      return elevArray.EnumerateArray()
        .Select(e => e.GetDouble())
        .ToArray();
    }

    return null;
  }
}
