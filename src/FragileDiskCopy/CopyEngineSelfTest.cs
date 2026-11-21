using System.Security.Cryptography;
using System.Text;

namespace FragileDiskCopy;

internal static class CopyEngineSelfTest
{
    public static int Run()
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "FragileDiskCopySelfTest.last.txt");
        var testRoot = Path.Combine(Path.GetTempPath(), "FragileDiskCopySelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(testRoot, "source");
            var target = Path.Combine(testRoot, "target");
            Directory.CreateDirectory(Path.Combine(source, "nested"));

            var bigFile = Path.Combine(source, "big.bin");
            var bigBytes = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(bigBytes);
            File.WriteAllBytes(bigFile, bigBytes);
            File.WriteAllText(Path.Combine(source, "nested", "small.txt"), "resume-check", Encoding.UTF8);

            var firstEngine = new CopyEngine(source, target, 512 * 1024);
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                firstEngine.Run(null, cancellation.Token);
            }

            var partialFiles = Directory.Exists(firstEngine.PartialRoot)
                ? Directory.GetFiles(firstEngine.PartialRoot, "*", SearchOption.AllDirectories)
                : [];
            Assert(partialFiles.Length > 0, "Cancellation did not leave a resumable partial file.");
            Assert(new FileInfo(partialFiles[0]).Length < bigBytes.Length, "Partial file unexpectedly complete.");

            var secondEngine = new CopyEngine(source, target, 20 * 1024 * 1024);
            var failures = new List<string>();
            var sawFileList = false;
            var sawFileProgress = false;
            var sawResumedFile = false;
            var sawCopyingStatus = false;
            var sawEstimatedTime = false;
            var report = secondEngine.Run(
                progress =>
                {
                    sawFileList |= progress.FileList?.Count == 2;
                    sawFileProgress |= progress.CurrentFile is { ProcessedBytes: > 0 };
                    sawResumedFile |= progress.CurrentFile is { ResumeBytes: > 0, ProcessedBytes: > 0 };
                    sawCopyingStatus |= progress.CurrentFile?.Status == ItemStatus.Copying;
                    sawEstimatedTime |= progress.EstimatedRemaining is not null;
                    if (progress.CurrentFile is { Error: not null } current)
                        failures.Add($"{current.RelativePath}: {current.Error}");
                },
                CancellationToken.None);
            Assert(!report.Paused, "Resume unexpectedly paused.");
            Assert(
                report.FailedFiles == 0,
                "Resume reported a failed file.\r\n" +
                string.Join("\r\n", failures) + "\r\n" +
                File.ReadAllText(secondEngine.ManifestPath));
            Assert(report.DoneFiles == 2, "Resume did not complete both files.");
            Assert(sawFileList, "Progress did not expose the complete file list.");
            Assert(sawFileProgress, "Progress did not expose per-file progress.");
            Assert(sawResumedFile, "Progress did not identify the existing bytes of a resumed file.");
            Assert(sawCopyingStatus, "Progress did not expose the copying status.");
            Assert(sawEstimatedTime, "Progress did not expose an estimated remaining time.");

            Assert(
                SHA256.HashData(File.ReadAllBytes(bigFile))
                    .SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "big.bin")))),
                "Resumed file content does not match the source.");
            Assert(
                File.ReadAllText(Path.Combine(target, "nested", "small.txt"), Encoding.UTF8) == "resume-check",
                "Nested file content does not match.");
            Assert(
                File.ReadLines(secondEngine.ManifestPath).Count(line => line.StartsWith("[x]")) == 2,
                "Manifest does not contain two checked files.");
            Assert(
                string.Equals(
                    CopyEngine.ReadSourceFromManifest(secondEngine.ManifestPath),
                    source,
                    StringComparison.OrdinalIgnoreCase),
                "Manifest does not record the source directory.");
            var previewRows = secondEngine.ReadManifestFileList();
            Assert(previewRows.Count == 2, "Manifest preview did not return both files.");
            Assert(
                previewRows.All(row => row.Status == ItemStatus.Done && row.ProcessedBytes == row.Size),
                "Manifest preview did not mark completed files correctly.");
            var throttleSource = Path.Combine(testRoot, "throttle-source");
            var throttleTarget = Path.Combine(testRoot, "throttle-target");
            Directory.CreateDirectory(throttleSource);
            File.WriteAllBytes(Path.Combine(throttleSource, "rate.bin"), new byte[2 * 1024 * 1024]);
            var throttleEngine = new CopyEngine(throttleSource, throttleTarget, 1024 * 1024);
            var maxObservedSpeed = 0d;
            var throttleClock = System.Diagnostics.Stopwatch.StartNew();
            var throttleReport = throttleEngine.Run(
                progress => maxObservedSpeed = Math.Max(maxObservedSpeed, progress.BytesPerSecond),
                CancellationToken.None);
            throttleClock.Stop();
            Assert(!throttleReport.Paused, "Throttle test unexpectedly paused.");
            Assert(throttleClock.Elapsed >= TimeSpan.FromSeconds(1.8), "1 MiB/s throttle copied 2 MiB too quickly.");
            Assert(maxObservedSpeed <= 1.25 * 1024 * 1024, "Observed speed exceeded the configured throttle.");

            var speedPolicy = new DynamicSpeedPolicy(1024 * 1024, 4 * 1024 * 1024, 10);
            for (var i = 0; i < 9; i++)
                Assert(!speedPolicy.RecordStableSecond(out _), "Dynamic speed increased too early.");
            Assert(
                speedPolicy.RecordStableSecond(out var raisedLimit),
                "Dynamic speed did not increase after the configured interval.");
            Assert(
                raisedLimit > DynamicSpeedPolicy.StartingBytesPerSecond &&
                raisedLimit <= 4 * 1024 * 1024,
                "Dynamic speed exceeded its maximum.");
            for (var i = 0; i < 190; i++)
                speedPolicy.RecordStableSecond(out _);
            Assert(
                Math.Abs(speedPolicy.CurrentLimit - 2.6 * 1024 * 1024) <= 128 * 1024,
                "Dynamic speed did not increase by the expected amount.");
            var reduced = speedPolicy.ReduceAfterStall();
            Assert(
                Math.Abs(reduced.Next - reduced.Previous * 9 / 10) <= 1024,
                "A stall did not reduce dynamic speed by 10 percent.");
            for (var i = 0;
                 i < 100 && speedPolicy.CurrentLimit > DynamicSpeedPolicy.StartingBytesPerSecond;
                 i++)
                speedPolicy.ReduceAfterStall();
            Assert(
                speedPolicy.CurrentLimit == DynamicSpeedPolicy.StartingBytesPerSecond,
                "A stall reduced dynamic speed below 0.5 MiB/s.");

            var fixedRamp = new DynamicSpeedPolicy(3 * 1024 * 1024, 3 * 1024 * 1024, 10);
            Assert(
                fixedRamp.CurrentLimit == DynamicSpeedPolicy.StartingBytesPerSecond,
                "Fixed mode did not start at the protective 0.5 MiB/s speed.");
            for (var i = 0; i < 100; i++)
                fixedRamp.RecordStableSecond(out _);
            Assert(
                fixedRamp.CurrentLimit == 3 * 1024 * 1024,
                "Fixed mode protection did not stop at the configured base speed.");

            var stallTracker = new StallEscalationTracker();
            Assert(!stallTracker.RecordStall(4), "A stall was recorded before 5 quiet seconds.");
            Assert(stallTracker.RecordStall(5), "A stall was not recorded at 5 quiet seconds.");
            Assert(!stallTracker.RecordStall(6), "The same stall was counted twice.");
            Assert(!stallTracker.ShouldReduce(9), "Dynamic speed reduced before 10 quiet seconds.");
            Assert(stallTracker.ShouldReduce(10), "Dynamic speed did not reduce after 10 quiet seconds.");
            Assert(!stallTracker.ShouldReduce(19), "Dynamic speed reduced before the next 10-second interval.");
            Assert(stallTracker.ShouldReduce(20), "Dynamic speed did not reduce again after 20 quiet seconds.");
            stallTracker.Reset();
            Assert(!stallTracker.RecordStall(4), "Stall tracking did not reset after progress resumed.");
            Assert(stallTracker.RecordStall(5), "A new stall was not recorded after progress resumed.");

            var scanSource = Path.Combine(testRoot, "scan-source");
            var scanTarget = Path.Combine(testRoot, "scan-target");
            Directory.CreateDirectory(scanSource);
            File.WriteAllText(Path.Combine(scanSource, "photo.jpg"), "image");
            File.WriteAllText(Path.Combine(scanSource, "notes.txt"), "document");
            var scanEngine = new CopyEngine(scanSource, scanTarget, 1024 * 1024);
            scanEngine.SetTypeFilter(FileTypeCategory.Images);
            var scannedFiles = scanEngine.ScanFiles(CancellationToken.None);
            Assert(scannedFiles.Count == 2, "Standalone scan did not list all files.");
            Assert(
                scannedFiles.Single(file => file.RelativePath == "photo.jpg").Selected &&
                !scannedFiles.Single(file => file.RelativePath == "notes.txt").Selected,
                "File type filtering did not set the copy selection.");
            Assert(
                !File.Exists(Path.Combine(scanTarget, "photo.jpg")) &&
                !File.Exists(Path.Combine(scanTarget, "notes.txt")),
                "Standalone scan copied files instead of only building the manifest.");
            var filteredCopy = scanEngine.Run(null, CancellationToken.None);
            Assert(filteredCopy.DoneFiles == 1, "Filtered copy selected the wrong number of files.");
            Assert(
                File.Exists(Path.Combine(scanTarget, "photo.jpg")) &&
                !File.Exists(Path.Combine(scanTarget, "notes.txt")),
                "File type filtering did not limit the actual copy scope.");

            var missingSource = Path.Combine(testRoot, "missing-source");
            var missingTarget = Path.Combine(testRoot, "missing-target");
            Directory.CreateDirectory(missingSource);
            var disappearingFile = Path.Combine(missingSource, "disappear.bin");
            File.WriteAllBytes(disappearingFile, [1, 2, 3]);
            var manifestEngine = new CopyEngine(missingSource, missingTarget, 1024 * 1024);
            using (var stopBeforeCopy = new CancellationTokenSource())
            {
                manifestEngine.Run(
                    progress =>
                    {
                        if (progress.FileList is not null)
                            stopBeforeCopy.Cancel();
                    },
                    stopBeforeCopy.Token);
            }
            File.Delete(disappearingFile);
            var missingEngine = new CopyEngine(missingSource, missingTarget, 1024 * 1024);
            var missingReport = missingEngine.Run(null, CancellationToken.None);
            Assert(missingReport.Paused, "A source file disappearing did not pause the task.");
            Assert(missingReport.DiskDropCount == 1, "A source file disappearing was not counted as a disk drop.");
            Assert(
                File.ReadLines(missingEngine.ManifestPath).Any(line => line.StartsWith("[ ]\t3\t")),
                "The missing source file was marked failed instead of remaining pending.");

            var multiRoot = Path.Combine(testRoot, "multi");
            var sourceOne = Path.Combine(multiRoot, "source-one");
            var sourceTwo = Path.Combine(multiRoot, "source-two");
            var multiTarget = Path.Combine(multiRoot, "target");
            Directory.CreateDirectory(sourceOne);
            Directory.CreateDirectory(sourceTwo);
            File.WriteAllText(Path.Combine(sourceOne, "one.txt"), "one");
            File.WriteAllText(Path.Combine(sourceTwo, "two.txt"), "two");
            var multiEngine = new CopyEngine([sourceOne, sourceTwo], multiTarget, 20 * 1024 * 1024);
            var multiReport = multiEngine.Run(null, CancellationToken.None);
            Assert(multiReport.DoneFiles == 2, "Multiple source folders were not copied.");
            var oneDestination = Path.Combine(multiTarget, "source-one", "one.txt");
            var twoDestination = Path.Combine(multiTarget, "source-two", "two.txt");
            Assert(File.ReadAllText(oneDestination) == "one", "The first source folder mapped incorrectly.");
            Assert(File.ReadAllText(twoDestination) == "two", "The second source folder mapped incorrectly.");
            Assert(
                CopyEngine.ReadSourcesFromManifest(multiEngine.ManifestPath)
                    .SequenceEqual([sourceOne, sourceTwo], StringComparer.OrdinalIgnoreCase),
                "The manifest did not preserve all source folders.");
            File.Delete(oneDestination);
            var multiResume = new CopyEngine([sourceOne, sourceTwo], multiTarget, 20 * 1024 * 1024);
            var multiResumeReport = multiResume.Run(null, CancellationToken.None);
            Assert(multiResumeReport.DoneFiles == 2, "Multiple-source resume failed.");
            Assert(File.ReadAllText(oneDestination) == "one", "Resumed multi-source file came from the wrong source.");
            Assert(
                !Directory.Exists(secondEngine.PartialRoot) ||
                !Directory.EnumerateFiles(secondEngine.PartialRoot, "*", SearchOption.AllDirectories).Any(),
                "Completed copy left temporary files.");

            secondEngine.RecordDiskDrop();
            secondEngine.RecordStall();
            var reopened = new CopyEngine(source, target, 20 * 1024 * 1024);
            var reopenedReport = reopened.Run(null, CancellationToken.None);
            Assert(reopenedReport.DiskDropCount == 1, "Disk-drop counter was not persisted.");
            Assert(reopenedReport.StallCount == 1, "Stall counter was not persisted.");

            using (var fileView = new VirtualFileListView())
            {
                var manyRows = Enumerable.Range(0, 3000)
                    .Select(i => new FileStatusRow($"folder\\file-{i}.bin", 1024, 0, ItemStatus.Pending, null))
                    .ToArray();
                fileView.SetInitialRows(manyRows);
                Assert(fileView.RowCount == 3000 && fileView.TotalRowCount == 3000, "Large file list was not loaded.");
                fileView.UpdateRow(manyRows[0] with { Status = ItemStatus.Done, ProcessedBytes = 1024 });
                Assert(fileView.RowCount == 3000, "Initial list order should not change before a run.");
                fileView.SetRunningMode(followCurrent: true);
                Assert(fileView.RowCount == 2999, "Follow mode did not hide the completed file.");
                fileView.SetRunningMode(followCurrent: false);
                Assert(fileView.RowCount == 3000, "Full list mode did not restore all files.");
                Assert(
                    fileView.AccessibleRole == AccessibleRole.List &&
                    fileView.AccessibleDescription == "当前显示 3000 项，共 3000 项，已完成 1 项",
                    "File list did not expose a compact accessibility summary.");
                fileView.SetInitialRows(
                [
                    new FileStatusRow("photo.JPG", 1, 0, ItemStatus.Pending, null),
                    new FileStatusRow("movie.MP4", 1, 0, ItemStatus.Pending, null),
                    new FileStatusRow("document.PDF", 1, 0, ItemStatus.Pending, null)
                ]);
                fileView.SetFileTypeFilter(FileTypeCategory.Images);
                Assert(fileView.RowCount == 1, "Image file filter did not match its formats.");
                fileView.SetFileTypeFilter(FileTypeCategory.Videos);
                Assert(fileView.RowCount == 1, "Video file filter did not match its formats.");
                fileView.SetFileTypeFilter(FileTypeCategory.Documents);
                Assert(fileView.RowCount == 1, "Document file filter did not match its formats.");
                fileView.SetSelected("photo.JPG", false);
                Assert(
                    fileView.GetSelectedPaths(FileTypeCategory.Images).Count == 0,
                    "Clearing a row checkbox did not remove it from the copy selection.");
                fileView.SetSelected("photo.JPG", true);
                Assert(
                    fileView.GetSelectedPaths(FileTypeCategory.Images).Count == 1,
                    "Checking a row did not restore it to the copy selection.");
            }

            using (var stalledBar = new PercentageProgressBar())
            {
                stalledBar.Value = 50;
                stalledBar.Stalled = true;
                Assert(stalledBar.Stalled, "Stalled progress-bar state was not retained.");
            }

            File.WriteAllText(
                resultPath,
                "PASS\r\n" +
                $"2 MiB at 1 MiB/s took {throttleClock.Elapsed.TotalSeconds:F2}s; " +
                $"max observed {maxObservedSpeed / 1024d / 1024d:F2} MiB/s.\r\n" +
                "Manifest, throttle, 3000-row virtual list, progress data, ETA, interruption, in-file resume, counters and final content checks passed.\r\n");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "FAIL\r\n" + ex + "\r\n");
            return 1;
        }
        finally
        {
            try
            {
                if (Path.GetFullPath(testRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(testRoot, true);
            }
            catch
            {
                // Unique test folders are safe to leave behind if a file handle is still closing.
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
