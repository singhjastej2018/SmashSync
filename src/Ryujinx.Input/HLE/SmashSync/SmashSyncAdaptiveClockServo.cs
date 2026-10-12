using Ryujinx.HLE;
using System;
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
                state.RatePercent = ratePercent;
            }
            catch
            {
                // The Switch may be disposing while the session shuts down.
            }
        }
    }
}
