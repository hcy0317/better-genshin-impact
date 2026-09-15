using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BetterGenshinImpact.Helpers;

/// <summary>同步文件事务：按路径跨会话互斥，持有者不得跨await；只在完整写入后替换正式文件。</summary>
internal static class FileMutationGate
{
    internal static IDisposable Enter(string path)
    {
        var identity = Path.GetFullPath(path).ToUpperInvariant();
        var mutex = new Mutex(false, "Global\\BetterGI.FileMutation." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("文件仍被其他运行持有，拒绝覆盖");
            }
            catch (AbandonedMutexException) { /* 已取得互斥，仍须读取原文件验证内容。 */ }
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool CompareExchange(string path, string? expected, string content)
    {
        using var transaction = Enter(path);
        string? current;
        try { current = File.ReadAllText(path); }
        catch (FileNotFoundException) { current = null; }
        catch (DirectoryNotFoundException) { current = null; }
        if (!string.Equals(current, expected, StringComparison.Ordinal)) return false;
        WriteAtomic(path, content);
        return true;
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _mutex, null);
            if (owned == null) return;
            try { owned.ReleaseMutex(); } finally { owned.Dispose(); }
        }
    }
}
