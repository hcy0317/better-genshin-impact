using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace BetterGenshinImpact.Core.Mask;

/// <summary>
/// 遮罩窗口绘制内容的存储。
/// 业务线程写入不可变快照，UI 侧订阅 <see cref="Changed"/> 后合并拉取。
/// </summary>
public sealed class MaskWindowDrawingBoard : IMaskWindowDrawingBoard, IMaskWindowSnapshotSource<MaskWindowDrawingSnapshot>
{
    private readonly object _lock = new();

    private ImmutableDictionary<string, MaskWindowDrawingEntry> _entries =
        ImmutableDictionary.Create<string, MaskWindowDrawingEntry>(StringComparer.Ordinal);

    private MaskWindowDrawingSnapshot _current = MaskWindowDrawingSnapshot.Empty;

    public MaskWindowDrawingSnapshot Current => Volatile.Read(ref _current);

    public event Action? Changed;

    private int _recognitionSuppressions;

    internal IDisposable SuppressRecognition()
    {
        var lease = new RecognitionLease(this);
        bool changed;
        lock (_lock)
        {
            _recognitionSuppressions++;
            var names = _entries.Where(x => x.Value.Group.Kind == MaskWindowDrawingKind.Recognition && x.Value.Group.IsAutomaticDiagnostic).Select(x => x.Key).ToArray();
            changed = names.Length != 0;
            if (changed) { _entries = _entries.RemoveRange(names); PublishLocked(); }
        }
        try { if (changed) Changed?.Invoke(); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private void ResumeRecognition()
    {
        lock (_lock) _recognitionSuppressions--;
    }

    private sealed class RecognitionLease(MaskWindowDrawingBoard owner) : IDisposable
    {
        private MaskWindowDrawingBoard? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ResumeRecognition();
    }

    public void Set(MaskWindowDrawingGroup group, IReadOnlyList<MaskWindowDrawingShape>? shapes)
    {
        if (shapes == null || shapes.Count == 0)
        {
            Clear(group);
            return;
        }

        // 拷贝一份，调用方之后再修改原列表也不会影响已存的内容
        var copy = shapes.ToArray();
        lock (_lock)
        {
            if (_recognitionSuppressions != 0 && group.Kind == MaskWindowDrawingKind.Recognition && group.IsAutomaticDiagnostic) return;
            if (_entries.TryGetValue(group.Name, out var existing)
                && existing.Group == group
                && existing.Shapes.SequenceEqual(copy))
            {
                return;
            }

            _entries = _entries.SetItem(group.Name, new MaskWindowDrawingEntry(group, copy));
            PublishLocked();
        }

        Changed?.Invoke();
    }

    public void Clear(MaskWindowDrawingGroup group)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(group.Name, out var existing) ||
                group.IsAutomaticDiagnostic && !existing.Group.IsAutomaticDiagnostic)
            {
                return;
            }

            _entries = _entries.Remove(group.Name);
            PublishLocked();
        }

        Changed?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            if (_entries.IsEmpty)
            {
                return;
            }

            _entries = _entries.Clear();
            PublishLocked();
        }

        Changed?.Invoke();
    }

    private void PublishLocked()
    {
        var entries = _entries.IsEmpty ? Array.Empty<MaskWindowDrawingEntry>() : _entries.Values.ToArray();
        Volatile.Write(ref _current, new MaskWindowDrawingSnapshot(_current.Version + 1, entries));
    }
}
