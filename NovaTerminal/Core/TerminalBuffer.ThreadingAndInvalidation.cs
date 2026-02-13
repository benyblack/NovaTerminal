using System;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private int _batchWriteDepth;
        private bool _batchInvalidatePending;
        private static readonly TimeSpan _maxSyncDuration = TimeSpan.FromMilliseconds(200);

        public bool IsSynchronizedOutput => _isSynchronizedOutput;

        private bool EnterReadLockIfNeeded()
        {
            if (Lock.IsWriteLockHeld || Lock.IsReadLockHeld) return false;
            Lock.EnterReadLock();
            return true;
        }

        private static void ExitReadLockIfNeeded(System.Threading.ReaderWriterLockSlim rwLock, bool lockTaken)
        {
            if (lockTaken) rwLock.ExitReadLock();
        }

        private bool EnterWriteLockIfNeeded()
        {
            if (Lock.IsWriteLockHeld) return false;
            Lock.EnterWriteLock();
            return true;
        }

        private static void ExitWriteLockIfNeeded(System.Threading.ReaderWriterLockSlim rwLock, bool lockTaken)
        {
            if (lockTaken) rwLock.ExitWriteLock();
        }

        public void EnterBatchWrite()
        {
            Lock.EnterWriteLock();
            _batchWriteDepth++;
        }

        public void ExitBatchWrite()
        {
            if (!Lock.IsWriteLockHeld || _batchWriteDepth <= 0)
            {
                throw new InvalidOperationException("ExitBatchWrite called without an active batch write lock.");
            }

            _batchWriteDepth--;
            bool shouldFlushInvalidate = _batchWriteDepth == 0 && _batchInvalidatePending;
            if (_batchWriteDepth == 0)
            {
                _batchInvalidatePending = false;
            }

            Lock.ExitWriteLock();

            if (shouldFlushInvalidate)
            {
                Invalidate();
            }
        }

        public void BeginSync()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _isSynchronizedOutput = true;
                _lastSyncStart = DateTime.UtcNow;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        public void EndSync()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (!_isSynchronizedOutput) return;
                _isSynchronizedOutput = false;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }

            // Trigger deferred invalidation immediately
            OnInvalidate?.Invoke();
        }

        public void Invalidate()
        {
            // Defer all invalidation while a parser batch holds the write lock.
            if (_batchWriteDepth > 0)
            {
                _batchInvalidatePending = true;
                return;
            }

            // If synchronized, check for timeout safety
            if (_isSynchronizedOutput)
            {
                if ((DateTime.UtcNow - _lastSyncStart) > _maxSyncDuration)
                {
                    // Timeout exceeded, force flush
                    EndSync();
                }
                else
                {
                    // Defer invalidation
                    return;
                }
            }

            OnInvalidate?.Invoke();
        }
    }
}
