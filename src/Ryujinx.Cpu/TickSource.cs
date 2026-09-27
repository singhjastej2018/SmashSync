using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Cpu
{
    public class TickSource : ITickSource
    {
        // Guest counter reads can be emitted directly by translated CPU code and are
        // therefore a hot path. Writers are rare (pause/resume/reset/turbo changes),
        // so use a small seqlock: reads do not take a monitor lock, while writers
        // publish a consistent base-host/base-guest/scalar snapshot.
        private readonly object _writeLock = new();
        private readonly Stopwatch _tickCounter;
        private readonly double _hostTickFreq;

        private long _baseHostTicks;
        private long _baseGuestTicks;
        private long _tickScalar;
        private int _stateVersion;

        /// <inheritdoc/>
        public ulong Frequency { get; }

        /// <inheritdoc/>
        public ulong Counter => (ulong)(ElapsedSeconds * Frequency);

        /// <inheritdoc/>
        public long TickScalar
        {
            get => Volatile.Read(ref _tickScalar);
            set
            {
                lock (_writeLock)
                {
                    BeginWrite();

                    long hostTicks = _tickCounter.ElapsedTicks;
                    long guestTicks = ComputeElapsedTicksLocked(hostTicks);

                    _baseGuestTicks = guestTicks;
                    _baseHostTicks = hostTicks;
                    _tickScalar = value;

                    EndWrite();
                }
            }
        }

        private void BeginWrite()
        {
            // Writers are serialized by _writeLock. Odd means a write is active.
            Interlocked.Increment(ref _stateVersion);
        }

        private void EndWrite()
        {
            // Even means readers may consume the published snapshot.
            Interlocked.Increment(ref _stateVersion);
        }

        private long ComputeElapsedTicksLocked(long hostTicks)
        {
            long delta = hostTicks - _baseHostTicks;
            if (delta <= 0)
            {
                return _baseGuestTicks;
            }

            return _baseGuestTicks + delta * _tickScalar / 100;
        }

        private long ElapsedTicks
        {
            get
            {
                while (true)
                {
                    int versionBefore = Volatile.Read(ref _stateVersion);
                    if ((versionBefore & 1) != 0)
                    {
                        Thread.SpinWait(1);
                        continue;
                    }

                    long baseHostTicks = Volatile.Read(ref _baseHostTicks);
                    long baseGuestTicks = Volatile.Read(ref _baseGuestTicks);
                    long scalar = Volatile.Read(ref _tickScalar);
                    long hostTicks = _tickCounter.ElapsedTicks;

                    int versionAfter = Volatile.Read(ref _stateVersion);
                    if (versionBefore != versionAfter)
                    {
                        continue;
                    }

                    long delta = hostTicks - baseHostTicks;
                    if (delta <= 0)
                    {
                        return baseGuestTicks;
                    }

                    return baseGuestTicks + delta * scalar / 100;
                }
            }
        }

        /// <inheritdoc/>
        public TimeSpan ElapsedTime => Stopwatch.GetElapsedTime(0, ElapsedTicks);

        /// <inheritdoc/>
        public double ElapsedSeconds => ElapsedTicks * _hostTickFreq;

        public TickSource(ulong frequency)
        {
            Frequency = frequency;
            _hostTickFreq = 1.0 / Stopwatch.Frequency;
            _tickCounter = Stopwatch.StartNew();
        }

        /// <inheritdoc/>
        public void Suspend()
        {
            lock (_writeLock)
            {
                BeginWrite();

                if (_tickCounter.IsRunning)
                {
                    _tickCounter.Stop();

                    long hostTicks = _tickCounter.ElapsedTicks;
                    _baseGuestTicks = ComputeElapsedTicksLocked(hostTicks);
                    _baseHostTicks = hostTicks;
                }

                EndWrite();
            }
        }

        /// <inheritdoc/>
        public void Reset()
        {
            lock (_writeLock)
            {
                BeginWrite();

                bool wasRunning = _tickCounter.IsRunning;
                _tickCounter.Reset();
                _baseHostTicks = 0;
                _baseGuestTicks = 0;

                if (wasRunning)
                {
                    _tickCounter.Start();
                }

                EndWrite();
            }
        }

        /// <inheritdoc/>
        public void Resume()
        {
            lock (_writeLock)
            {
                BeginWrite();

                if (!_tickCounter.IsRunning)
                {
                    // Stopwatch excludes suspended wall time. Rebase at the stopped
                    // host tick so the next lock-free read starts with a zero delta.
                    _baseHostTicks = _tickCounter.ElapsedTicks;
                    _tickCounter.Start();
                }

                EndWrite();
            }
        }
    }
}
