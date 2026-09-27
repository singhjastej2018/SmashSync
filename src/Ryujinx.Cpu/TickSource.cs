using System;
using System.Diagnostics;

namespace Ryujinx.Cpu
{
    public class TickSource : ITickSource
    {
        private readonly object _sync = new();
        private readonly Stopwatch _tickCounter;
        private readonly double _hostTickFreq;

        private long _accumElapsedTicks;
        private long _lastElapsedTicks;

        /// <inheritdoc/>
        public ulong Frequency { get; }

        /// <inheritdoc/>
        public ulong Counter => (ulong)(ElapsedSeconds * Frequency);

        public long TickScalar { get; set; }

        private long AccumulateElapsedTicksLocked()
        {
            long elapsedTicks = _tickCounter.ElapsedTicks;
            long delta = elapsedTicks - _lastElapsedTicks;

            // Stopwatch is monotonic, but Reset changes its epoch. Treat any
            // unexpected negative delta as zero rather than corrupting guest time.
            if (delta > 0)
            {
                _accumElapsedTicks += delta * TickScalar / 100;
            }

            _lastElapsedTicks = elapsedTicks;
            return _accumElapsedTicks;
        }

        private long ElapsedTicks
        {
            get
            {
                lock (_sync)
                {
                    return AccumulateElapsedTicksLocked();
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
            lock (_sync)
            {
                if (!_tickCounter.IsRunning)
                {
                    return;
                }

                AccumulateElapsedTicksLocked();
                _tickCounter.Stop();
            }
        }

        /// <inheritdoc/>
        public void Reset()
        {
            lock (_sync)
            {
                bool wasRunning = _tickCounter.IsRunning;

                _tickCounter.Reset();
                _accumElapsedTicks = 0;
                _lastElapsedTicks = 0;

                if (wasRunning)
                {
                    _tickCounter.Start();
                }
            }
        }

        /// <inheritdoc/>
        public void Resume()
        {
            lock (_sync)
            {
                if (_tickCounter.IsRunning)
                {
                    return;
                }

                // Do not count host wall time spent suspended.
                _lastElapsedTicks = _tickCounter.ElapsedTicks;
                _tickCounter.Start();
            }
        }
    }
}
