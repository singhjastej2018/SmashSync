using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.Memory;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Ryujinx.HLE.SmashAi
{
    /// <summary>
    /// Discovers and reads the fixed Smash AI observation block exported by the
    /// companion SSBU plugin. Discovery is intentionally host-side so the trainer
    /// never needs version-specific guest addresses.
    /// </summary>
    internal sealed class SmashAiGuestStateBridge
    {
        private static readonly byte[] Magic = "SSAI0001"u8.ToArray();

        public const int HeaderSize = 24;
        public const int MaxStateSize = 16 * 1024;

        private const int ScanChunkSize = 64 * 1024;
        private const ulong MaxBytesPerRegionType = 64UL * 1024 * 1024;

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

            ProcessResult activeApplication = _device.Processes.ActiveApplication;
            if (activeApplication == null ||
                !_device.System.KernelContext.Processes.TryGetValue(activeApplication.ProcessId, out KProcess process))
            {
                ResetCache();
                return false;
            }

            if (_cachedPid != process.Pid)
            {
                ResetCache();
                _cachedPid = process.Pid;
            }

            if (_cachedAddress != 0 && TryReadAt(process, _cachedAddress, destination, out bytesWritten))
            {
                return true;
            }

            _cachedAddress = 0;

            long now = Stopwatch.GetTimestamp();
            if (now < _nextScanTimestamp)
            {
                return false;
            }

            _nextScanTimestamp = now + (2 * Stopwatch.Frequency);

            // Skyline/exlaunch module writable data should normally be ModCodeMutable.
            // CodeMutable is retained as a fallback for alternate loaders.
            if (TryFindMagic(process, MemoryState.ModCodeMutable, out ulong address) ||
                TryFindMagic(process, MemoryState.CodeMutable, out address))
            {
                _cachedAddress = address;
                return TryReadAt(process, address, destination, out bytesWritten);
            }

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

                if (protocolVersion != 1 ||
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

        private static bool TryFindMagic(KProcess process, MemoryState targetType, out ulong address)
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

                    if (regionType == MemoryState.Reserved)
                    {
                        break;
                    }

                    if (regionSize == 0)
                    {
                        break;
                    }

                    if (regionType == targetType &&
                        (permission & KMemoryPermission.Read) != 0 &&
                        (permission & KMemoryPermission.Write) != 0)
                    {
                        ulong regionOffset = 0;

                        while (regionOffset < regionSize && scannedBytes < MaxBytesPerRegionType)
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
