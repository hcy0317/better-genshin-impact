using System;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>系统输入只有一个持有者。旧工作退出并成功释放前，不允许下一场接管。</summary>
public sealed class CombatInputCoordinator
{
    private readonly object _gate = new();
    private Session? _owner;

    public Session? TryAcquire(Guid battleId, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            if (_owner != null) return null;
            return _owner = new(this, battleId, release);
        }
    }

    public sealed class Session
        : IDisposable
    {
        private readonly CombatInputCoordinator _coordinator;
        private readonly Action _release;
        private bool _closed;
        private bool _working;
        private bool _releaseStarted;
        public Guid BattleId { get; }

        internal Session(CombatInputCoordinator coordinator, Guid battleId, Action release)
        {
            _coordinator = coordinator;
            BattleId = battleId;
            _release = release;
        }

        public IDisposable EnterOperation()
        {
            lock (_coordinator._gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_working) throw new InvalidOperationException("同一战斗不能并发发送两组输入");
                _working = true;
                return new Operation(this);
            }
        }

        public bool TryReleaseInput(Action release)
        {
            ArgumentNullException.ThrowIfNull(release);
            lock (_coordinator._gate)
            {
                if (_closed || _working || !ReferenceEquals(_coordinator._owner, this)) return false;
                try { release(); }
                catch
                {
                    _closed = true;
                    _releaseStarted = true; // 释放结果不确定时保留所有权，禁止交接。
                    throw;
                }
                return true;
            }
        }

        public void Dispose()
        {
            lock (_coordinator._gate)
            {
                _closed = true;
                ReleaseWhenIdle();
            }
        }

        private void ExitOperation()
        {
            lock (_coordinator._gate)
            {
                _working = false;
                ReleaseWhenIdle();
            }
        }

        private void ReleaseWhenIdle()
        {
            if (!_closed || _working || _releaseStarted) return;
            _releaseStarted = true;
            _release(); // 若释放失败，保留所有权并拒绝交接，不能猜测键已全部松开。
            if (ReferenceEquals(_coordinator._owner, this)) _coordinator._owner = null;
        }

        private sealed class Operation(Session session) : IDisposable
        {
            private Session? _session = session;
            public void Dispose() => Interlocked.Exchange(ref _session, null)?.ExitOperation();
        }
    }
}
