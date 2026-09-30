using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.Memory;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.SmashAi
{
    /// <summary>
    /// Discovers and reads the fixed Smash AI observation block exported by the
    /// companion SSBU plugin. Protocol v2 also exposes a small host-writable
    /// control tail used for exact game-frame gating.
    /// </summary>
    internal sealed class SmashAiGuestStateBridge
    {
        private static readonly byte[] Magic = "SSAI0001"u8.ToArray();

        public const int HeaderSize = 24;
        public const int MaxStateSize = 16 * 1024;

        private const uint MinProtocolVersion = 1;
        private const uint MaxProtocolVersion = 2;

        // Protocol-v1 state ends at byte 288. Protocol v2 appends four u32s:
        // gate_enabled, step_budget, gate_epoch, gate_waiting.
        private const int ProtocolV2ControlOffset = 288;
        private const int ProtocolV2StateSize = 304;
        private const ulong GateEnabledOffset = ProtocolV2ControlOffset + 0;
        private const ulong StepBudgetOffset = ProtocolV2ControlOffset + 4;
        private const ulong GateWaitingOffset = ProtocolV2ControlOffset + 12;
        private const uint MaxStepFrames = 6000;
        private const int StableReadAttempts = 6;

        private const int ScanChunkSize = 64 * 1024;
        private const ulong PreferredPluginRegionMaxSize = 16UL * 1024 * 1024;
        private const ulong MaxFallbackBytesPerRegionType = 512UL * 1024 * 1024;

        private static readonly object RegistrationLock = new();
        private static ulong _registeredPid;
        private static ulong _registeredAddress;

        private readonly Switch _device;

        private ulong _cachedPid;
        private ulong _cachedAddress;
        private long _nextScanTimestamp;

        public SmashAiGuestStateBridge(Switch device)
        {
            _device = device;
        }

        public bool TryRead(Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;

            if (!TryGetActiveProcess(out KProcess process))
            {
                ResetCache();
                return false;
            }

            if (_cachedPid != process.Pid)
            {
                ResetCache();
                _cachedPid = process.Pid;
            }

            if (_cachedAddress != 0 &&
                TryReadStable(process, _cachedAddress, destination, out bytesWritten))
            {
                return true;
            }

            if (TryGetRegisteredAddress(process.Pid, out ulong registeredAddress) &&
                TryReadStable(process, registeredAddress, destination, out bytesWritten))
            {
                _cachedAddress = registeredAddress;
                return true;
            }

            _cachedAddress = 0;

            long now = Stopwatch.GetTimestamp();
            if (now < _nextScanTimestamp)
            {
                return false;
            }

            _nextScanTimestamp = now + (2 * Stopwatch.Frequency);

            // Skyline NRO writable data is mapped as writable code memory. SSBU's own
            // mutable data can be much larger than a plugin and can appear earlier in
            // the address space, so first scan small writable-code regions (where NRO
            // plugin data normally lives), then do a larger fallback scan.
            if (TryFindMagic(process, MemoryState.ModCodeMutable, PreferredPluginRegionMaxSize, ulong.MaxValue, out ulong address) ||
                TryFindMagic(process, MemoryState.CodeMutable, PreferredPluginRegionMaxSize, ulong.MaxValue, out address) ||
                TryFindMagic(process, MemoryState.ModCodeMutable, ulong.MaxValue, MaxFallbackBytesPerRegionType, out address) ||
                TryFindMagic(process, MemoryState.CodeMutable, ulong.MaxValue, MaxFallbackBytesPerRegionType, out address))
            {
                _cachedAddress = address;
                return TryReadStable(process, address, destination, out bytesWritten);
            }

            return false;
        }

        public bool TrySetFrameGate(bool enabled)
        {
            if (!TryResolveProtocolV2Control(out KProcess process, out ulong address))
            {
                return false;
            }

            try
            {
                if (enabled)
                {
                    // Budget must be zero before enabling, otherwise the first gated
                    // boundary could consume a stale step request.
                    process.CpuMemory.Write(address + StepBudgetOffset, 0u);
                    process.CpuMemory.Write(address + GateEnabledOffset, 1u);
                }
                else
                {
                    // Release a blocked guest frame thread first, then clear budget.
                    process.CpuMemory.Write(address + GateEnabledOffset, 0u);
                    process.CpuMemory.Write(address + StepBudgetOffset, 0u);
                }

                return true;
            }
            catch (InvalidMemoryRegionException)
            {
                _cachedAddress = 0;
                return false;
            }
        }

        public bool TryStepFrames(uint frames)
        {
            if (frames == 0 || frames > MaxStepFrames ||
                !TryResolveProtocolV2Control(out KProcess process, out ulong address))
            {
                return false;
            }

            try
            {
                uint enabled = process.CpuMemory.Read<uint>(address + GateEnabledOffset);
                uint budget = process.CpuMemory.Read<uint>(address + StepBudgetOffset);
                uint waiting = process.CpuMemory.Read<uint>(address + GateWaitingOffset);

                // Only release from a known stable frame boundary. This is what makes
                // protocol-v2 stepping exact rather than a host-side polling race.
                if (enabled != 1 || waiting != 1 || budget != 0)
                {
                    return false;
                }

                process.CpuMemory.Write(address + StepBudgetOffset, frames);
                return true;
            }
            catch (InvalidMemoryRegionException)
            {
                _cachedAddress = 0;
                return false;
            }
        }

        private bool TryResolveProtocolV2Control(out KProcess process, out ulong address)
        {
            address = 0;

            if (!TryGetActiveProcess(out process))
            {
                return false;
            }

            if (_cachedPid != process.Pid)
            {
                ResetCache();
                _cachedPid = process.Pid;
            }

            if (_cachedAddress == 0 &&
                TryGetRegisteredAddress(process.Pid, out ulong registeredAddress))
            {
                _cachedAddress = registeredAddress;
            }

            if (_cachedAddress == 0)
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(MaxStateSize);
                try
                {
                    if (!TryRead(rented.AsSpan(0, MaxStateSize), out _))
                    {
                        return false;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            address = _cachedAddress;
            if (address == 0)
            {
                return false;
            }

            try
            {
                Span<byte> header = stackalloc byte[16];
                process.CpuMemory.Read(address, header);

                if (!header[..8].SequenceEqual(Magic))
                {
                    return false;
                }

                uint protocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
                int totalSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));

                return protocolVersion >= 2 &&
                    protocolVersion <= MaxProtocolVersion &&
                    totalSize >= ProtocolV2StateSize &&
                    totalSize <= MaxStateSize;
            }
            catch (InvalidMemoryRegionException)
            {
                _cachedAddress = 0;
                return false;
            }
        }

        private bool TryGetActiveProcess(out KProcess process)
        {
            process = null;

            ProcessResult activeApplication = _device.Processes.ActiveApplication;
            return activeApplication != null &&
                _device.System.KernelContext.Processes.TryGetValue(activeApplication.ProcessId, out process);
        }

        internal static void RegisterLoadedNroData(KProcess process, ulong dataAddress, ulong dataSize)
        {
            if (process == null || dataAddress == 0 || dataSize < HeaderSize)
            {
                return;
            }

            if (TryFindMagicInRange(process, dataAddress, dataSize, out ulong address))
            {
                lock (RegistrationLock)
                {
                    _registeredPid = process.Pid;
                    _registeredAddress = address;
                }
            }
        }

        private static bool TryGetRegisteredAddress(ulong pid, out ulong address)
        {
            lock (RegistrationLock)
            {
                if (_registeredPid == pid && _registeredAddress != 0)
                {
                    address = _registeredAddress;
                    return true;
                }
            }

            address = 0;
            return false;
        }

        private static bool TryFindMagicInRange(KProcess process, ulong startAddress, ulong size, out ulong address)
        {
            address = 0;
            byte[] rented = ArrayPool<byte>.Shared.Rent(ScanChunkSize + Magic.Length - 1);

            try
            {
                ulong offset = 0;
                int carry = 0;

                while (offset < size)
                {
                    int length = (int)Math.Min((ulong)ScanChunkSize, size - offset);
                    Span<byte> chunk = rented.AsSpan(0, carry + length);

                    try
                    {
                        process.CpuMemory.Read(startAddress + offset, chunk.Slice(carry, length));
                    }
                    catch (InvalidMemoryRegionException)
                    {
                        carry = 0;
                        offset += (ulong)length;
                        continue;
                    }

                    int index = chunk.IndexOf(Magic);
                    if (index >= 0)
                    {
                        address = startAddress + offset - (ulong)carry + (ulong)index;
                        return true;
                    }

                    carry = Math.Min(Magic.Length - 1, chunk.Length);
                    if (carry > 0)
                    {
                        chunk.Slice(chunk.Length - carry, carry).CopyTo(rented.AsSpan(0, carry));
                    }

                    offset += (ulong)length;
                }

                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static bool TryReadStable(KProcess process, ulong address, Span<byte> destination, out int bytesWritten)
        {
            // While an exact step is running, Python may request an observation
            // during the exporter's very short seqlock write window. That is a
            // transient read collision, not evidence that the NRO disappeared.
            // Retry a few times before treating the address as unavailable.
            for (int attempt = 0; attempt < StableReadAttempts; attempt++)
            {
                if (TryReadAt(process, address, destination, out bytesWritten))
                {
                    return true;
                }

                Thread.SpinWait(64 << attempt);
            }

            bytesWritten = 0;
            return false;
        }

        private static bool TryReadAt(KProcess process, ulong address, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;

            try
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                process.CpuMemory.Read(address, header);

                if (!header[..8].SequenceEqual(Magic))
                {
                    return false;
                }

                uint protocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
                int totalSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
                uint sequenceBefore = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));

                if (protocolVersion < MinProtocolVersion ||
                    protocolVersion > MaxProtocolVersion ||
                    totalSize < HeaderSize ||
                    totalSize > MaxStateSize ||
                    totalSize > destination.Length ||
                    (sequenceBefore & 1) != 0)
                {
                    return false;
                }

                process.CpuMemory.Read(address, destination[..totalSize]);

                uint sequenceInCopy = BinaryPrimitives.ReadUInt32LittleEndian(destination.Slice(16, 4));
                uint sequenceAfter = process.CpuMemory.Read<uint>(address + 16);

                if (sequenceBefore != sequenceInCopy ||
                    sequenceBefore != sequenceAfter ||
                    (sequenceAfter & 1) != 0)
                {
                    return false;
                }

                bytesWritten = totalSize;
                return true;
            }
            catch (InvalidMemoryRegionException)
            {
                return false;
            }
        }

        private static bool TryFindMagic(
            KProcess process,
            MemoryState targetType,
            ulong maxRegionSize,
            ulong maxTotalBytes,
            out ulong address)
        {
            address = 0;
            ulong cursor = 0;
            ulong scannedBytes = 0;
            byte[] rented = ArrayPool<byte>.Shared.Rent(ScanChunkSize);

            try
            {
                while (true)
                {
                    KMemoryInfo info = process.MemoryManager.QueryMemory(cursor);

                    ulong regionAddress = info.Address;
                    ulong regionSize = info.Size;
                    MemoryState regionType = info.State & MemoryState.UserMask;
                    KMemoryPermission permission = info.Permission;

                    KMemoryInfo.Pool.Release(info);

                    if (regionType == MemoryState.Reserved || regionSize == 0)
                    {
                        break;
                    }

                    if (regionType == targetType &&
                        regionSize <= maxRegionSize &&
                        (permission & KMemoryPermission.Read) != 0 &&
                        (permission & KMemoryPermission.Write) != 0)
                    {
                        ulong regionOffset = 0;

                        while (regionOffset < regionSize && scannedBytes < maxTotalBytes)
                        {
                            int length = (int)Math.Min((ulong)ScanChunkSize, regionSize - regionOffset);

                            try
                            {
                                Span<byte> chunk = rented.AsSpan(0, length);
                                process.CpuMemory.Read(regionAddress + regionOffset, chunk);

                                int index = chunk.IndexOf(Magic);
                                if (index >= 0)
                                {
                                    address = regionAddress + regionOffset + (ulong)index;
                                    return true;
                                }
                            }
                            catch (InvalidMemoryRegionException)
                            {
                                // Memory maps can change while scanning. Skip the failed chunk.
                            }

                            regionOffset += (ulong)length;
                            scannedBytes += (ulong)length;
                        }
                    }

                    ulong next = regionAddress + regionSize;
                    if (next <= cursor)
                    {
                        break;
                    }

                    cursor = next;
                }

                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void ResetCache()
        {
            _cachedPid = 0;
            _cachedAddress = 0;
            _nextScanTimestamp = 0;
        }
    }
}
