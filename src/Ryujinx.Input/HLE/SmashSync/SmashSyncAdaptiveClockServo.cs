using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ryujinx.Input.HLE.SmashSync
{
    /// <summary>
    /// Bridges SmashSync's host-monotonic epoch onto Ryujinx guest time.
    ///
    /// SmashSyncSession still stores tick deadlines as host nanoseconds. During a
    /// lockstep pause, however, the guest TickSource is suspended while host time
    /// continues to advance. Without compensating for that pause, every logical HID
    /// tick that became overdue is consumed in a burst after resume. Two peers can
    /// therefore expose the same canonical input stream on different SSBU updates.
    ///
    /// This bridge continuously expresses "guest elapsed time since netplay start"
    /// as an equivalent host epoch. While the guest runs at normal speed the epoch
    /// remains effectively constant. While the guest is paused the epoch moves
    /// forward with host time, so paused wall time never creates catch-up ticks.
    ///
    /// This deliberately never changes TickScalar and never performs peer-local rate
    /// correction. It is an intermediate determinism mechanism, not a substitute for
    /// a verified SSBU simulation-frame barrier.
    /// </summary>
    internal static class SmashSyncAdaptiveClockServo
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo RunningStartedField = typeof(SmashSyncSession).GetField("_runningStartedNs", PrivateInstance);
        private static readonly FieldInfo LocalEpochField = typeof(SmashSyncSession).GetField("_localEpochNs", PrivateInstance);

        private sealed class State
        {
            public Switch Device;
            public object TickSource;
            public PropertyInfo CounterProperty;
            public PropertyInfo FrequencyProperty;
            public long RunMarkerNs;
            public ulong GuestEpochCounter;
            public ulong GuestFrequency;
            public bool TimelineArmed;
            public bool ClockUnavailableReported;
            public long LastPublishedEpochNs;
        }

        private static readonly ConditionalWeakTable<object, State> States = new();

        public static void Attach(object owner, Switch device)
        {
            if (owner == null || device == null)
            {
                return;
            }

            State state = States.GetValue(owner, static _ => new State());
            if (!ReferenceEquals(state.Device, device))
            {
                state.Device = device;
                ResetState(state, clearDeviceClock: true);
            }
        }

        /// <summary>
        /// Called after SmashSyncSession.PumpControl(). Re-publishes the local host
        /// epoch so IsTickDue() advances only when guest-visible time advances.
        /// </summary>
        public static void UpdateFromSession(SmashSyncSession session)
        {
            if (session == null)
            {
                return;
            }

            if (!session.CanonicalRouting)
            {
                Restore(session);
                return;
            }

            if (RunningStartedField == null || LocalEpochField == null ||
                !States.TryGetValue(session, out State state) || state.Device == null)
            {
                return;
            }

            long runMarkerNs = (long)RunningStartedField.GetValue(session);
            if (runMarkerNs <= 0)
            {
                // The synchronized release epoch is still authoritative while the
                // startup barrier is active. Do not rewrite it before the guest runs.
                ResetTimeline(state);
                return;
            }

            if (!TryReadGuestClock(state, out ulong guestCounter, out ulong guestFrequency))
            {
                if (!state.ClockUnavailableReported)
                {
                    state.ClockUnavailableReported = true;
                    Logger.Warning?.PrintMsg(
                        LogClass.Application,
                        "SmashSync: guest TickSource unavailable; retaining host-time input scheduling");
                }
                return;
            }

            long nowNs = MonotonicNowNs();

            if (!state.TimelineArmed ||
                state.RunMarkerNs != runMarkerNs ||
                state.GuestFrequency != guestFrequency ||
                guestCounter < state.GuestEpochCounter)
            {
                ArmTimeline(state, runMarkerNs, nowNs, guestCounter, guestFrequency);
            }

            ulong guestElapsedTicks = guestCounter - state.GuestEpochCounter;
            long guestElapsedNs = GuestTicksToNanoseconds(guestElapsedTicks, state.GuestFrequency);
            long desiredEpochNs = guestElapsedNs >= nowNs ? 1 : nowNs - guestElapsedNs;

            // Avoid a reflection write when the calculated mapping is unchanged.
            // The mapping is absolute (not incremental), so sub-microsecond sampling
            // jitter cannot accumulate into long-term phase drift.
            if (desiredEpochNs != state.LastPublishedEpochNs)
            {
                LocalEpochField.SetValue(session, desiredEpochNs);
                state.LastPublishedEpochNs = desiredEpochNs;
            }
        }

        public static void ResetPhase(object owner)
        {
            if (owner != null && States.TryGetValue(owner, out State state))
            {
                ResetTimeline(state);
            }
        }

        /// <summary>
        /// Legacy call surface retained for older integration/tests. Adaptive rate
        /// control is intentionally disabled; active synchronization is performed by
        /// UpdateFromSession using the guest timeline instead.
        /// </summary>
        public static void Update(
            object owner,
            SmashSyncConfig config,
            long nowMs,
            long nowNs,
            ulong localTick,
            int remoteLeadTicks,
            int targetLeadTicks,
            double effectiveHz,
            ref long localEpochNs)
        {
        }

        public static (int RatePercent, double ErrorTicks) GetStatus(object owner) => (100, 0);

        public static void Restore(object owner)
        {
            if (owner != null && States.TryGetValue(owner, out State state))
            {
                ResetTimeline(state);
            }
        }

        private static void ArmTimeline(
            State state,
            long runMarkerNs,
            long nowNs,
            ulong guestCounter,
            ulong guestFrequency)
        {
            // NotifyResumed records runMarkerNs immediately after the guest TickSource
            // resumes. Back-project the first observed counter to that instant so the
            // first control-pump delay does not become a permanent input phase offset.
            long hostElapsedSinceRunNs = Math.Max(0, nowNs - runMarkerNs);
            ulong estimatedGuestTicksSinceRun = NanosecondsToGuestTicks(
                hostElapsedSinceRunNs,
                guestFrequency,
                state.Device?.TickScalar ?? 100);

            state.GuestEpochCounter = guestCounter >= estimatedGuestTicksSinceRun
                ? guestCounter - estimatedGuestTicksSinceRun
                : 0;
            state.GuestFrequency = guestFrequency;
            state.RunMarkerNs = runMarkerNs;
            state.TimelineArmed = true;
            state.ClockUnavailableReported = false;
            state.LastPublishedEpochNs = 0;

            Logger.Info?.PrintMsg(
                LogClass.Application,
                $"SmashSync: guest-time input timeline armed counter={state.GuestEpochCounter} frequency={guestFrequency}");
        }

        private static bool TryReadGuestClock(State state, out ulong counter, out ulong frequency)
        {
            counter = 0;
            frequency = 0;

            try
            {
                if (state.TickSource == null)
                {
                    object horizon = state.Device?.System;
                    if (horizon == null)
                    {
                        return false;
                    }

                    PropertyInfo tickSourceProperty = horizon.GetType().GetProperty("TickSource", AnyInstance);
                    state.TickSource = tickSourceProperty?.GetValue(horizon);
                    if (state.TickSource == null)
                    {
                        return false;
                    }

                    Type tickSourceType = state.TickSource.GetType();
                    state.CounterProperty = tickSourceType.GetProperty("Counter", AnyInstance);
                    state.FrequencyProperty = tickSourceType.GetProperty("Frequency", AnyInstance);
                }

                if (state.CounterProperty?.GetValue(state.TickSource) is not ulong currentCounter ||
                    state.FrequencyProperty?.GetValue(state.TickSource) is not ulong currentFrequency ||
                    currentFrequency == 0)
                {
                    return false;
                }

                counter = currentCounter;
                frequency = currentFrequency;
                return true;
            }
            catch
            {
                // The Switch/Horizon may be disposing while the input thread exits.
                return false;
            }
        }

        private static long GuestTicksToNanoseconds(ulong ticks, ulong frequency)
        {
            if (frequency == 0 || ticks == 0)
            {
                return 0;
            }

            decimal nanoseconds = (decimal)ticks * 1_000_000_000m / frequency;
            return nanoseconds >= long.MaxValue ? long.MaxValue : (long)nanoseconds;
        }

        private static ulong NanosecondsToGuestTicks(long nanoseconds, ulong frequency, long tickScalar)
        {
            if (nanoseconds <= 0 || frequency == 0)
            {
                return 0;
            }

            decimal scalar = Math.Max(1, tickScalar) / 100m;
            decimal ticks = (decimal)nanoseconds * frequency * scalar / 1_000_000_000m;
            return ticks >= ulong.MaxValue ? ulong.MaxValue : (ulong)ticks;
        }

        private static long MonotonicNowNs()
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            long seconds = ticks / System.Diagnostics.Stopwatch.Frequency;
            long remainder = ticks % System.Diagnostics.Stopwatch.Frequency;
            return seconds * 1_000_000_000L + remainder * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void ResetTimeline(State state)
        {
            state.RunMarkerNs = 0;
            state.GuestEpochCounter = 0;
            state.GuestFrequency = 0;
            state.TimelineArmed = false;
            state.LastPublishedEpochNs = 0;
        }

        private static void ResetState(State state, bool clearDeviceClock)
        {
            ResetTimeline(state);
            state.ClockUnavailableReported = false;

            if (clearDeviceClock)
            {
                state.TickSource = null;
                state.CounterProperty = null;
                state.FrequencyProperty = null;
            }
        }
    }
}
