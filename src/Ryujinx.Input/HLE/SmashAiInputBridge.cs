using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Switch = Ryujinx.HLE.Switch;
using PlayerIndex = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex;

namespace Ryujinx.Input.HLE
{
    /// <summary>
    /// Localhost-only bridge used by the Smash AI trainer.
    /// Supports controller override, observations, and protocol-v2 exact
    /// game-frame gating through the companion SSBU exporter.
    /// </summary>
    internal sealed class SmashAiInputBridge : IDisposable
    {
        private static readonly byte[] Magic = "SAI1"u8.ToArray();

        private const byte MessageAction = 0x01;
        private const byte MessageObservationRequest = 0x02;
        private const byte MessagePing = 0x03;
        private const byte MessageClear = 0x04;
        private const byte MessageFrameGate = 0x05;
        private const byte MessageStepFrames = 0x06;
        private const byte MessageFastMode = 0x07;
        private const byte MessagePresentation = 0x08;

        private const byte MessageObservationResponse = 0x82;
        private const byte MessagePong = 0x83;
        private const byte MessageFrameGateResponse = 0x85;
        private const byte MessageStepFramesResponse = 0x86;
        private const byte MessageFastModeResponse = 0x87;
        private const byte MessagePresentationResponse = 0x88;

        private const int MaxControllers = 9;
        private const int ActionPacketSize = 24;
        private const int GateWatchdogSeconds = 5;

        private readonly object _inputLock = new();
        private readonly GamepadInput[] _inputs = new GamepadInput[MaxControllers];
        private readonly bool[] _enabled = new bool[MaxControllers];

        private readonly Switch _device;
        private readonly UdpClient _udp;
        private readonly Thread _thread;
        private volatile bool _running;
        private bool _gateEnabled;
        private bool _fastModeEnabled;
        private Ryujinx.Common.Configuration.VSyncMode _savedVSyncMode;
        private float _savedVolume;
        private long _lastTrainerPacketTimestamp;

        private SmashAiInputBridge(Switch device, int port)
        {
            _device = device;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _udp.Client.ReceiveTimeout = 500;

            _running = true;
            _lastTrainerPacketTimestamp = Stopwatch.GetTimestamp();
            _thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "SmashAI.Bridge",
            };
            _thread.Start();

            Logger.Info?.Print(LogClass.Hid, $"Smash AI bridge listening on 127.0.0.1:{port}");
        }

        public static SmashAiInputBridge TryCreate(Switch device)
        {
            string portText = Environment.GetEnvironmentVariable("SMASH_AI_PORT");

            if (string.IsNullOrWhiteSpace(portText))
            {
                return null;
            }

            if (!int.TryParse(portText, out int port) || port is < 1024 or > 65535)
            {
                Logger.Warning?.Print(LogClass.Hid, $"Ignoring invalid SMASH_AI_PORT '{portText}'.");
                return null;
            }

            try
            {
                return new SmashAiInputBridge(device, port);
            }
            catch (SocketException exception)
            {
                Logger.Error?.Print(LogClass.Hid, $"Unable to start Smash AI bridge on UDP {port}: {exception.Message}");
                return null;
            }
        }

        public bool TryGetInput(PlayerIndex playerIndex, out GamepadInput input)
        {
            int index = (int)playerIndex;

            if ((uint)index >= MaxControllers)
            {
                input = default;
                return false;
            }

            lock (_inputLock)
            {
                if (!_enabled[index])
                {
                    input = default;
                    return false;
                }

                input = _inputs[index];
                input.PlayerId = playerIndex;
                return true;
            }
        }

        private void ReceiveLoop()
        {
            IPEndPoint remote = new(IPAddress.Loopback, 0);

            while (_running)
            {
                byte[] packet;

                try
                {
                    packet = _udp.Receive(ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
                {
                    MaybeReleaseStaleGate();
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        MaybeReleaseStaleGate();
                        continue;
                    }

                    break;
                }

                if (!IPAddress.IsLoopback(remote.Address) ||
                    packet.Length < 5 ||
                    !packet.AsSpan(0, 4).SequenceEqual(Magic))
                {
                    continue;
                }

                _lastTrainerPacketTimestamp = Stopwatch.GetTimestamp();

                switch (packet[4])
                {
                    case MessageAction:
                        HandleAction(packet);
                        break;
                    case MessageObservationRequest:
                        SendObservation(remote);
                        break;
                    case MessagePing:
                        SendSimple(remote, MessagePong);
                        break;
                    case MessageClear:
                        HandleClear(packet);
                        break;
                    case MessageFrameGate:
                        HandleFrameGate(packet, remote);
                        break;
                    case MessageStepFrames:
                        HandleStepFrames(packet, remote);
                        break;
                    case MessageFastMode:
                        HandleFastMode(packet, remote);
                        break;
                    case MessagePresentation:
                        HandlePresentation(packet, remote);
                        break;
                }
            }
        }

        private void HandleAction(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < ActionPacketSize)
            {
                return;
            }

            int player = packet[5];
            if ((uint)player >= MaxControllers)
            {
                return;
            }

            ControllerKeys buttons = (ControllerKeys)BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(8, 8));
            short lx = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(16, 2));
            short ly = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(18, 2));
            short rx = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(20, 2));
            short ry = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(22, 2));

            GamepadInput input = new()
            {
                PlayerId = (PlayerIndex)player,
                Buttons = buttons,
                LStick = new JoystickPosition { Dx = lx, Dy = ly },
                RStick = new JoystickPosition { Dx = rx, Dy = ry },
            };

            lock (_inputLock)
            {
                _inputs[player] = input;
                _enabled[player] = true;
            }
        }

        private void HandleClear(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 6)
            {
                return;
            }

            int player = packet[5];
            if ((uint)player >= MaxControllers)
            {
                return;
            }

            lock (_inputLock)
            {
                _inputs[player] = default;
                _enabled[player] = false;
            }
        }

        private void HandleFrameGate(ReadOnlySpan<byte> packet, IPEndPoint remote)
        {
            if (packet.Length < 6)
            {
                SendStatus(remote, MessageFrameGateResponse, false);
                return;
            }

            bool enabled = packet[5] != 0;
            bool success = _device.TrySetSmashAiFrameGate(enabled);

            if (success)
            {
                _gateEnabled = enabled;
            }

            SendStatus(remote, MessageFrameGateResponse, success);
        }

        private void HandleStepFrames(ReadOnlySpan<byte> packet, IPEndPoint remote)
        {
            if (packet.Length < 9)
            {
                SendStatus(remote, MessageStepFramesResponse, false);
                return;
            }

            uint frames = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(5, 4));
            bool success = _device.TryStepSmashAiFrames(frames);
            SendStatus(remote, MessageStepFramesResponse, success);
        }

        private void HandleFastMode(ReadOnlySpan<byte> packet, IPEndPoint remote)
        {
            if (packet.Length < 6)
            {
                SendStatus(remote, MessageFastModeResponse, false);
                return;
            }

            bool enabled = packet[5] != 0;

            try
            {
                if (enabled && !_fastModeEnabled)
                {
                    _savedVSyncMode = _device.VSyncMode;
                    _savedVolume = _device.GetVolume();

                    // Unbounded host presentation removes the normal 60 Hz pacing.
                    // The protocol-v2 frame gate still preserves SSBU's exact virtual
                    // frame semantics, so this changes wall-clock speed only.
                    _device.VSyncMode = Ryujinx.Common.Configuration.VSyncMode.Unbounded;
                    _device.UpdateVSyncInterval();
                    _device.Gpu.Renderer.Window?.ChangeVSyncMode(
                        Ryujinx.Common.Configuration.VSyncMode.Unbounded);
                    _device.SetVolume(0f);
                    _fastModeEnabled = true;

                    Logger.Info?.Print(
                        LogClass.Hid,
                        "Smash AI fast mode enabled: unbounded presentation pacing, audio muted.");
                }
                else if (!enabled && _fastModeEnabled)
                {
                    RestoreFastMode();
                }

                SendStatus(remote, MessageFastModeResponse, true);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Hid, $"Unable to change Smash AI fast mode: {exception.Message}");
                SendStatus(remote, MessageFastModeResponse, false);
            }
        }

        private void HandlePresentation(ReadOnlySpan<byte> packet, IPEndPoint remote)
        {
            if (packet.Length < 6)
            {
                SendStatus(remote, MessagePresentationResponse, false);
                return;
            }

            bool enabled = packet[5] != 0;
            _device.SmashAiSuppressPresentation = !enabled;

            Logger.Info?.Print(
                LogClass.Hid,
                enabled
                    ? "Smash AI host presentation enabled."
                    : "Smash AI host presentation suppressed for training.");

            SendStatus(remote, MessagePresentationResponse, true);
        }

        private void RestoreFastMode()
        {
            if (!_fastModeEnabled)
            {
                return;
            }

            _device.VSyncMode = _savedVSyncMode;
            _device.UpdateVSyncInterval();
            _device.Gpu.Renderer.Window?.ChangeVSyncMode(_savedVSyncMode);
            _device.SetVolume(_savedVolume);
            _fastModeEnabled = false;

            Logger.Info?.Print(LogClass.Hid, "Smash AI fast mode disabled; normal presentation pacing restored.");
        }

        private void MaybeReleaseStaleGate()
        {
            if (!_gateEnabled)
            {
                return;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _lastTrainerPacketTimestamp;
            if (elapsedTicks < GateWatchdogSeconds * Stopwatch.Frequency)
            {
                return;
            }

            if (_device.TrySetSmashAiFrameGate(false))
            {
                _gateEnabled = false;
                _device.SmashAiSuppressPresentation = false;
                RestoreFastMode();
                Logger.Warning?.Print(
                    LogClass.Hid,
                    "Smash AI exact-step gate released because the trainer was silent for 5 seconds.");
            }
        }

        private void SendObservation(IPEndPoint remote)
        {
            byte[] state = new byte[16 * 1024];

            bool available = _device.TryReadSmashAiGuestState(state, out int stateLength);
            int payloadLength = available ? stateLength : 0;

            byte[] response = new byte[8 + payloadLength];
            Magic.CopyTo(response, 0);
            response[4] = MessageObservationResponse;
            response[5] = available ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6, 2), (ushort)payloadLength);

            if (payloadLength > 0)
            {
                state.AsSpan(0, payloadLength).CopyTo(response.AsSpan(8));
            }

            try
            {
                _udp.Send(response, response.Length, remote);
            }
            catch (SocketException)
            {
                // The trainer may have gone away between request and response.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void SendSimple(IPEndPoint remote, byte messageType)
        {
            byte[] response = new byte[5];
            Magic.CopyTo(response, 0);
            response[4] = messageType;

            TrySend(response, remote);
        }

        private void SendStatus(IPEndPoint remote, byte messageType, bool success)
        {
            byte[] response = new byte[6];
            Magic.CopyTo(response, 0);
            response[4] = messageType;
            response[5] = success ? (byte)0 : (byte)1;

            TrySend(response, remote);
        }

        private void TrySend(byte[] response, IPEndPoint remote)
        {
            try
            {
                _udp.Send(response, response.Length, remote);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _running = false;

            if (_gateEnabled)
            {
                _device.TrySetSmashAiFrameGate(false);
                _gateEnabled = false;
            }

            _device.SmashAiSuppressPresentation = false;
            RestoreFastMode();

            _udp.Dispose();

            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(1000);
            }

            lock (_inputLock)
            {
                Array.Clear(_enabled);
                Array.Clear(_inputs);
            }
        }
    }
}
