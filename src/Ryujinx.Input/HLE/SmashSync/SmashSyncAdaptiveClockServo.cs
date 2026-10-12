using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ryujinx.Input.HLE.SmashSync
{
    /// <summary>
    /// Small phase-locked pacing controller for SmashSync netplay.
    ///
    /// The strict input barrier remains authoritative. This controller only trims
    /// small phase errors before they turn into a hard stall. It adjusts both the
    /// local SmashSync epoch and the guest tick scalar by a bounded amount.
    /// </summary>
    internal static class SmashSyncAdaptiveClockServo
    {
        private const int UpdateIntervalMs = 50;
        private const double ErrorAlpha = 0.25;
        private const double ErrorDeadbandTicks = 0.35;
        private const double GainPercentPerTick = 1.25;
        private const double MinimumSpeedupHeadroom = 0.97;

        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo ConfigField = typeof(SmashSyncSession).GetField("_config", PrivateInstance);
        private static readonly FieldInfo EffectiveDelayField = typeof(SmashSyncSession).GetField("_effectiveInputDelayTicks", PrivateInstance);
        private static readonly FieldInfo LastRttField = typeof(SmashSyncSession).GetField("_lastRttMs", PrivateInstance);
        private static readonly FieldInfo RunningStartedField = typeof(SmashSyncSession).GetField("_runningStartedNs", PrivateInstance);
        private static readonly FieldInfo LocalEpochField = typeof(SmashSyncSession).GetField("_localEpochNs", PrivateInstance);
        private static readonly FieldInfo RemoteHistoryField = typeof(SmashSyncSession).GetField("_remoteHistory", PrivateInstance);
        private static readonly FieldInfo RemoteLockField = typeof(SmashSyncSession).GetField("_remoteLock", PrivateInstance);

        private sealed class State
        {
            public Switch Device;
            public long BaselineTickScalar = 100;
            public long AppliedTickScalar = 100;
            public long LastUpdateMs;
            public long LastUpdateNs;
            public double ErrorEwma;
            public int RatePercent = 100;
        }

        private static readonly ConditionalWeakTable<object, State> States = new();

        public static void Attach(object owner, Switch device)
        {
            if (owner == null || device == null)
            {
                return;
            }

            State state = States.GetOrCreateValue(owner);
            if (!ReferenceEquals(state.Device, device))
            {
                state.Device = device;
                state.BaselineTickScalar = device.TickScalar;
                state.AppliedTickScalar = device.TickScalar;
                state.LastUpdateMs = 0;
                state.LastUpdateNs = 0;
                state.ErrorEwma = 0;
                state.RatePercent = 100;
            }
        }

        /// <summary>
        /// Called immediately after the normal SmashSync control pump. The session
        /// intentionally remains the owner of strict lockstep; the servo observes
        /// its received-input buffer and nudges only local pacing.
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

            // While a hard barrier is active the guest clock is suspended anyway.
            // Preserve the last correction so the peer does not immediately race
            // back into another stall after resume.
            if (session.OwnsPause || session.PauseRequested)
            {
                return;
            }

            if (ConfigField?.GetValue(session) is not SmashSyncConfig config ||
                EffectiveDelayField == null ||
                LastRttField == null ||
                RunningStartedField == null ||
                LocalEpochField == null ||
                RemoteHistoryField == null ||
                RemoteLockField == null)
            {
                Restore(session);
                return;
            }

            if (!config.EnableAdaptiveClockSync || config.MaxClockRateAdjustmentPercent <= 0)
            {
                Restore(session);
                return;
            }

            long localEpochNs = (long)LocalEpochField.GetValue(session);
            if (localEpochNs <= 0)
            {
                return;
            }

            Dictionary<ulong, GamepadInput> remoteHistory = RemoteHistoryField.GetValue(session) as Dictionary<ulong, GamepadInput>;
            object remoteLock = RemoteLockField.GetValue(session);
            if (remoteHistory == null || remoteLock == null)
            {
                return;
            }

            ulong localTick = session.SharedTick;
            ulong highestRemoteTick = 0;
            bool haveRemote = false;

            lock (remoteLock)
            {
                foreach (ulong tick in remoteHistory.Keys)
                {
                    if (!haveRemote || tick > highestRemoteTick)
                    {
                        highestRemoteTick = tick;
                        haveRemote = true;
                    }
                }
            }

            if (!haveRemote)
            {
                return;
            }

            long signedLead = highestRemoteTick >= localTick
                ? (long)Math.Min(highestRemoteTick - localTick, 64UL)
                : -(long)Math.Min(localTick - highestRemoteTick, 64UL);

            int effectiveDelay = (int)EffectiveDelayField.GetValue(session);
            double lastRttMs = (double)LastRttField.GetValue(session);
            double tickMs = 1000.0 / Math.Max(1, config.SyncHz);
            int transportTicks = lastRttMs > 0
                ? (int)Math.Floor((lastRttMs * 0.5) / tickMs)
                : 0;
            int targetLead = Math.Max(0, effectiveDelay - transportTicks);

            long nowNs = MonotonicNowNs();
            long runningStartedNs = (long)RunningStartedField.GetValue(session);
            double effectiveHz = 0;
            if (runningStartedNs > 0 && nowNs > runningStartedNs && localTick > 0)
            {
                effectiveHz = localTick * 1_000_000_000.0 / (nowNs - runningStartedNs);
            }

            long nowMs = Environment.TickCount64;
            long originalEpochNs = localEpochNs;

            Update(
                session,
                config,
                nowMs,
                nowNs,
                localTick,
                (int)signedLead,
                targetLead,
                effectiveHz,
                ref localEpochNs);

            if (localEpochNs != originalEpochNs)
            {
                LocalEpochField.SetValue(session, localEpochNs);
            }
        }

        public static void ResetPhase(object owner)
        {
            if (owner == null || !States.TryGetValue(owner, out State state))
            {
                return;
            }

            state.LastUpdateMs = 0;
            state.LastUpdateNs = 0;
            state.ErrorEwma = 0;
            ApplyRate(state, 100);
        }

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
            if (owner == null ||
                config == null ||
                !config.EnableAdaptiveClockSync ||
                config.MaxClockRateAdjustmentPercent <= 0 ||
                !States.TryGetValue(owner, out State state) ||
                state.Device == null ||
                state.Device.TurboMode ||
                localEpochNs <= 0)
            {
                Restore(owner);
                return;
            }

            if (state.LastUpdateMs != 0 && nowMs - state.LastUpdateMs < UpdateIntervalMs)
            {
                return;
            }

            if (state.LastUpdateNs == 0)
            {
                state.LastUpdateNs = nowNs;
                state.LastUpdateMs = nowMs;
                return;
            }

            long elapsedNs = Math.Clamp(nowNs - state.LastUpdateNs, 0, 250_000_000L);
            state.LastUpdateNs = nowNs;
            state.LastUpdateMs = nowMs;

            double rawError = Math.Clamp(remoteLeadTicks - targetLeadTicks, -8, 8);
            state.ErrorEwma += (rawError - state.ErrorEwma) * ErrorAlpha;

            int maxAdjust = Math.Clamp(config.MaxClockRateAdjustmentPercent, 0, 10);
            double desiredAdjustment = 0;

            if (Math.Abs(state.ErrorEwma) >= ErrorDeadbandTicks)
            {
                desiredAdjustment = Math.Clamp(
                    state.ErrorEwma * GainPercentPerTick,
                    -maxAdjust,
                    maxAdjust);
            }

            // If this machine is genuinely compute-bound, increasing its guest clock
            // cannot manufacture CPU/GPU capacity and can make divergence worse. In
            // that case leave it at real-time speed and let the leading peer slow.
            if (desiredAdjustment > 0 &&
                effectiveHz > 0 &&
                effectiveHz < config.SyncHz * MinimumSpeedupHeadroom)
            {
                desiredAdjustment = 0;
            }

            int ratePercent = 100 + (int)Math.Round(desiredAdjustment, MidpointRounding.AwayFromZero);
            ratePercent = Math.Clamp(ratePercent, 100 - maxAdjust, 100 + maxAdjust);

            // Move the local shared epoch at the inverse rate. A 99% rate pushes the
            // next due time forward by ~1% of wall time; 101% pulls it earlier.
            // This adjusts SmashSync sequence pacing itself rather than only changing
            // guest-visible timers.
            long epochAdjustmentNs = (long)Math.Round(
                elapsedNs * (100.0 - ratePercent) / 100.0,
                MidpointRounding.AwayFromZero);

            localEpochNs += epochAdjustmentNs;
            ApplyRate(state, ratePercent);
        }

        public static (int RatePercent, double ErrorTicks) GetStatus(object owner)
        {
            if (owner != null && States.TryGetValue(owner, out State state))
            {
                return (state.RatePercent, state.ErrorEwma);
            }

            return (100, 0);
        }

        public static void Restore(object owner)
        {
            if (owner == null || !States.TryGetValue(owner, out State state))
            {
                return;
            }

            ApplyRate(state, 100);
            state.ErrorEwma = 0;
        }

        private static long MonotonicNowNs()
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            long seconds = ticks / System.Diagnostics.Stopwatch.Frequency;
            long remainder = ticks % System.Diagnostics.Stopwatch.Frequency;
            return seconds * 1_000_000_000L + remainder * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void ApplyRate(State state, int ratePercent)
        {
            if (state.Device == null)
            {
                return;
            }

            ratePercent = Math.Clamp(ratePercent, 50, 150);
            long desiredScalar = Math.Max(
                1,
                (long)Math.Round(state.BaselineTickScalar * ratePercent / 100.0, MidpointRounding.AwayFromZero));

            if (state.AppliedTickScalar == desiredScalar && state.RatePercent == ratePercent)
            {
                return;
            }

            try
            {
                state.Device.TickScalar = desiredScalar;
                state.AppliedTickScalar = desiredScalar;

                if (state.RatePercent != ratePercent)
                {
                    Logger.Info?.PrintMsg(
                        LogClass.Application,
                        $"SmashSync: adaptive clock rate {state.RatePercent}% -> {ratePercent}% guestScalar={desiredScalar}");
                }

                state.RatePercent = ratePercent;
            }
            catch
            {
                // The Switch may be disposing while the session shuts down.
            }
        }
    }
}
