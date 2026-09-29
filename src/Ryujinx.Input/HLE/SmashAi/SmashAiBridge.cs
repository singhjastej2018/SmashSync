using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ryujinx.Input.HLE.SmashAi
{
    /// <summary>
    /// Low-overhead localhost bridge used by the Smash AI trainer.
    /// It is intentionally disabled unless RYUJINX_SMASH_AI_PORT is set.
    /// The protocol is binary UDP so controller injection does not depend on
    /// SDL polling, JSON parsing, or the UI thread.
    /// </summary>
    public sealed class SmashAiBridge : IDisposable
    {
        private const uint Magic = 0x31494153; // "SAI1" little-endian.
        private const int MaxStateSize = 64 * 1024;

        private static readonly byte[] StateMagic = Encoding.ASCII.GetBytes("SSAI_STATE_V1\0\0\0");

        private readonly object _inputLock = new();
        private readonly GamepadInput?[] _overrides = new GamepadInput?[8];
        private readonly Switch _device;
        private readonly UdpClient _udp;
        private readonly Thread _thread;

        private volatile bool _running = true;
        private ulong _stateAddress;
        private int _stateSize;

        public int Port { get; }

        private SmashAiBridge(Switch device, int port)
        {
            _device = device;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;

            _thread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "SmashAI.Bridge",
            };
            _thread.Start();

            Logger.Info?.Print(LogClass.Input, $"Smash AI bridge listening on 127.0.0.1:{Port}");
        }

        public static SmashAiBridge TryCreate(Switch device)
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_SMASH_AI_PORT");

            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!int.TryParse(value, out int port) || port is < 0 or > 65535)
            {
                Logger.Error?.Print(LogClass.Input, $"Invalid RYUJINX_SMASH_AI_PORT '{value}'. Expected 0-65535.");
                return null;
            }

            try
            {
                return new SmashAiBridge(device, port);
            }
            catch (SocketException ex)
            {
                Logger.Error?.Print(LogClass.Input, $"Unable to start Smash AI bridge on port {port}: {ex.Message}");
                return null;
            }
        }

        public bool TryGetOverride(PlayerIndex player, out GamepadInput state)
        {
            int index = (int)player;

            if ((uint)index >= _overrides.Length)
            {
                state = default;
                return false;
            }

            lock (_inputLock)
            {
                GamepadInput? value = _overrides[index];
                if (!value.HasValue)
                {
                    state = default;
                    return false;
                }

                state = value.Value;
                state.PlayerId = player;
                return true;
            }
        }

        private void SetOverride(int player, ReadOnlySpan<byte> packet)
        {
            if ((uint)player >= _overrides.Length || packet.Length < 24)
            {
                return;
            }

            GamepadInput state = new()
            {
                PlayerId = (PlayerIndex)player,
                Buttons = (ControllerKeys)BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(8, 8)),
                LStick = new JoystickPosition
                {
                    Dx = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(16, 2)),
                    Dy = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(18, 2)),
                },
                RStick = new JoystickPosition
                {
                    Dx = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(20, 2)),
                    Dy = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(22, 2)),
                },
            };

            lock (_inputLock)
            {
                _overrides[player] = state;
            }
        }

        private void ClearOverride(int player)
        {
            if ((uint)player >= _overrides.Length)
            {
                return;
            }

            lock (_inputLock)
            {
                _overrides[player] = null;
            }
        }

        private void ServerLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = new(IPAddress.Loopback, 0);
                    byte[] packet = _udp.Receive(ref remote);

                    if (!IPAddress.IsLoopback(remote.Address) || packet.Length < 8)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> span = packet;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
                    {
                        continue;
                    }

                    byte command = span[4];
                    byte player = span[5];

                    switch (command)
                    {
                        case 1: // ping
                            SendStatus(remote, command, 0);
                            break;
                        case 2: // set input
                            if (span.Length < 24)
                            {
                                SendStatus(remote, command, 2);
                                break;
                            }
                            SetOverride(player, span);
                            SendStatus(remote, command, 0);
                            break;
                        case 3: // clear input
                            ClearOverride(player);
                            SendStatus(remote, command, 0);
                            break;
                        case 4: // read state
                            SendState(remote, command);
                            break;
                        case 5: // set known state address
                            if (span.Length < 20)
                            {
                                SendStatus(remote, command, 2);
                                break;
                            }
                            _stateAddress = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
                            _stateSize = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(16, 4)), 0, MaxStateSize);
                            SendStatus(remote, command, 0);
                            break;
                        case 6: // locate SSAI_STATE_V1 exported block
                            LocateState(remote, command);
                            break;
                        case 7: // turbo
                            bool turbo = span.Length >= 9 && span[8] != 0;
                            if (_device.TurboMode != turbo)
                            {
                                _device.ToggleTurbo();
                            }
                            SendStatus(remote, command, 0);
                            break;
                        default:
                            SendStatus(remote, command, 1);
                            break;
                    }
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Input, $"Smash AI bridge error: {ex.Message}");
                }
            }
        }

        private void LocateState(IPEndPoint remote, byte command)
        {
            if (!_device.TryFindActiveApplicationMemory(StateMagic, out ulong address))
            {
                _stateAddress = 0;
                _stateSize = 0;
                SendStatus(remote, command, 3);
                return;
            }

            Span<byte> header = stackalloc byte[24];
            if (!_device.TryReadActiveApplicationMemory(address, header))
            {
                SendStatus(remote, command, 4);
                return;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
            if (size < 24 || size > MaxStateSize)
            {
                SendStatus(remote, command, 5);
                return;
            }

            _stateAddress = address;
            _stateSize = size;
            SendStatus(remote, command, 0, address, size);
        }

        private void SendState(IPEndPoint remote, byte command)
        {
            if (_stateAddress == 0 || _stateSize <= 0)
            {
                SendStatus(remote, command, 3);
                return;
            }

            byte[] response = new byte[12 + _stateSize];
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(0, 4), Magic);
            response[4] = (byte)(command | 0x80);
            response[5] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8, 4), _stateSize);

            if (!_device.TryReadActiveApplicationMemory(_stateAddress, response.AsSpan(12)))
            {
                SendStatus(remote, command, 4);
                return;
            }

            _udp.Send(response, response.Length, remote);
        }

        private void SendStatus(IPEndPoint remote, byte command, byte status, ulong address = 0, int size = 0)
        {
            Span<byte> response = stackalloc byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(response, Magic);
            response[4] = (byte)(command | 0x80);
            response[5] = status;
            BinaryPrimitives.WriteUInt64LittleEndian(response.Slice(8, 8), address);
            BinaryPrimitives.WriteInt32LittleEndian(response.Slice(16, 4), size);
            _udp.Send(response, remote);
        }

        public void Dispose()
        {
            _running = false;
            _udp.Dispose();

            if (_thread.IsAlive && Thread.CurrentThread != _thread)
            {
                _thread.Join(250);
            }
        }
    }
}
