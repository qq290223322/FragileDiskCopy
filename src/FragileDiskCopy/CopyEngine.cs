using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FragileDiskCopy;

internal enum ItemStatus
{
    Pending,
    Copying,
    Done,
    Failed,
    ScanError,
    Skipped
}

internal sealed class ManifestItem
{
    public string RelativePath { get; set; } = "";
    public int SourceIndex { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public ItemStatus Status { get; set; }
    public string Error { get; set; } = "";
    public bool Selected { get; set; } = true;
}

internal sealed record FileStatusRow(
    string RelativePath,
    long Size,
    long ProcessedBytes,
    ItemStatus Status,
    string? Error,
    long ResumeBytes = 0,
    bool Selected = true);

internal sealed record CopyProgress(
    int CompletedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    FileStatusRow? CurrentFile,
    IReadOnlyList<FileStatusRow>? FileList,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    int DiskDropCount,
    int StallCount);

internal sealed record CopyReport(
    bool Paused,
    string? PauseReason,
    int TotalFiles,
    int DoneFiles,
    int FailedFiles,
    int ScanErrors,
    int SkippedItems,
    long TotalBytes,
    int DiskDropCount,
    int StallCount);

internal sealed class StorageUnavailableException(string message) : IOException(message);

internal sealed class SourceFileMissingException(string path)
    : IOException($"源文件已突然不存在：{path}");

internal sealed class CopyEngine
{
    private sealed record SourceMount(string Root, string DestinationPrefix);

    private const string ManifestFileName = "_copy-manifest.tsv";
    private const string PartialDirectoryName = "_copy-resume";
    private const int BufferSize = 64 * 1024;

    private readonly Stopwatch _rateClock = Stopwatch.StartNew();
    private readonly object _manifestLock = new();
    private long _bytesRead;
    private int _diskDropCount;
    private int _stallCount;
    private volatile List<ManifestItem>? _manifestItems;
    private readonly SourceMount[] _sourceMounts;
    private FileTypeCategory _typeFilter = FileTypeCategory.AllFiles;

    public CopyEngine(string sourceRoot, string targetRoot, long bytesPerSecond)
        : this([sourceRoot], targetRoot, bytesPerSecond)
    {
    }

    public CopyEngine(
        IReadOnlyList<string> sourceRoots,
        string targetRoot,
        long bytesPerSecond)
    {
        var normalizedSources = sourceRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSources.Length == 0)
            throw new ArgumentException("至少需要选择一个源目录。", nameof(sourceRoots));

        SourceRoots = normalizedSources;
        SourceRoot = normalizedSources[0];
        TargetRoot = NormalizeRoot(targetRoot);

        var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
        if (storageProblem is not null)
            throw new StorageUnavailableException(storageProblem);
        foreach (var root in SourceRoots)
        {
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"源目录不存在：{root}");
            if (string.Equals(root, TargetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("源目录和目标目录不能相同。");
            if (IsWithin(TargetRoot, root) || IsWithin(root, TargetRoot))
                throw new InvalidOperationException($"源目录与目标目录不能互相包含：{root}");
        }

        for (var i = 0; i < SourceRoots.Count; i++)
        {
            for (var j = i + 1; j < SourceRoots.Count; j++)
            {
                if (IsWithin(SourceRoots[i], SourceRoots[j]) ||
                    IsWithin(SourceRoots[j], SourceRoots[i]))
                    throw new InvalidOperationException("选择的源目录不能互相包含。");
            }
        }

        _sourceMounts = SourceRoots
            .Select((root, index) => new SourceMount(
                root,
                SourceRoots.Count == 1 ? "" : GetSourcePrefix(root)))
            .ToArray();
        if (_sourceMounts
            .Where(mount => mount.DestinationPrefix.Length > 0)
            .GroupBy(mount => mount.DestinationPrefix, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("多个源文件夹名称相同，无法映射到同一目标目录，请选择名称不同的文件夹。");
        }

        Directory.CreateDirectory(TargetRoot);
        ManifestPath = Path.Combine(TargetRoot, ManifestFileName);
        PartialRoot = Path.Combine(TargetRoot, PartialDirectoryName);
        SetBytesPerSecond(bytesPerSecond);
    }

    public string SourceRoot { get; }
    public IReadOnlyList<string> SourceRoots { get; }
    public string TargetRoot { get; }
    public string ManifestPath { get; }
    public string PartialRoot { get; }
    public long BytesPerSecond { get; private set; }
    public int DiskDropCount => Volatile.Read(ref _diskDropCount);
    public int StallCount => Volatile.Read(ref _stallCount);

    public void RecordDiskDrop()
    {
        Interlocked.Increment(ref _diskDropCount);
        PersistCounters();
    }

    public void RecordStall()
    {
        Interlocked.Increment(ref _stallCount);
        PersistCounters();
    }

    public IReadOnlyList<FileStatusRow> ReadManifestFileList()
    {
        var items = LoadManifest();
        _manifestItems = items;
        return items.Select(item => ToFileStatusRow(item, item.Status == ItemStatus.Done ? item.Size : 0)).ToArray();
    }

    public IReadOnlyList<FileStatusRow> ScanFiles(CancellationToken cancellationToken)
    {
        EnsureStorageAvailable();
        var existing = File.Exists(ManifestPath)
            ? LoadManifest()
            : [];
        var existingByPath = existing.ToDictionary(
            item => item.SourceIndex + "\0" + item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var scanned = ScanSource(cancellationToken);

        foreach (var item in scanned)
        {
            if (!existingByPath.TryGetValue(
                    item.SourceIndex + "\0" + item.RelativePath,
                    out var previous) ||
                previous.Size != item.Size ||
                previous.LastWriteUtc != item.LastWriteUtc)
            {
                continue;
            }

            item.Status = previous.Status == ItemStatus.Copying
                ? ItemStatus.Pending
                : previous.Status;
            item.Error = previous.Error;
            if (item.Status == ItemStatus.Done)
            {
                var destination = GetDestinationPath(item.RelativePath);
                if (!File.Exists(destination) || new FileInfo(destination).Length != item.Size)
                {
                    item.Status = ItemStatus.Pending;
                    item.Error = "目标文件缺失或大小不一致，将重新复制。";
                }
            }
        }

        SaveManifest(scanned);
        _manifestItems = scanned;
        return scanned
            .Select(item => ToFileStatusRow(item, item.Status == ItemStatus.Done ? item.Size : 0))
            .ToArray();
    }

    public void SetBytesPerSecond(long bytesPerSecond)
    {
        if (bytesPerSecond < 1)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        BytesPerSecond = bytesPerSecond;
        _bytesRead = 0;
        _rateClock.Restart();
    }

    public void SetTypeFilter(FileTypeCategory category)
    {
        _typeFilter = category;
    }

    public void ApplySelection(IEnumerable<string> selectedPaths)
    {
        var items = _manifestItems;
        if (items is null)
            return;

        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            item.Selected = selected.Contains(item.RelativePath);
        SaveManifest(items);
    }

    public void SetItemSelected(string relativePath, bool selected)
    {
        var item = _manifestItems?.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;
        item.Selected = selected;
        SaveManifest(_manifestItems!);
    }

    public CopyReport Run(Action<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        _bytesRead = 0;
        _rateClock.Restart();

        var items = new List<ManifestItem>();
        var files = new List<ManifestItem>();
        var doneFiles = 0;
        var copiedBytes = 0L;
        var totalBytes = 0L;

        try
        {
            EnsureStorageAvailable();
            items = LoadOrBuildManifest(cancellationToken);
            _manifestItems = items;
            files = items
                .Where(item =>
                    item.Selected &&
                    item.Status is not ItemStatus.ScanError and not ItemStatus.Skipped)
                .ToList();
            totalBytes = files.Sum(item => item.Size);

            foreach (var item in files.Where(item => item.Status == ItemStatus.Done))
            {
                var destination = GetDestinationPath(item.RelativePath);
                if (File.Exists(destination) && new FileInfo(destination).Length == item.Size)
                {
                    doneFiles++;
                    copiedBytes += item.Size;
                }
                else
                {
                    item.Status = ItemStatus.Pending;
                    item.Error = "目标文件缺失或大小不一致，将重新复制。";
                }
            }

            SaveManifest(items);
            Report(includeFileList: true);
            _bytesRead = 0;
            _rateClock.Restart();

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureStorageAvailable();
                var item = files[i];
                if (item.Status == ItemStatus.Done)
                {
                    Report(item, copiedBytes, item.Size);
                    continue;
                }

                var completedBeforeFile = copiedBytes;
                try
                {
                    CopyItem(
                        item,
                        (bytesWritten, resumeBytes) =>
                        {
                            Report(item, completedBeforeFile + bytesWritten, bytesWritten, resumeBytes);
                        },
                        cancellationToken);

                    if (item.Status == ItemStatus.Done)
                    {
                        doneFiles++;
                        copiedBytes += item.Size;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SourceFileMissingException ex)
                {
                    throw new StorageUnavailableException(
                        $"源文件 {item.RelativePath} 突然不存在，按源盘掉盘处理：{OneLine(ex.Message)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
                    if (storageProblem is not null)
                        throw new StorageUnavailableException(storageProblem);

                    item.Status = ItemStatus.Failed;
                    item.Error = OneLine(ex.Message);
                }

                SaveManifest(items);
                var partialBytes = item.Status == ItemStatus.Done
                    ? item.Size
                    : TryGetPartialLength(item.RelativePath);
                Report(
                    item,
                    item.Status == ItemStatus.Done ? copiedBytes : copiedBytes + partialBytes,
                    partialBytes);
            }
        }
        catch (OperationCanceledException)
        {
            RestoreCopyingAsPending(items);
            return BuildReport(true, TrySaveManifest());
        }
        catch (StorageUnavailableException ex)
        {
            RecordDiskDrop();
            RestoreCopyingAsPending(items);
            return BuildReport(true, TrySaveManifest() ?? ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
            if (storageProblem is null)
                throw;
            RecordDiskDrop();
            RestoreCopyingAsPending(items);
            return BuildReport(true, TrySaveManifest() ?? storageProblem);
        }

        if (files.All(item => item.Status is ItemStatus.Done or ItemStatus.Skipped))
            TryDeletePartialRoot();

        return BuildReport(false, null);

        void Report(
            ManifestItem? item = null,
            long? processedBytes = null,
            long? currentFileBytes = null,
            long resumeBytes = 0,
            bool includeFileList = false)
        {
            if (progress is null)
                return;

            try
            {
                var elapsedSeconds = _rateClock.Elapsed.TotalSeconds;
                var currentSpeed = elapsedSeconds > 0 ? _bytesRead / elapsedSeconds : 0;
                var currentProcessed = Math.Clamp(processedBytes ?? copiedBytes, 0, totalBytes);
                var remainingBytes = Math.Max(0, totalBytes - currentProcessed);
                var estimatedRemaining = currentSpeed > 0 && _bytesRead > 0
                    ? TimeSpan.FromSeconds(remainingBytes / currentSpeed)
                    : (TimeSpan?)null;
                var currentFile = item is null
                    ? null
                    : ToFileStatusRow(
                        item,
                        Math.Clamp(currentFileBytes ?? (item.Status == ItemStatus.Done ? item.Size : 0), 0, item.Size),
                        resumeBytes);
                var fileList = includeFileList
                    ? items.Select(row => ToFileStatusRow(row, row.Status == ItemStatus.Done ? row.Size : 0)).ToArray()
                    : null;

                progress(new CopyProgress(
                    doneFiles,
                    files.Count,
                    currentProcessed,
                    totalBytes,
                    currentFile,
                    fileList,
                    currentSpeed,
                    estimatedRemaining,
                    DiskDropCount,
                    StallCount));
            }
            catch
            {
                // A UI callback must not stop the copy.
            }
        }

        CopyReport BuildReport(bool paused, string? pauseReason)
        {
            return new CopyReport(
                paused,
                pauseReason,
                files.Count,
                doneFiles,
                files.Count(item => item.Status == ItemStatus.Failed),
                items.Count(item => item.Status == ItemStatus.ScanError),
                items.Count(item => item.Status == ItemStatus.Skipped),
                totalBytes,
                DiskDropCount,
                StallCount);
        }

        string? TrySaveManifest()
        {
            if (items.Count == 0)
                return null;
            try
            {
                SaveManifest(items);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
                return storageProblem ?? "清单暂时无法保存：" + OneLine(ex.Message);
            }
        }
    }

    private List<ManifestItem> LoadOrBuildManifest(CancellationToken cancellationToken)
    {
        if (File.Exists(ManifestPath))
        {
            var existing = LoadManifest();
            if (!existing.Any(item => item.Status == ItemStatus.ScanError))
                return existing;
        }

        var items = ScanSource(cancellationToken);
        SaveManifest(items);
        return items;
    }

    private List<ManifestItem> ScanSource(CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        for (var sourceIndex = 0; sourceIndex < _sourceMounts.Length; sourceIndex++)
        {
            var mount = _sourceMounts[sourceIndex];
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(mount.Root);

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pendingDirectories.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
                    if (storageProblem is not null)
                        throw new StorageUnavailableException(storageProblem);

                    var relativeDirectory = ToLogicalPath(
                        mount,
                        Path.GetRelativePath(mount.Root, directory));
                    items[relativeDirectory] = new ManifestItem
                    {
                        RelativePath = relativeDirectory,
                        SourceIndex = sourceIndex,
                        Status = ItemStatus.ScanError,
                        Error = OneLine(ex.Message)
                    };
                    continue;
                }

                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = ToLogicalPath(
                        mount,
                        Path.GetRelativePath(mount.Root, entry));
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            items[relativePath] = new ManifestItem
                            {
                                RelativePath = relativePath,
                                SourceIndex = sourceIndex,
                                Status = ItemStatus.Skipped,
                                Error = "已跳过目录链接或文件链接。"
                            };
                        }
                        else if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pendingDirectories.Push(entry);
                        }
                        else
                        {
                            var info = new FileInfo(entry);
                            items[relativePath] = new ManifestItem
                            {
                                RelativePath = relativePath,
                                SourceIndex = sourceIndex,
                                Size = info.Length,
                                LastWriteUtc = info.LastWriteTimeUtc,
                                Status = ItemStatus.Pending,
                                Selected = FileTypeFilter.Matches(relativePath, _typeFilter)
                            };
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
                        if (storageProblem is not null)
                            throw new StorageUnavailableException(storageProblem);

                        items[relativePath] = new ManifestItem
                        {
                            RelativePath = relativePath,
                            SourceIndex = sourceIndex,
                            Status = ItemStatus.ScanError,
                            Error = OneLine(ex.Message)
                        };
                    }
                }
            }
        }

        return items.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void CopyItem(
        ManifestItem item,
        Action<long, long> reportPartial,
        CancellationToken cancellationToken)
    {
        var sourcePath = GetSourcePath(item);
        var destinationPath = GetDestinationPath(item.RelativePath);
        var partialPath = GetPartialPath(item.RelativePath);
        var sourceInfo = new FileInfo(sourcePath);

        if (!sourceInfo.Exists)
            throw new SourceFileMissingException(sourcePath);

        if (sourceInfo.Length != item.Size || sourceInfo.LastWriteTimeUtc != item.LastWriteUtc)
        {
            item.Size = sourceInfo.Length;
            item.LastWriteUtc = sourceInfo.LastWriteTimeUtc;
            item.Error = "";
            item.Status = ItemStatus.Pending;
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

        long finalLength;
        using (var input = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   BufferSize,
                   FileOptions.SequentialScan))
        using (var output = new FileStream(
                   partialPath,
                   FileMode.OpenOrCreate,
                   FileAccess.Write,
                   FileShare.Read,
                   BufferSize,
                   FileOptions.SequentialScan))
        {
            if (output.Length > 0 && output.Length <= input.Length)
            {
                input.Seek(output.Length, SeekOrigin.Begin);
                output.Seek(output.Length, SeekOrigin.Begin);
            }
            else
            {
                output.SetLength(0);
            }

            var resumeBytes = output.Position;
            item.Status = ItemStatus.Copying;
            reportPartial(output.Position, resumeBytes);
            var buffer = new byte[BufferSize];
            var lastReport = Stopwatch.GetTimestamp();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                output.Write(buffer, 0, read);
                Throttle(read, cancellationToken);

                if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(250))
                {
                    reportPartial(output.Position, resumeBytes);
                    lastReport = Stopwatch.GetTimestamp();
                }
            }

            output.Flush(true);
            if (output.Length != input.Length)
                throw new IOException($"复制后长度不一致：源 {input.Length} 字节，临时文件 {output.Length} 字节。");
            finalLength = output.Length;
        }

        File.Move(partialPath, destinationPath, true);
        item.Status = ItemStatus.Done;
        item.Size = finalLength;
        item.LastWriteUtc = sourceInfo.LastWriteTimeUtc;
        item.Error = "";
        reportPartial(item.Size, 0);
    }

    private void Throttle(int bytesRead, CancellationToken cancellationToken)
    {
        _bytesRead += bytesRead;
        var dueMilliseconds = _bytesRead * 1000d / BytesPerSecond;
        var waitMilliseconds = Math.Ceiling(dueMilliseconds - _rateClock.Elapsed.TotalMilliseconds);
        if (waitMilliseconds <= 0)
            return;

        cancellationToken.WaitHandle.WaitOne((int)Math.Min(waitMilliseconds, 1000));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private List<ManifestItem> LoadManifest()
    {
        var items = new List<ManifestItem>();
        foreach (var line in File.ReadLines(ManifestPath, Encoding.UTF8))
        {
            if (line.StartsWith("# 掉盘次数: ", StringComparison.Ordinal) &&
                int.TryParse(line[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var diskDrops))
                Interlocked.Exchange(ref _diskDropCount, diskDrops);
            if (line.StartsWith("# 卡顿次数: ", StringComparison.Ordinal) &&
                int.TryParse(line[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stalls))
                Interlocked.Exchange(ref _stallCount, stalls);

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("状态\t", StringComparison.Ordinal))
                continue;

            var fields = line.Split('\t');
            if (fields.Length is not (5 or 6 or 7))
                throw new InvalidDataException($"清单格式损坏，请保留并检查文件：{ManifestPath}");

            var sourceIndex = 0;
            if (fields.Length >= 6 &&
                !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceIndex))
                throw new InvalidDataException($"清单源目录编号无效：{fields[5]}");
            var item = new ManifestItem
            {
                RelativePath = fields[3],
                SourceIndex = sourceIndex,
                Error = fields[4],
                Selected = fields.Length < 7 || fields[6] != "0"
            };
            item.Status = fields[0] switch
            {
                "[ ]" => ItemStatus.Pending,
                "[>]" => ItemStatus.Pending,
                "[x]" => ItemStatus.Done,
                "[!]" => ItemStatus.Failed,
                "[E]" => ItemStatus.ScanError,
                "[-]" => ItemStatus.Skipped,
                _ => throw new InvalidDataException($"清单状态无效：{fields[0]}")
            };

            if (item.Status is not ItemStatus.ScanError and not ItemStatus.Skipped)
            {
                if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
                    !DateTime.TryParse(
                        fields[2],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var modified))
                {
                    throw new InvalidDataException($"清单数据损坏：{fields[3]}");
                }

                item.Size = size;
                item.LastWriteUtc = modified.ToUniversalTime();
            }

            if (item.SourceIndex < 0 || item.SourceIndex >= _sourceMounts.Length)
                throw new InvalidDataException($"清单中的源目录编号无效：{item.SourceIndex}");
            GetDestinationPath(item.RelativePath);
            items.Add(item);
        }

        return items.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SaveManifest(IReadOnlyCollection<ManifestItem> items)
    {
        lock (_manifestLock)
        {
            Directory.CreateDirectory(TargetRoot);
            var temporaryPath = ManifestPath + ".tmp";
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine("# 脆盘拷贝清单：保留此文件，程序才能按清单继续。");
                writer.WriteLine("# 源目录: " + OneLine(SourceRoot));
                writer.WriteLine($"# 源目录数量: {SourceRoots.Count}");
                for (var i = 0; i < SourceRoots.Count; i++)
                {
                    writer.WriteLine($"# 源目录{i + 1}: {OneLine(SourceRoots[i])}");
                    writer.WriteLine($"# 目标前缀{i + 1}: {OneLine(_sourceMounts[i].DestinationPrefix)}");
                }
                writer.WriteLine($"# 当前限速：{BytesPerSecond / 1024d / 1024d:F1} MiB/s");
                writer.WriteLine($"# 掉盘次数: {DiskDropCount}");
                writer.WriteLine($"# 卡顿次数: {StallCount}");
                writer.WriteLine("# 状态：[ ]待拷贝  [>]正在拷贝  [x]已完成  [!]失败  [E]扫描错误  [-]已跳过");
                writer.WriteLine("状态\t大小(字节)\t修改时间(UTC)\t相对路径\t错误\t源序号\t已勾选");

                foreach (var item in items.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    writer.Write(StatusText(item.Status));
                    writer.Write('\t');
                    writer.Write(item.Size.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(item.LastWriteUtc.ToString("O", CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(OneLine(item.RelativePath));
                    writer.Write('\t');
                    writer.Write(OneLine(item.Error));
                    writer.Write('\t');
                    writer.Write(item.SourceIndex.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.WriteLine(item.Selected ? "1" : "0");
                }

                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporaryPath, ManifestPath, true);
        }
    }

    private string GetDestinationPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(TargetRoot, relativePath));
        if (!IsWithin(fullPath, TargetRoot) || string.Equals(fullPath, TargetRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"清单中的路径越出目标目录：{relativePath}");
        return fullPath;
    }

    private string GetSourcePath(ManifestItem item)
    {
        if (item.SourceIndex < 0 || item.SourceIndex >= _sourceMounts.Length)
            throw new InvalidDataException($"清单中的源目录编号无效：{item.SourceIndex}");
        var mount = _sourceMounts[item.SourceIndex];
        var relativePath = RemoveDestinationPrefix(mount, item.RelativePath);
        var fullPath = Path.GetFullPath(Path.Combine(mount.Root, relativePath));
        if (!IsWithin(fullPath, mount.Root) ||
            string.Equals(fullPath, mount.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"清单中的路径越出源目录：{item.RelativePath}");
        return fullPath;
    }

    private string GetPartialPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(PartialRoot, relativePath));
        if (!IsWithin(fullPath, PartialRoot) || string.Equals(fullPath, PartialRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"清单中的临时路径越出目标目录：{relativePath}");
        return fullPath;
    }

    private static string ToLogicalPath(SourceMount mount, string sourceRelativePath)
    {
        if (mount.DestinationPrefix.Length == 0)
            return sourceRelativePath;
        return sourceRelativePath == "."
            ? mount.DestinationPrefix
            : Path.Combine(mount.DestinationPrefix, sourceRelativePath);
    }

    private static string RemoveDestinationPrefix(SourceMount mount, string logicalPath)
    {
        if (mount.DestinationPrefix.Length == 0)
            return logicalPath;

        var prefix = mount.DestinationPrefix + Path.DirectorySeparatorChar;
        if (!logicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"清单路径不属于指定的源文件夹：{logicalPath}");
        return logicalPath[prefix.Length..];
    }

    private static string GetSourcePrefix(string sourceRoot)
    {
        var trimmed = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var drive = Path.GetPathRoot(sourceRoot)?.TrimEnd(':', '\\', '/');
        return string.IsNullOrWhiteSpace(drive) ? "source" : drive + "_drive";
    }

    private long TryGetPartialLength(string relativePath)
    {
        try
        {
            var path = GetPartialPath(relativePath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void TryDeletePartialRoot()
    {
        try
        {
            if (Directory.Exists(PartialRoot))
                Directory.Delete(PartialRoot, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover empty resume folders are harmless.
        }
    }

    public static string? ReadSourceFromManifest(string manifestPath)
    {
        return ReadSourcesFromManifest(manifestPath).FirstOrDefault();
    }

    public static IReadOnlyList<string> ReadSourcesFromManifest(string manifestPath)
    {
        const string legacyPrefix = "# 源目录: ";
        const string numberedPrefix = "# 源目录";
        var numbered = new SortedDictionary<int, string>();
        string? legacy = null;

        foreach (var line in File.ReadLines(manifestPath, Encoding.UTF8))
        {
            if (line.StartsWith(legacyPrefix, StringComparison.Ordinal))
            {
                legacy = line[legacyPrefix.Length..];
                continue;
            }
            if (!line.StartsWith(numberedPrefix, StringComparison.Ordinal) ||
                line.StartsWith("# 源目录数量:", StringComparison.Ordinal))
                continue;

            var separator = line.IndexOf(':', numberedPrefix.Length);
            if (separator < 0 ||
                !int.TryParse(line[numberedPrefix.Length..separator], out var index))
                continue;

            numbered[index] = line[(separator + 1)..].Trim();
        }

        return numbered.Count > 0
            ? numbered.Values.Where(path => path.Length > 0).ToArray()
            : legacy is null ? [] : [legacy];
    }

    private void PersistCounters()
    {
        var items = _manifestItems;
        if (items is null)
            return;
        try
        {
            SaveManifest(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next normal manifest save will persist the counters.
        }
    }

    public static string? GetStorageProblem(string sourceRoot, string targetRoot)
    {
        return GetStorageProblem([sourceRoot], targetRoot);
    }

    public static string? GetStorageProblem(
        IReadOnlyList<string> sourceRoots,
        string targetRoot)
    {
        foreach (var sourceRoot in sourceRoots)
        {
            var sourceDrive = Path.GetPathRoot(Path.GetFullPath(sourceRoot)) ?? sourceRoot;
            if (!IsStorageRootReady(sourceRoot))
                return $"源盘 {sourceDrive} 已断开或未就绪。请恢复后点击“开始 / 继续”。";
        }

        var targetDrive = Path.GetPathRoot(Path.GetFullPath(targetRoot)) ?? targetRoot;
        if (!IsStorageRootReady(targetRoot))
            return $"目标盘 {targetDrive} 已断开或未就绪。请恢复后点击“开始 / 继续”。";

        return null;
    }

    private void EnsureStorageAvailable()
    {
        var storageProblem = GetStorageProblem(SourceRoots, TargetRoot);
        if (storageProblem is not null)
            throw new StorageUnavailableException(storageProblem);
    }

    private static bool IsStorageRootReady(string path)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(root))
            return false;

        if (root.Length >= 2 && root[1] == ':')
        {
            try
            {
                return new DriveInfo(root).IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        return Directory.Exists(root);
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("目录不能为空。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string StatusText(ItemStatus status)
    {
        return status switch
        {
            ItemStatus.Pending => "[ ]",
            ItemStatus.Copying => "[>]",
            ItemStatus.Done => "[x]",
            ItemStatus.Failed => "[!]",
            ItemStatus.ScanError => "[E]",
            ItemStatus.Skipped => "[-]",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }

    private static FileStatusRow ToFileStatusRow(ManifestItem item, long processedBytes, long resumeBytes = 0)
    {
        return new FileStatusRow(
            item.RelativePath,
            item.Size,
            processedBytes,
            item.Status,
            string.IsNullOrEmpty(item.Error) ? null : item.Error,
            resumeBytes,
            item.Selected);
    }

    private static void RestoreCopyingAsPending(IEnumerable<ManifestItem> items)
    {
        foreach (var item in items.Where(item => item.Status == ItemStatus.Copying))
            item.Status = ItemStatus.Pending;
    }

    private static string OneLine(string text)
    {
        return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
