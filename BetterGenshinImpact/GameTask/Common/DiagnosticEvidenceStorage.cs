using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed class DiagnosticEvidenceBudgetException() : IOException("run-disk-budget");

// 单writer拥有；所有目录遍历、编码和文件IO只在后台运行，不占用感知线程。
internal sealed class DiagnosticEvidenceStorage : IDisposable
{
    internal const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _root, _directory;
    private readonly Guid _run;
    private readonly long _maximumBytes, _summaryReserve, _terminalReserve;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _diagnostic;
    private FileStream? _lease;
    private Exception? _initializationError;
    private bool _initialized;
    private volatile bool _budgetExhausted;
    private long _usedBytes;
    internal bool BudgetExhausted => _budgetExhausted;

    internal static bool IsTerminalPhase(string phase) => phase is "terminal" or "combat-terminal";

    internal DiagnosticEvidenceStorage(string root, Guid run, long maximumBytes = DefaultMaximumBytes,
        TimeProvider? clock = null, Action<string>? diagnostic = null)
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
            var meta = JsonBytes(new { RunId = _run, StartedAt = _clock.GetUtcNow(), MaximumBytes = _maximumBytes });
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
        var terminal = IsTerminalPhase(evidence.Phase);
        if (_budgetExhausted && !terminal) throw new DiagnosticEvidenceBudgetException();
        if (evidence.RunId != _run || evidence.Sequence <= 0) throw new IOException("Evidence run identity mismatch");
        if (!Cv2.ImEncode(".png", image, out var png)) throw new IOException("PNG encoding failed");
        var metadata = JsonBytes(evidence);
        var bytes = checked(png.LongLength + metadata.LongLength);
        var available = _maximumBytes - _summaryReserve - _usedBytes - (terminal ? 0 : _terminalReserve);
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
            await WriteNewAsync(name + ".png", png, () => imageCreated = true).ConfigureAwait(false);
            await WriteNewAsync(name + ".json", metadata, () => metadataCreated = true).ConfigureAwait(false);
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
        var bytes = JsonBytes(summary);
        if (bytes.LongLength > _summaryReserve || bytes.LongLength > _maximumBytes - _usedBytes)
            throw new DiagnosticEvidenceBudgetException();
        _usedBytes += bytes.LongLength;
        await WriteNewAsync(Path.Combine(_directory, "summary.json"), bytes).ConfigureAwait(false);
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
