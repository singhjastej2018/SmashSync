using Ryujinx.HLE;

namespace Ryujinx.Input.HLE.SmashSync
{
    /// <summary>
    /// Compatibility shim for the experimental adaptive pacing controller.
    ///
    /// Peer-local clock correction is intentionally disabled. Changing either the
    /// guest TickScalar or the local SmashSync epoch independently can cause the
    /// same logical input tick to become visible on different SSBU simulation/menu
    /// updates on the two peers. Accumulated states such as the stage-select cursor
    /// then drift even while the canonical input stream itself still matches.
    ///
    /// Keep the call surface in place so existing NpadManager integration and old
    /// configuration files remain compatible. A future controller may replace this
    /// shim once pacing is tied to a verified guest/game simulation boundary and a
    /// correction is applied identically to both peers.
    /// </summary>
    internal static class SmashSyncAdaptiveClockServo
    {
        public static void Attach(object owner, Switch device)
        {
        }

        public static void UpdateFromSession(SmashSyncSession session)
        {
        }

        public static void ResetPhase(object owner)
        {
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
        }

        public static (int RatePercent, double ErrorTicks) GetStatus(object owner) => (100, 0);

        public static void Restore(object owner)
        {
        }
    }
}
