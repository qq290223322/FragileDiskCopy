# FragileDiskCopy

[中文](README.md) | English

A Windows utility for rate-limited, resumable copying from unstable or failing disks. The source disk is treated as the priority: reads are sequential, bandwidth is throttled, incomplete file data is retained, and hazardous copy behavior stops automatically when the source or destination disappears.

> This is a data-salvage copy tool, not a data-recovery or bad-sector repair application.

[Download the latest release](https://github.com/qq290223322/FragileDiskCopy/releases/latest)

## Features

### Protected and resumable copying

- Starts every copy session at a protective **0.5 MiB/s** speed.
- Reads source data sequentially in 64 KiB chunks with continuous software throttling.
- Uses a per-file partial area in `_copy-resume`; an interrupted file continues from its existing byte offset.
- If the source file size or modification time changes, its old partial data is discarded and the file is copied again.
- Fixed mode ramps from 0.5 MiB/s to the configured base speed and then stays there.
- Dynamic mode can continue above the base speed up to a user-defined maximum.

### Failing-disk protection

- Checks source and destination volumes every second for availability.
- Automatically pauses when a source or destination volume disappears.
- Detects a previously listed source file suddenly disappearing and treats it as a possible drive loss.
- Records a stall after **5 seconds without new bytes**, without stopping the task.
- While stalled, the current-file progress bar turns red and the speed text turns red at 0 B/s.
- During a continuous stall, dynamic speed is reduced by 10% every 10 seconds, with a 0.5 MiB/s floor.
- Disk-drop and stall counts are persisted in the TSV manifest.

### Multiple source folders

- “选择 A” opens a multi-folder manager where folders can be added and removed.
- With one source folder, its contents are copied directly into the destination.
- With multiple source folders, each source is copied into a destination subfolder with the same name:

```text
Destination\
  SourceFolderA\
  SourceFolderB\
```

- Source folders with identical names are rejected to prevent collisions.
- Multiple sources share one manifest, overall progress, disk-drop statistics and resume state.

### Scanning, type filters and checkboxes

- Adding or removing source folders does not scan automatically.
- The **Scan files** button builds or refreshes the manifest and file list without starting a copy.
- Starting a task without an existing manifest scans first and then copies.
- The file-type selector supports:
  - All files
  - Images
  - Videos
  - Documents
  - Images + videos
  - Images + videos + documents
- Image, video and document groups include common JPG, PNG, TIFF, camera RAW, MP4, MOV, MKV, PDF, Office and text formats.
- Files that do not match the selected type are excluded from the actual copy scope.
- Every file row has a checkbox, checked by default. Only files in the current type selection that remain checked are copied.
- Checkbox state is stored in the manifest and survives pause, close and resume.
- Type selection and row checkboxes are locked after a copy task starts.

### File list and task resume

- The interface supports live switching between Chinese and English. Chinese system locales default to Chinese; other locales default to English, and a manual choice is saved.
- The file list is a custom-painted virtual list; only visible rows are rendered.
- It shows status, relative path, size, exact processed bytes and errors.
- “Follow file” is enabled by default: only unfinished files are shown and the current file is kept in view.
- The file being copied is shown in green as “正在拷贝” (copying).
- The **Continue task (TSV)** button loads an existing `_copy-manifest.tsv`, including all source folders, file states and progress.
- Loading a TSV does not start copying; click **Start / Continue** afterward.

## Usage

1. Click **选择 A** and add one or more source folders.
2. Choose the destination folder B.
3. Set the base limit, dynamic maximum and growth interval.
4. Optionally click **Scan files**, then choose a file type and clear individual checkboxes.
5. Click **Start / Continue**.
6. After pausing, closing or reconnecting a disk, load the same task and click **Start / Continue**.

Paths are locked after a task starts. Speed settings can still be changed while paused.

## Manifest and partial data

The destination contains:

- `_copy-manifest.tsv`: human-readable task state.
- `_copy-resume/`: partially copied file data.

Do not delete either one if you need to resume.

Common status values:

| Status | Meaning |
|---|---|
| `[ ]` | Pending |
| `[>]` | Copying; restored to pending when paused |
| `[x]` | Completed |
| `[!]` | Failed |
| `[E]` | Scan error |
| `[-]` | Skipped |

A file is marked complete only after its partial file is flushed, closed, length-checked and moved to the final path.

## Speed control

### Fixed mode

- Starts at 0.5 MiB/s.
- Grows every 10 seconds without a stall.
- Each step is 10% of the configured base speed.
- Stops at the base speed.

### Dynamic mode

- Starts at 0.5 MiB/s.
- Growth interval defaults to 10 seconds and has a minimum of 10 seconds.
- Each step is 10% of the base speed.
- Stops at the user-defined maximum.
- The maximum is not capped at 20 MiB/s, but it must be strictly greater than the base speed.
- Continuous stall reduces the current dynamic limit by 10% every 10 seconds, never below 0.5 MiB/s.

The limit covers data read by this process. Other programs, search indexing and antivirus scans can still access the source disk.

## Build

Requirements:

- Windows 10/11 x64
- .NET 8 SDK or newer

```powershell
dotnet publish .\src\FragileDiskCopy\FragileDiskCopy.csproj `
  -c Release `
  -o .\src\FragileDiskCopy\release
```

The project is configured for self-contained, compressed, single-file `win-x64` publishing.

Run the built-in self-test:

```powershell
.\src\FragileDiskCopy\release\FragileDiskCopy.exe --self-test
```

The result is written to `%TEMP%\FragileDiskCopySelfTest.last.txt`.

## Repository layout

```text
.
├─ .github/workflows/release.yml
├─ FragileDiskCopy.sln
├─ README.md
├─ README.en.md
└─ src/FragileDiskCopy/
```

Pushing a `v*` tag builds a versioned Windows executable and publishes it automatically to GitHub Releases.

## Limitations

- Does not read SMART health, temperature, power-delivery status, reallocated-sector counts or CRC errors.
- Cannot distinguish an intentionally deleted file from a temporarily invisible file on a disconnected volume; both cause a safe pause.
- Completed files are checked by existence and length, not by a full hash, to avoid rereading the failing disk.
- File-type selection and row checkboxes define the actual copy scope.
- Source folders with identical names cannot be mapped into the same destination.
- The application cannot prevent other processes from accessing the source disk.

## Data safety

- Start at 0.5–1 MiB/s when the source disk is unstable.
- Avoid disk scans, photo indexing, antivirus full scans and other heavy source-disk activity during the salvage copy.
- Use a healthy destination disk with enough free space.
- After recovery, make another backup on a healthy disk if the data is important.
