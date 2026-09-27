using ARMeilleure.State;
using System;

namespace Ryujinx.Cpu
{
    /// <summary>
    /// Tick source interface.
    /// </summary>
    public interface ITickSource : ICounter
    {
        public const long RealityTickScalar = 100;

        /// <summary>
        /// Time elapsed since the counter was created.
        /// </summary>
        TimeSpan ElapsedTime { get; }

        /// <summary>
        /// Clock tick scalar, in percent points (100 = 1.0).
        /// </summary>
        long TickScalar { get; set; }

        /// <summary>
        /// Time elapsed since the counter was created, in seconds.
        /// </summary>
        double ElapsedSeconds { get; }

        /// <summary>
        /// Stops counting.
        /// </summary>
        void Suspend();

        /// <summary>
        /// Resets elapsed guest time to zero while preserving the current
        /// running/stopped state.
        /// </summary>
        void Reset();

        /// <summary>
        /// Resumes counting after a call to <see cref="Suspend"/>.
        /// </summary>
        void Resume();
    }
}
