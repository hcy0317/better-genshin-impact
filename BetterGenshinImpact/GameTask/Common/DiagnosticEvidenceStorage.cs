using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed class DiagnosticEvidenceBudgetException() : IOException("run-disk-budget");

// 单writer拥有；所有目录遍历、编码和文件IO只在后台运行，不占用感知线程。
internal sealed class DiagnosticEvidenceStorage : IDisposable
{
    internal const long DefaultMaximumBytes = 10L * 1024 * 1024 * 1024;
    private readonly string _root, _directory;
    private readonly Guid _run;
    private readonly long _maximumBytes, _summaryReserve, _terminalReserve;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _diagnostic;
    private readonly Action<int, string>? _onEvicted;
    private FileStream? _lease;
    private Exception? _initializationError;
    private bool _initialized;
    private volatile bool _budgetExhausted;
    private long _usedBytes;
    private sealed class SharedImage(string file, long bytes)
    {
        internal readonly string File = file;
        internal readonly long Bytes = bytes;
        internal int References;
    }
    private readonly Dictionary<string, SharedImage> _images = new(StringComparer.Ordinal);
    private sealed record StoredEvidence(int Sequence, string Request, string Phase, string ImageKey, long MetadataBytes, DiagnosticEvidencePriority Priority);
    private readonly List<StoredEvidence> _stored = [];
    private readonly List<object> _evicted = [];
    private int _evictedCount;
    internal bool BudgetExhausted => _budgetExhausted;

    internal static bool IsTerminalPhase(string phase) => phase is "terminal" or "combat-terminal";

    internal DiagnosticEvidenceStorage(string root, Guid run, long maximumBytes = DefaultMaximumBytes,
        TimeProvider? clock = null, Action<string>? diagnostic = null, Action<int, string>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        _root = Path.GetFullPath(root);
        _run = run;
        _directory = Path.Combine(_root, run.ToString("N"));
        _maximumBytes = maximumBytes;
        _summaryReserve = Math.Min(1024 * 1024, maximumBytes / 4);
        _terminalReserve = Math.Min(64L * 1024 * 1024, maximumBytes / 4);
        _clock = clock ?? TimeProvider.System;
        _diagnostic = diagnostic;
        _onEvicted = onEvicted;
    }

    internal void Initialize()
    {
        if (_initialized)
        {
            if (_initializationError != null) throw new IOException("evidence-storage-unavailable", _initializationError);
            return;
        }
        _initialized = true;
        try
        {
            EnsureNoLinks(_root);
            Directory.CreateDirectory(_root);
            EnsureNoLinks(_root);
            CleanupExpiredRuns();
            if (Directory.Exists(_directory)) throw new IOException("Evidence run directory already exists");
            Directory.CreateDirectory(_directory);
            _lease = new FileStream(Path.Combine(_directory, ".active"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var assembly = typeof(DiagnosticEvidenceScope).Assembly;
            var meta = JsonBytes(new { SchemaVersion = 2, RunId = _run, StartedAt = _clock.GetUtcNow(), MaximumBytes = _maximumBytes,
                Build = new { Version = assembly.GetName().Version?.ToString(), ModuleVersionId = assembly.ManifestModule.ModuleVersionId,
                    SourceRevision = "unknown:not-embedded-in-build", Branch = "unknown:not-embedded-in-build" } });
            if (meta.LongLength > _maximumBytes - _summaryReserve) throw new DiagnosticEvidenceBudgetException();
            using var file = new FileStream(Path.Combine(_directory, "run.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _usedBytes += meta.LongLength;
            file.Write(meta);
        }
        catch (Exception error)
        {
            _initializationError = error;
            Dispose();
            throw;
        }
    }

    internal async Task WriteAsync(DiagnosticEvidence evidence, Mat image)
    {
        Initialize();
        var priority = IsTerminalPhase(evidence.Phase) ? DiagnosticEvidencePriority.Error : evidence.Priority;
        if (evidence.Window != null && evidence.Phase != "selection-before-submit" && priority == DiagnosticEvidencePriority.Routine)
            priority = DiagnosticEvidencePriority.Warning;
        var terminal = priority > DiagnosticEvidencePriority.Routine;
        if (_budgetExhausted && !terminal) throw new DiagnosticEvidenceBudgetException();
        if (evidence.RunId != _run || evidence.Sequence <= 0) throw new IOException("Evidence run identity mismatch");
        if (!Cv2.ImEncode(".png", image, out var png)) throw new IOException("PNG encoding failed");
        // 同源的裁剪/修改图不能误合并；编码与内容摘要仅在后台writer计算。
        var key = $"{evidence.Source.SessionId:N}/{evidence.Source.Sequence}/{Convert.ToHexString(SHA256.HashData(png))}";
        _images.TryGetValue(key, out var shared);
        var imageFile = shared?.File ?? $"evidence-{evidence.Sequence:D4}.png";
        var document = JObject.FromObject(evidence);
        document["SchemaVersion"] = 2;
        document["ImageFile"] = imageFile;
        document["WrittenAt"] = JToken.FromObject(_clock.GetUtcNow());
        var metadata = JsonBytes(document);
        var bytes = checked((shared == null ? png.LongLength : 0) + metadata.LongLength);
        // Warning可使用故障预算，但仍给Error留独立空间；总10GiB硬限不变。
        var reserve = priority == DiagnosticEvidencePriority.Error ? 0 : terminal ? _terminalReserve / 2 : _terminalReserve;
        var available = _maximumBytes - _summaryReserve - _usedBytes - reserve;
        if (terminal && bytes > available)
        {
            ReclaimLowerPriority(bytes - available, key, priority);
            available = _maximumBytes - _summaryReserve - _usedBytes - reserve;
        }
        if (bytes > available)
        {
            _budgetExhausted = true;
            throw new DiagnosticEvidenceBudgetException();
        }
        // 失败保守占账；残留无法删除也不能通过退账突破硬上限。
        _usedBytes += bytes;
        var name = Path.Combine(_directory, $"evidence-{evidence.Sequence:D4}");
        var imageCreated = false;
        var metadataCreated = false;
        try
        {
            if (shared == null)
                await WriteNewAsync(Path.Combine(_directory, imageFile), png, () => imageCreated = true).ConfigureAwait(false);
            await WriteNewAsync(name + ".json", metadata, () => metadataCreated = true).ConfigureAwait(false);
            if (shared == null)
            {
                // Scope本身最多8192张；独立调用Storage也不能令索引无限增长。
                if (_images.Count < 8192) _images[key] = shared = new(imageFile, png.LongLength);
            }
            if (shared != null) shared.References++;
            if (_stored.Count < 8192 && shared != null)
                _stored.Add(new(evidence.Sequence, evidence.Request, evidence.Phase, key, metadata.LongLength, priority));
        }
        catch
        {
            if (imageCreated) TryDelete(name + ".png");
            if (metadataCreated) TryDelete(name + ".json");
            throw;
        }
    }

    internal async Task CompleteAsync(object summary)
    {
        Initialize();
        var document = JObject.FromObject(summary);
        document["Retention"] = JToken.FromObject(new { EvictedCount = _evictedCount,
            Evicted = _evicted, EvictionDetailsTruncated = _evictedCount > _evicted.Count });
        var bytes = JsonBytes(document);
        if (bytes.LongLength > _summaryReserve || bytes.LongLength > _maximumBytes - _usedBytes)
            throw new DiagnosticEvidenceBudgetException();
        _usedBytes += bytes.LongLength;
        await WriteNewAsync(Path.Combine(_directory, "summary.json"), bytes).ConfigureAwait(false);
    }

    private void ReclaimLowerPriority(long required, string incomingImageKey, DiagnosticEvidencePriority incomingPriority)
    {
        var reclaimed = 0L;
        foreach (var entry in _stored.Where(item => item.Priority < incomingPriority).OrderBy(item => item.Priority).ToArray())
        {
            var path = Path.Combine(_directory, $"evidence-{entry.Sequence:D4}.json");
            try { File.Delete(path); }
            catch { continue; } // 无法删除不能退账，也不能删除仍被其引用的图像。
            _usedBytes -= entry.MetadataBytes;
            reclaimed += entry.MetadataBytes;
            _stored.Remove(entry);
            var image = _images[entry.ImageKey];
            // 即将复用的PNG暂时固定，允许回收同源JSON；准入成功后增加引用。
            if (--image.References == 0 && entry.ImageKey != incomingImageKey)
            {
                try
                {
                    File.Delete(Path.Combine(_directory, image.File));
                    _usedBytes -= image.Bytes;
                    reclaimed += image.Bytes;
                    _images.Remove(entry.ImageKey);
                }
                catch { /* 残留图像仍占预算，下一次同帧可以复用。 */ }
            }
            _evictedCount++;
            var reason = entry.Priority == DiagnosticEvidencePriority.Routine ? "routine-reclaimed-for-fault" : "warning-reclaimed-for-error";
            if (_evicted.Count < 128) _evicted.Add(new { entry.Sequence, entry.Request, entry.Phase, Reason = reason });
            Report($"EVIDENCE_EVICTED sequence={entry.Sequence} reason={reason}");
            try { _onEvicted?.Invoke(entry.Sequence, reason); } catch { /* 诊断回调不得影响存储回收。 */ }
            if (reclaimed >= required) break;
        }
    }

    private static byte[] JsonBytes(object value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented));

    private static async Task WriteNewAsync(string path, byte[] bytes, Action? created = null)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
        created?.Invoke();
        await file.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static void EnsureNoLinks(string path)
    {
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Evidence paths cannot contain links");
    }

    private static readonly Regex EvidenceFile = new(@"^evidence-[0-9]+\.(png|json)$", RegexOptions.CultureInvariant);
    private static FileInfo[]? RecognizedFiles(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || !Guid.TryParseExact(directory.Name, "N", out _)) return null;
        var entries = directory.GetFileSystemInfos();
        if (entries.Any(entry => entry is not FileInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !(entry.Name is ".active" or "run.json" or "summary.json" || EvidenceFile.IsMatch(entry.Name)))) return null;
        var files = entries.Cast<FileInfo>().ToArray();
        return files.Any(file => file.Name == "run.json" || EvidenceFile.IsMatch(file.Name)) ? files : null;
    }

    private void CleanupExpiredRuns()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-3);
        var candidates = new List<(DirectoryInfo Directory, DateTime Latest)>();
        foreach (var directory in new DirectoryInfo(_root).EnumerateDirectories())
        {
            try
            {
                var files = RecognizedFiles(directory);
                if (files == null)
                {
                    Report("EVIDENCE_CLEANUP_SKIPPED run=" + directory.Name + " reason=unrecognized-or-linked");
                    continue;
                }
                // 最晚mtime保守保护旧格式和仍有近期写入的run，不相信文件名时间。
                var latest = files.Max(file => file.LastWriteTimeUtc);
                if (latest < cutoff) candidates.Add((directory, latest));
            }
            catch (Exception error) { Report("EVIDENCE_CLEANUP_SKIPPED " + error.GetType().Name); }
        }
        foreach (var (directory, _) in candidates.OrderBy(candidate => candidate.Latest))
        {
            try
            {
                // 精确根的直接子目录；不递归删除，不跟随链接，不删除未知文件。
                if (!string.Equals(directory.Parent?.FullName, _root, StringComparison.OrdinalIgnoreCase)) continue;
                EnsureNoLinks(directory.FullName);
                var active = Path.Combine(directory.FullName, ".active");
                using (var lease = new FileStream(active, File.Exists(active) ? FileMode.Open : FileMode.CreateNew,
                           FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    var files = RecognizedFiles(directory);
                    if (files == null || files.Where(file => file.Name != ".active").Any(file => file.LastWriteTimeUtc >= cutoff)) continue;
                    foreach (var file in files.Where(file => file.Name != ".active")) file.Delete();
                }
                directory.Delete(recursive: false);
                Report("EVIDENCE_CLEANUP_REMOVED run=" + directory.Name);
            }
            catch (Exception error) { Report("EVIDENCE_CLEANUP_SKIPPED run=" + directory.Name + " reason=" + error.GetType().Name); }
        }
    }

    private void Report(string message) { try { _diagnostic?.Invoke(message); } catch { } }
    public void Dispose() { _lease?.Dispose(); _lease = null; }
}
