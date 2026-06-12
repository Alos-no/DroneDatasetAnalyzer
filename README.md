# DroneDatasetAnalyzer

A zero-dependency .NET CLI tool that analyzes DJI drone photo datasets. Extracts flight timelines, camera settings, overlap geometry, gimbal configuration, altitude/GSD, and terrain elevation from EXIF + XMP metadata.

Takes a directory of thousands of photos and produces a comprehensive mission report in seconds — no image processing, no external libraries, just raw metadata parsing.

## Features

- **Flight Timeline** — Segments photos into flights by detecting battery swaps (timestamp gaps + DJI sequence resets)
- **Camera Settings** — Shutter speed, aperture, ISO, focal length per flight
- **Forward & Side Overlap** — Computed from consecutive GPS positions and flight-line geometry
- **Smart Oblique Analysis** — Correlates gimbal pitch, roll, and yaw to determine the real oblique angle setting
- **GSD & Footprint** — Ground sample distance and footprint from DJI calibrated focal length
- **Terrain Elevation** — Queries Open-Meteo SRTM API for ground elevation and above-ground-level computation
- **Equipment ID** — Drone model, serial numbers, sensor specs from XMP metadata
- **Per-Flight Breakdown** — Day-by-day tables with photo counts, durations, and camera parameters

## Quick Start

```bash
# Clone and build
git clone https://github.com/Alos-no/DroneDatasetAnalyzer.git
cd DroneDatasetAnalyzer
dotnet build src/DroneDatasetAnalyzer.csproj

# Analyze a dataset
dotnet run --project src/DroneDatasetAnalyzer.csproj -- "/path/to/drone/photos"
```

Or download a pre-built release from the [Releases](https://github.com/Alos-no/DroneDatasetAnalyzer/releases) page:

```bash
DroneDatasetAnalyzer "/path/to/drone/photos"
```

## Usage

```
DroneDatasetAnalyzer <directory> [options]

Arguments:
  <directory>                 Path to directory containing DJI drone photos

Options:
  -o, --output <path>         Output report path (default: MISSION-REPORT.md in dataset dir)
  -s, --samples <n>           Samples per flight for metadata (default: 9)
  -b, --overlap-block <n>     Consecutive photos for overlap (default: 500)
  --skip-elevation            Skip elevation API query (faster, no AGL)
  -h, --help                  Show help
```

### Examples

```bash
# Basic analysis (writes MISSION-REPORT.md in the dataset directory)
DroneDatasetAnalyzer "D:\Flights\Mission_001"

# Custom output path, more samples per flight
DroneDatasetAnalyzer "D:\Flights\Mission_001" -o report.md --samples 15

# Skip elevation API (no internet needed)
DroneDatasetAnalyzer "D:\Flights\Mission_001" --skip-elevation
```

## Sample Output

```
═══ MISSION SUMMARY ═══
  Platform:     DJI Matrice 4E
  Photos:       20,184 across 3 day(s)
  Flights:      20 (17 battery swaps)
  Altitude:     110 m (relative)
  GSD:          2.95 cm/px
  Overlap:      78% forward, 75% side
  Gimbal:       Smart Oblique at 45°
  Capture Time: 4h 59m
```

The tool also generates a full Markdown report with 9 sections: Equipment, Location, Mission Overview, Altitude & GSD, Overlap & Coverage, Gimbal Configuration, Per-Day Flight Tables (with camera settings), Camera Summary, and Methodology Notes.

## How It Works

The analysis pipeline is designed for speed — it reads as few files as possible:

1. **Filename Parsing** (0 file reads) — Extracts timestamps and sequence numbers from DJI's `DJI_YYYYMMDDHHMMSS_NNNN_V.jpg` naming convention
2. **Flight Segmentation** (0 file reads) — Splits at >30s gaps, merges brief pauses unless the sequence counter resets (= power cycle)
3. **Metadata Sampling** (~180 file reads) — Reads EXIF + XMP from 9 evenly-spaced photos per flight
4. **Overlap Block** (~500 file reads) — Reads a consecutive block for precise forward/side overlap
5. **Elevation API** (~40 HTTP requests) — Queries SRTM ground elevation for AGL computation

Total: ~680 file reads for a 20,000-photo dataset. Analysis completes in 10–15 seconds on a local SSD, or 30–60 seconds over a network share.

### Metadata Sources

| Source | Data Extracted |
|--------|----------------|
| **DJI Filename** | Capture timestamp, sequence number |
| **EXIF IFD** | GPS coordinates, altitude (MSL), exposure, aperture, ISO, focal length, image dimensions |
| **DJI XMP** | Relative altitude, gimbal pitch/yaw/roll, flight yaw, ground speed, calibrated focal length, RTK status, sensor temperature, device serial numbers |

EXIF is parsed from raw TIFF/IFD binary format. XMP is extracted via regex on the embedded XML. No external image libraries required.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building from source)
- Or any of the pre-built releases (self-contained, no SDK needed)
- DJI drone photos with standard naming convention and EXIF/XMP metadata
- Internet connection for elevation API (optional — use `--skip-elevation` to skip)

### Supported Drones

Tested with:
- DJI Matrice 4E (M4E)

Should work with any DJI drone that uses the standard `DJI_YYYYMMDDHHMMSS_NNNN_V.jpg` filename convention and `drone-dji:*` XMP namespace (Mavic, Phantom, Matrice, Mini series, etc.).

## Building

```bash
# Debug build
dotnet build src/DroneDatasetAnalyzer.csproj

# Release build
dotnet build src/DroneDatasetAnalyzer.csproj -c Release

# Self-contained single-file executable
dotnet publish src/DroneDatasetAnalyzer.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/
```

## License

Apache License 2.0 — see [LICENSE](LICENSE).

## About

Built by [Alos](https://alos.no) — a Norwegian drone services company specializing in imaging, mapping, inspection, and reality capture.
