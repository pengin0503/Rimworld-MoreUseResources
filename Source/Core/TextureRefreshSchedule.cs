using System;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Tracks finished GPU prewarm passes, allowing another pass after a configured delay.
    /// No graphics objects are owned by this class. Intended to be used only on the game thread.
    /// </summary>
    public sealed class TextureRefreshSchedule
    {
        private long? _completedAtMs;

        public int PassesCompleted { get; private set; }

        public void MarkPassCompleted(long nowMs)
        {
            if (_completedAtMs.HasValue) return;
            _completedAtMs = nowMs;
            PassesCompleted++;
        }

        public bool ShouldRefresh(long nowMs, int refreshSeconds)
        {
            if (!_completedAtMs.HasValue || refreshSeconds <= 0) return false;
            if (nowMs < _completedAtMs.Value)
            {
                // A clock rollback cannot trigger unlimited immediate rescans.
                _completedAtMs = nowMs;
                return false;
            }
            return nowMs - _completedAtMs.Value >= (long)refreshSeconds * 1000L;
        }

        public void BeginNextPass() => _completedAtMs = null;
    }
}
