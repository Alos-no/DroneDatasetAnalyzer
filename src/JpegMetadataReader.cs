using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DroneDatasetAnalyzer;

/// <summary>
/// Low-level JPEG metadata reader that extracts EXIF IFD tags and DJI XMP fields
/// from raw file bytes. Zero external dependencies — parses TIFF/IFD binary format
/// directly and extracts XMP via regex on the embedded XML text.
///
/// Only reads the first 128KB of each file for efficiency; all DJI metadata
/// (both EXIF APP1 and XMP APP1) fits within that window.
/// </summary>
public static partial class JpegMetadataReader
{
  /// <summary>Maximum bytes to read from each JPEG file.</summary>
  private const int ReadBufferSize = 128 * 1024;


  #region Public API

  /// <summary>
  /// Reads EXIF camera settings and DJI XMP metadata from a JPEG file.
  /// Populates XMP and EXIF fields on a new <see cref="PhotoMetadata"/> instance.
  /// Returns null if the file is not a valid JPEG or unreadable.
  /// </summary>
  public static PhotoMetadata? ReadFromFile(DjiFileInfo fileInfo)
  {
    byte[] buffer;
    int bytesRead;

    try
    {
      // Read only the header region; metadata is always near the start
      buffer = new byte[ReadBufferSize];
      using var fs = new FileStream(fileInfo.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
      bytesRead = fs.Read(buffer, 0, buffer.Length);
    }
    catch (IOException)
    {
      return null;
    }

    // Validate JPEG SOI marker (0xFFD8)
    if (bytesRead < 4 || buffer[0] != 0xFF || buffer[1] != 0xD8)
      return null;

    var metadata = new PhotoMetadata
    {
      FileName = fileInfo.FileName,
      FilePath = fileInfo.FilePath,
      LocalTimestamp = fileInfo.LocalTimestamp,
      SequenceNumber = fileInfo.SequenceNumber,
    };

    var span = buffer.AsSpan(0, bytesRead);

    // Parse structured EXIF data from the EXIF APP1 segment
    ParseExifFromJpeg(span, metadata);

    // Parse DJI-specific XMP fields by searching for the xmpmeta XML block
    ParseXmpFromBuffer(buffer, bytesRead, metadata);

    return metadata;
  }

  #endregion


  #region EXIF parsing (TIFF/IFD binary format)

  /// <summary>
  /// Locates the EXIF APP1 segment in the JPEG byte stream and parses its TIFF/IFD structure.
  /// Extracts GPS coordinates, altitude, and camera settings (exposure, aperture, ISO, focal length).
  /// </summary>
  private static void ParseExifFromJpeg(ReadOnlySpan<byte> jpeg, PhotoMetadata metadata)
  {
    // Scan JPEG markers for APP1 (0xFFE1) with EXIF header
    int pos = 2; // Skip SOI
    while (pos < jpeg.Length - 4)
    {
      // Each marker starts with 0xFF
      if (jpeg[pos] != 0xFF)
        break;

      byte marker = jpeg[pos + 1];
      pos += 2;

      // SOS marker (0xFFDA) means we've reached image data — stop scanning
      if (marker == 0xDA)
        break;

      // Markers without length field (standalone markers 0xD0-0xD9)
      if (marker >= 0xD0 && marker <= 0xD9)
        continue;

      // Read segment length (big-endian, includes the 2 length bytes)
      if (pos + 2 > jpeg.Length)
        break;

      int segLen = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos));
      int segStart = pos + 2;
      int segEnd = pos + segLen;

      if (segEnd > jpeg.Length)
        break;

      // APP1 (0xE1) with "Exif\0\0" header = EXIF data
      if (marker == 0xE1 && segLen > 8)
      {
        var seg = jpeg.Slice(segStart, segLen - 2);

        // Check for "Exif\0\0" signature (6 bytes)
        if (seg.Length > 6 &&
            seg[0] == (byte)'E' && seg[1] == (byte)'x' &&
            seg[2] == (byte)'i' && seg[3] == (byte)'f' &&
            seg[4] == 0 && seg[5] == 0)
        {
          // TIFF data starts after the 6-byte Exif header
          var tiff = seg.Slice(6);
          ParseTiffData(tiff, metadata);
        }
      }

      pos = segEnd;
    }
  }

  /// <summary>
  /// Parses TIFF-formatted EXIF data. Reads IFD0 to find GPS and EXIF sub-IFD pointers,
  /// then extracts fields from each sub-IFD.
  /// </summary>
  private static void ParseTiffData(ReadOnlySpan<byte> tiff, PhotoMetadata metadata)
  {
    if (tiff.Length < 8)
      return;

    // Byte order: "II" = little-endian (Intel), "MM" = big-endian (Motorola)
    bool bigEndian = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';

    // Validate TIFF magic number (42)
    ushort magic = ReadU16(tiff, 2, bigEndian);
    if (magic != 42)
      return;

    // Offset to the first IFD (IFD0)
    int ifd0Offset = (int)ReadU32(tiff, 4, bigEndian);
    if (ifd0Offset < 8 || ifd0Offset >= tiff.Length)
      return;

    // Parse IFD0 to find sub-IFD pointers
    var ifd0 = ParseIfd(tiff, ifd0Offset, bigEndian);

    // GPS sub-IFD (tag 0x8825)
    if (TryGetUInt(ifd0, 0x8825, out uint gpsOffset) && gpsOffset < tiff.Length)
    {
      var gpsIfd = ParseIfd(tiff, (int)gpsOffset, bigEndian);
      ExtractGpsData(gpsIfd, metadata);
    }

    // EXIF sub-IFD (tag 0x8769)
    if (TryGetUInt(ifd0, 0x8769, out uint exifOffset) && exifOffset < tiff.Length)
    {
      var exifIfd = ParseIfd(tiff, (int)exifOffset, bigEndian);
      ExtractCameraSettings(exifIfd, metadata);
    }
  }

  /// <summary>
  /// Parses a single IFD (Image File Directory) at the given offset.
  /// Returns a dictionary mapping tag ID → parsed value (double, double[], int, string, or uint).
  /// </summary>
  private static Dictionary<ushort, object> ParseIfd(ReadOnlySpan<byte> tiff, int offset, bool bigEndian)
  {
    var tags = new Dictionary<ushort, object>();

    if (offset + 2 > tiff.Length)
      return tags;

    ushort entryCount = ReadU16(tiff, offset, bigEndian);
    int entriesStart = offset + 2;

    for (int i = 0; i < entryCount; i++)
    {
      int entryOffset = entriesStart + i * 12;

      if (entryOffset + 12 > tiff.Length)
        break;

      ushort tag = ReadU16(tiff, entryOffset, bigEndian);
      ushort type = ReadU16(tiff, entryOffset + 2, bigEndian);
      uint count = ReadU32(tiff, entryOffset + 4, bigEndian);
      uint rawValueOrOffset = ReadU32(tiff, entryOffset + 8, bigEndian);

      // Type sizes: BYTE=1, ASCII=1, SHORT=2, LONG=4, RATIONAL=8, UNDEFINED=1, SLONG=4, SRATIONAL=8
      int typeSize = type switch
      {
        1 => 1,  // BYTE
        2 => 1,  // ASCII
        3 => 2,  // SHORT
        4 => 4,  // LONG
        5 => 8,  // RATIONAL (2 × uint32)
        7 => 1,  // UNDEFINED
        9 => 4,  // SLONG
        10 => 8, // SRATIONAL (2 × int32)
        _ => 0
      };

      if (typeSize == 0)
        continue;

      long totalSize = (long)typeSize * count;

      // If total value fits in 4 bytes, it's stored inline at entryOffset+8;
      // otherwise, rawValueOrOffset is a pointer to the value data
      int valueOffset = totalSize <= 4 ? entryOffset + 8 : (int)rawValueOrOffset;

      if (valueOffset < 0 || valueOffset + totalSize > tiff.Length)
        continue;

      // Parse the value based on type
      object? value = (type, count) switch
      {
        // Single RATIONAL → double
        (5, 1) => ReadRational(tiff, valueOffset, bigEndian),
        // Multiple RATIONALs → double[]
        (5, _) => ReadRationalArray(tiff, valueOffset, (int)count, bigEndian),
        // Single SHORT → uint (stored as uint for consistency)
        (3, 1) => (uint)ReadU16(tiff, valueOffset, bigEndian),
        // Multiple SHORTs → uint
        (3, _) => (uint)ReadU16(tiff, valueOffset, bigEndian),
        // LONG → uint
        (4, _) => ReadU32(tiff, valueOffset, bigEndian),
        // ASCII → string (trimmed of null terminator)
        (2, _) => ReadAscii(tiff, valueOffset, (int)count),
        // BYTE (single) → uint
        (1, 1) => (uint)tiff[valueOffset],
        // Default: store the raw offset/value
        _ => rawValueOrOffset
      };

      if (value is not null)
        tags[tag] = value;
    }

    return tags;
  }

  /// <summary>
  /// Extracts GPS latitude, longitude, and altitude from a parsed GPS IFD.
  /// Stores results on the metadata object as EXIF-sourced GPS coordinates.
  /// </summary>
  private static void ExtractGpsData(Dictionary<ushort, object> gps, PhotoMetadata metadata)
  {
    // Latitude: tag 2 = 3 RATIONALs (degrees, minutes, seconds), tag 1 = ref ("N"/"S")
    if (gps.TryGetValue(2, out var latVal) && latVal is double[] latDms && latDms.Length >= 3 &&
        gps.TryGetValue(1, out var latRef) && latRef is string latRefStr)
    {
      double lat = latDms[0] + latDms[1] / 60.0 + latDms[2] / 3600.0;

      if (latRefStr.StartsWith('S'))
        lat = -lat;

      // Only set if XMP didn't already provide higher-precision GPS
      metadata.Latitude ??= lat;
    }

    // Longitude: tag 4 = 3 RATIONALs, tag 3 = ref ("E"/"W")
    if (gps.TryGetValue(4, out var lonVal) && lonVal is double[] lonDms && lonDms.Length >= 3 &&
        gps.TryGetValue(3, out var lonRef) && lonRef is string lonRefStr)
    {
      double lon = lonDms[0] + lonDms[1] / 60.0 + lonDms[2] / 3600.0;

      if (lonRefStr.StartsWith('W'))
        lon = -lon;

      metadata.Longitude ??= lon;
    }

    // Altitude: tag 6 = RATIONAL, tag 5 = ref (0=above sea level, 1=below)
    if (gps.TryGetValue(6, out var altVal) && altVal is double alt)
    {
      if (TryGetUInt(gps, 5, out uint altRef) && altRef == 1)
        alt = -alt;

      // EXIF altitude matches DJI AbsoluteAltitude (both MSL/EGM96)
      metadata.AbsoluteAltitude ??= alt;
    }
  }

  /// <summary>
  /// Extracts camera exposure settings from a parsed EXIF IFD.
  /// Tags: ExposureTime (0x829A), FNumber (0x829D), ISO (0x8827),
  /// FocalLength (0x920A), FocalLength35mm (0xA405), PixelDimensions (0xA002/A003).
  /// </summary>
  private static void ExtractCameraSettings(Dictionary<ushort, object> exif, PhotoMetadata metadata)
  {
    // Exposure time (RATIONAL): e.g., 1/2000 = 0.0005
    if (exif.TryGetValue(0x829A, out var expVal) && expVal is double exp)
      metadata.ExposureTime = exp;

    // F-number (RATIONAL): e.g., 2.8
    if (exif.TryGetValue(0x829D, out var fnVal) && fnVal is double fn)
      metadata.FNumber = fn;

    // ISO speed (SHORT)
    if (TryGetUInt(exif, 0x8827, out uint iso))
      metadata.Iso = (int)iso;

    // Focal length in mm (RATIONAL)
    if (exif.TryGetValue(0x920A, out var flVal) && flVal is double fl)
      metadata.FocalLengthMm = fl;

    // 35mm equivalent focal length (SHORT)
    if (TryGetUInt(exif, 0xA405, out uint fl35))
      metadata.FocalLength35mm = (int)fl35;

    // Image width (LONG or SHORT, tag 0xA002)
    if (TryGetUInt(exif, 0xA002, out uint w))
      metadata.ImageWidth = (int)w;

    // Image height (LONG or SHORT, tag 0xA003)
    if (TryGetUInt(exif, 0xA003, out uint h))
      metadata.ImageHeight = (int)h;
  }

  #endregion


  #region XMP parsing (DJI drone-dji namespace)

  /// <summary>
  /// Regex pattern matching drone-dji:FieldName="value" attribute pairs in the XMP XML text.
  /// Captures the field name (group 1) and the full quoted value including spaces (group 2).
  /// </summary>
  [GeneratedRegex(@"drone-dji:(\w+)=""([^""]*?)""", RegexOptions.Compiled)]
  private static partial Regex DjiXmpFieldPattern();

  /// <summary>
  /// Searches the raw file buffer for the XMP metadata block (delimited by &lt;x:xmpmeta&gt;)
  /// and extracts DJI-specific fields via regex matching.
  /// </summary>
  private static void ParseXmpFromBuffer(byte[] buffer, int length, PhotoMetadata metadata)
  {
    // Find the XMP region by searching for the <x:xmpmeta marker in raw bytes.
    // XMP is always UTF-8 encoded within the JPEG APP1 segment.
    int xmpStart = FindByteSequence(buffer, length, "<x:xmpmeta"u8);

    if (xmpStart < 0)
      return;

    int xmpEnd = FindByteSequence(buffer, length, "</x:xmpmeta>"u8);

    if (xmpEnd <= xmpStart)
      xmpEnd = length; // Fallback: use rest of buffer if closing tag not found

    int xmpLength = Math.Min(xmpEnd - xmpStart + 13, length - xmpStart);

    // Decode the XMP region as UTF-8 text
    string xmpText = Encoding.UTF8.GetString(buffer, xmpStart, xmpLength);

    // Extract all drone-dji:FieldName="value" pairs
    foreach (Match match in DjiXmpFieldPattern().Matches(xmpText))
    {
      string fieldName = match.Groups[1].Value;
      string fieldValue = match.Groups[2].Value;

      PopulateDjiField(metadata, fieldName, fieldValue);
    }
  }

  /// <summary>
  /// Maps a DJI XMP field name to the corresponding <see cref="PhotoMetadata"/> property.
  /// </summary>
  private static void PopulateDjiField(PhotoMetadata metadata, string fieldName, string value)
  {
    switch (fieldName)
    {
      case "RelativeAltitude":
        if (TryParseDouble(value, out double relAlt))
          metadata.RelativeAltitude = relAlt;
        break;

      case "AbsoluteAltitude":
        if (TryParseDouble(value, out double absAlt))
          metadata.AbsoluteAltitude = absAlt;
        break;

      case "GpsLatitude":
        if (TryParseDouble(value, out double lat))
          metadata.Latitude = lat; // XMP decimal degrees overrides EXIF DMS (higher precision)
        break;

      case "GpsLongitude":
        if (TryParseDouble(value, out double lon))
          metadata.Longitude = lon;
        break;

      case "GimbalPitchDegree":
        if (TryParseDouble(value, out double pitch))
          metadata.GimbalPitch = pitch;
        break;

      case "GimbalYawDegree":
        if (TryParseDouble(value, out double yaw))
          metadata.GimbalYaw = yaw;
        break;

      case "GimbalRollDegree":
        if (TryParseDouble(value, out double roll))
          metadata.GimbalRoll = roll;
        break;

      case "FlightYawDegree":
        if (TryParseDouble(value, out double fYaw))
          metadata.FlightYaw = fYaw;
        break;

      case "FlightXSpeed":
        if (TryParseDouble(value, out double xs))
          metadata.FlightXSpeed = xs;
        break;

      case "FlightYSpeed":
        if (TryParseDouble(value, out double ys))
          metadata.FlightYSpeed = ys;
        break;

      case "SensorTemperature":
        if (TryParseDouble(value, out double temp))
          metadata.SensorTemperature = temp;
        break;

      case "WhiteBalanceCCT":
        if (int.TryParse(value, out int cct))
          metadata.WhiteBalanceCct = cct;
        break;

      case "RtkFlag":
        if (int.TryParse(value, out int rtk))
          metadata.RtkFlag = rtk;
        break;

      case "LRFTargetDistance":
        if (TryParseDouble(value, out double lrf))
          metadata.LrfTargetDistance = lrf;
        break;

      case "CalibratedFocalLength":
        if (TryParseDouble(value, out double cfl))
          metadata.CalibratedFocalLength = cfl;
        break;

      case "ShutterType":
        metadata.ShutterType = value;
        break;

      case "ProductName":
        metadata.ProductName = value;
        break;

      case "DroneSerialNumber":
        metadata.DroneSerialNumber = value;
        break;

      case "CameraSerialNumber":
        metadata.CameraSerialNumber = value;
        break;

      case "ImageSource":
        metadata.ImageSource = value;
        break;

      case "ShutterCount":
        if (int.TryParse(value, out int sc))
          metadata.ShutterCount = sc;
        break;

      case "UTCAtExposure":
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
              DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
          metadata.UtcAtExposure = utc;
        break;
    }
  }

  #endregion


  #region Binary helpers

  /// <summary>Reads a 16-bit unsigned integer with the specified byte order.</summary>
  private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
    bigEndian
      ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset))
      : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));

  /// <summary>Reads a 32-bit unsigned integer with the specified byte order.</summary>
  private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
    bigEndian
      ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset))
      : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));

  /// <summary>
  /// Reads a TIFF RATIONAL value (two uint32: numerator/denominator) as a double.
  /// Returns 0.0 if the denominator is zero.
  /// </summary>
  private static double ReadRational(ReadOnlySpan<byte> data, int offset, bool bigEndian)
  {
    uint numerator = ReadU32(data, offset, bigEndian);
    uint denominator = ReadU32(data, offset + 4, bigEndian);

    return denominator == 0 ? 0.0 : (double)numerator / denominator;
  }

  /// <summary>Reads an array of RATIONAL values.</summary>
  private static double[] ReadRationalArray(ReadOnlySpan<byte> data, int offset, int count, bool bigEndian)
  {
    var result = new double[count];

    for (int i = 0; i < count; i++)
      result[i] = ReadRational(data, offset + i * 8, bigEndian);

    return result;
  }

  /// <summary>Reads a null-terminated ASCII string from the TIFF data.</summary>
  private static string ReadAscii(ReadOnlySpan<byte> data, int offset, int count)
  {
    // ASCII values include a null terminator; trim it
    int length = Math.Min(count, data.Length - offset);
    var slice = data.Slice(offset, length);

    // Find null terminator
    int nullIdx = slice.IndexOf((byte)0);

    if (nullIdx >= 0)
      slice = slice.Slice(0, nullIdx);

    return Encoding.ASCII.GetString(slice);
  }

  /// <summary>
  /// Attempts to read a uint32 from a tag value (handles both SHORT and LONG storage).
  /// </summary>
  private static bool TryGetUInt(Dictionary<ushort, object> tags, ushort tag, out uint value)
  {
    if (tags.TryGetValue(tag, out var obj) && obj is uint u)
    {
      value = u;
      return true;
    }

    value = 0;
    return false;
  }

  /// <summary>
  /// Searches for a byte sequence within a buffer. Returns the starting index, or -1 if not found.
  /// </summary>
  private static int FindByteSequence(byte[] buffer, int length, ReadOnlySpan<byte> pattern)
  {
    int limit = length - pattern.Length;

    for (int i = 0; i <= limit; i++)
    {
      if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
        return i;
    }

    return -1;
  }

  /// <summary>Parses a double from a string using invariant culture.</summary>
  private static bool TryParseDouble(string value, out double result) =>
    double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign,
      CultureInfo.InvariantCulture, out result);

  #endregion
}
