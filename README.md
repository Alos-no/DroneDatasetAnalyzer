# DroneDatasetAnalyzer

A zero-dependency .NET CLI tool that analyzes DJI drone photo datasets. Extracts flight timelines, camera settings, overlap geometry, gimbal configuration, altitude/GSD, and terrain elevation from EXIF + XMP metadata.

Takes one or more directories of photos and produces a comprehensive mission report in seconds — no image processing, no external libraries, just raw metadata parsing.

## Features

- **Capture Group Classification** — Automatically groups flights by altitude band (stable-altitude flights clustered, varying-altitude flights in a catch-all group) with per-group analysis
- **Flight Timeline** — Segments photos into flights by detecting power cycles (timestamp gaps + DJI sequence resets)
- **Camera Settings** — Shutter speed, aperture, ISO, focal length per flight
- **Forward & Side Overlap** — Computed from consecutive GPS positions and flight-line geometry, per capture group
- **Smart Oblique Analysis** — Correlates gimbal pitch, roll, and yaw to determine the real oblique angle setting
- **GSD & Footprint** — Ground sample distance and footprint from DJI calibrated focal length
- **Terrain Elevation** — Queries Open-Meteo SRTM API for ground elevation and above-ground-level computation
- **Equipment ID** — Drone model, serial numbers, sensor specs from XMP metadata
- **Per-Flight Breakdown** — Day-by-day tables with photo counts, durations, camera parameters, and group labels
- **Multi-Directory Support** — Merge photos from multiple folders (common when flights are split across subfolders)
- **Recursive Search** — Automatically finds all JPEG files in subdirectories

## Quick Start

```bash
# Clone and build
git clone https://github.com/Alos-no/DroneDatasetAnalyzer.git
cd DroneDatasetAnalyzer
dotnet build src/DroneDatasetAnalyzer.csproj

# Analyze a dataset (searches recursively)
dotnet run --project src/DroneDatasetAnalyzer.csproj -- "/path/to/drone/photos"
```

Or download a pre-built release from the [Releases](https://github.com/Alos-no/DroneDatasetAnalyzer/releases) page:

```bash
DroneDatasetAnalyzer "/path/to/drone/photos"
```

## Usage

```
DroneDatasetAnalyzer <directory> [directory2 ...] [options]

Arguments:
  <directory>                 One or more directories containing DJI drone photos.
                              Each directory is searched recursively for JPEG files.
                              Photos from all directories are merged and analyzed together.

Options:
  -o, --output <path>         Output report path (default: MISSION-REPORT.md in first dir)
  -s, --samples <n>           Samples per flight for metadata (default: 9)
  -b, --overlap-block <n>     Consecutive photos for overlap (default: 500)
  --skip-elevation            Skip elevation API query (faster, no AGL)
  -h, --help                  Show help
```

### Examples

```bash
# Analyze a dataset with photos in subfolders (common DJI folder structure)
DroneDatasetAnalyzer "D:\Flights\2026-05-03_Site\M4E"

# Analyze multiple flight directories together
DroneDatasetAnalyzer "D:\Flight_001" "D:\Flight_002" "D:\Flight_003" -o report.md

# Custom output path, more samples per flight
DroneDatasetAnalyzer "D:\Flights\Flight_001" -o report.md --samples 15

# Skip elevation API (no internet needed)
DroneDatasetAnalyzer "D:\Flights\Flight_001" --skip-elevation
```

## Sample Output

```
═══ MISSION SUMMARY ═══
  Platform:       DJI Matrice 4E
  Photos:         5,391 across 1 day(s)
  Flights:        5
  Capture Groups: 4
  Capture Time:   1h 58m

  ── Capture at 105 m (1,883 photos, 1 flight(s)) ──
     Altitude: 105 m  |  GSD: 2.82 cm/px
     Overlap:  77% forward, 79% side
     Gimbal:   Smart Oblique at 50°

  ── Capture at 90 m (1,698 photos, 1 flight(s)) ──
     Altitude: 90 m  |  GSD: 2.42 cm/px
     Overlap:  73% forward, 75% side
     Gimbal:   Smart Oblique at 50°

  ── Capture at 40 m (398 photos, 1 flight(s)) ──
     Altitude: 40 m  |  GSD: 1.07 cm/px
     Overlap:  0% forward, 0% side
     Gimbal:   Fixed pitch -21.1°

  ── Varying altitude (1,412 photos, 2 flight(s)) ──
     Altitude: 30–83 m range
     Gimbal:   Fixed pitch -18.1°
```

The tool generates a full Markdown report with dynamic sections: Equipment, Location, Mission Overview (with capture group summary table), Elevation & Terrain (global), one section per capture group (with full Altitude/GSD, Overlap, and Gimbal detail), Flight Details (per-day tables with group labels), Camera Summary, and Methodology Notes.

## How It Works

The analysis pipeline is designed for speed — it reads as few files as possible:

1. **Directory Scanning** (0 file reads) — Recursively finds all JPEG files across input directories
2. **Filename Parsing** (0 file reads) — Extracts timestamps and sequence numbers from DJI's `DJI_YYYYMMDDHHMMSS_NNNN_V.jpg` naming convention
3. **Flight Segmentation** (0 file reads) — Splits at >30s gaps, merges brief pauses unless the sequence counter resets (= power cycle)
4. **Metadata Sampling** (~45 file reads) — Reads EXIF + XMP from 9 evenly-spaced photos per flight
5. **Capture Group Classification** (0 file reads) — Groups flights by altitude band using sampled metadata
6. **Per-Group Overlap Block** (~500 file reads per classified group) — Reads consecutive photos for overlap computation within each altitude band
7. **Elevation API** (~10 HTTP requests) — Queries SRTM ground elevation for AGL computation

Total: ~1,500+ file reads for a 5,000-photo multi-group dataset. Analysis completes in ~30 seconds over a network share, or a few seconds on local SSD.

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
