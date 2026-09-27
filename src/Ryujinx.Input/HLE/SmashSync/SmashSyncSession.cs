using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Input.HLE.SmashSync
{
    internal sealed class SmashSyncSession : IDisposable
    {
        private const uint Magic = 0x504E5353; // SSNP
        private const byte Version = 1;
        private const int HeaderSize = 32;
        private const int RecordSize = 32;
        private const int MaxRedundancy = 3;
        private const int ReleaseLeadMs = 120;

        private enum PacketType : byte { Hello = 1, Ready = 2, Start = 3, StartAck = 4, Input = 5, Ping = 6, Pong = 7, Release = 8, ReleaseAck = 9, StateFingerprint = 10 }
        private enum RunState { WaitingForReady, PausingForReady, ReadyBarrier, WaitingForResume, Running, Failed }

        private sealed class ReplayInput
        {
            public long Buttons { get; set; }
            public int Lx { get; set; }
            public int Ly { get; set; }
            public int Rx { get; set; }
            public int Ry { get; set; }

            public static ReplayInput From(GamepadInput input) => new()
            {
                Buttons = (long)input.Buttons,
                Lx = input.LStick.Dx,
                Ly = input.LStick.Dy,
                Rx = input.RStick.Dx,
                Ry = input.RStick.Dy,
            };

            public GamepadInput To(PlayerIndex player) => new()
            {
                PlayerId = player,
                Buttons = (ControllerKeys)Buttons,
                LStick = new JoystickPosition { Dx = Lx, Dy = Ly },
                RStick = new JoystickPosition { Dx = Rx, Dy = Ry },
            };
        }

        private sealed class ReplayFrame
        {
            public ulong Tick { get; set; }
            public ReplayInput P1 { get; set; } = new();
            public ReplayInput P2 { get; set; } = new();
        }

        private readonly SmashSyncConfig _config;
        private readonly SmashSyncMode _mode;
        private readonly object _remoteLock = new();
        private readonly object _sendLock = new();
        private readonly Dictionary<ulong, GamepadInput> _localHistory = [];
        private readonly Dictionary<ulong, GamepadInput> _remoteHistory = [];
        private readonly Dictionary<ulong, ReplayFrame> _replay = [];

        private Socket _socket;
        private EndPoint _peer;
        private Thread _receiver;
        private StreamWriter _record;
        private StreamWriter _log;

        private volatile bool _disposed;
        private volatile bool _peerHello;
        private volatile bool _peerReady;
        private volatile bool _startReceived;
        private volatile bool _startAckReceived;
        private volatile bool _releaseReceived;
        private volatile bool _releaseAckReceived;
        private volatile bool _stateFingerprintReceived;

        private RunState _state;
        private bool _pauseRequested;
        private bool _resumeRequested;
        private bool _ownsPause;
        private long _sessionId;
        private uint _sendInputSequence;
        private uint _sendControlSequence;
        private uint _lastRemoteSequence;
        private ulong _tick;
        private ulong _digest = 14695981039346656037UL;
        private long _lastPingStamp;
        private double _lastRttMs;
        private long _lastHelloSendMs;
        private long _lastReadySendMs;
        private long _lastStartSendMs;
        private long _lastReleaseSendMs;
        private long _lastBarrierPingMs;
        private long _barrierEnteredMs;
        private long _resumeTargetStamp;
        private long _lastRetransmitMs;
        private long _lastStateFingerprintSendMs;
        private ulong _localStateFingerprint;
        private ulong _remoteStateFingerprint;
        private bool _stateFingerprintReady;
        private readonly long _tickInterval;
        private long _nextTickStamp;
        private GamepadInput _lastP1;
        private GamepadInput _lastP2;
        private bool _haveLastCombined;

        private static readonly ControllerKeys ReadyChord =
            ControllerKeys.L | ControllerKeys.R | ControllerKeys.Plus | ControllerKeys.Minus;

        public SmashSyncMode Mode => _mode;
        public bool Enabled => _mode != SmashSyncMode.Off;
        public bool ConfigureTwoPlayers => _config.ConfigureTwoPlayers && (_mode is SmashSyncMode.Replay or SmashSyncMode.Netplay);
        public bool PauseRequested => _pauseRequested;
        public bool ResumeRequested => _resumeRequested;
        public bool OwnsPause => _ownsPause;
        public ulong SharedTick => _tick;
        public bool CanonicalRouting => _mode == SmashSyncMode.Netplay && SmashSyncLobbyService.IsConnected;
        public PlayerIndex CanonicalLocalPlayer => (PlayerIndex)LocalPlayerIndex;
        public PlayerIndex CanonicalRemotePlayer => (PlayerIndex)RemotePlayerIndex;

        private int LocalPlayerIndex => _config.LocalPlayer - 1;
        private int PhysicalPlayerIndex =>
            SmashSyncLobbyService.IsConnected ? LocalPlayerIndex : _config.PhysicalPlayer - 1;
        private int RemotePlayerIndex => LocalPlayerIndex == 0 ? 1 : 0;

        private SmashSyncSession(SmashSyncConfig config)
        {
            _config = config;
            _mode = config.ParsedMode;
            _tickInterval = Math.Max(1, Stopwatch.Frequency / _config.SyncHz);
            _nextTickStamp = Stopwatch.GetTimestamp();
            SmashSyncLobbyService.Initialize();
            bool preGameAccepted = _mode == SmashSyncMode.Netplay && SmashSyncLobbyService.IsConnected;

            _state = preGameAccepted
                ? RunState.PausingForReady
                : _mode == SmashSyncMode.Netplay && config.RequireReadyChord
                    ? RunState.WaitingForReady
                    : RunState.Running;
            _pauseRequested = preGameAccepted;

            OpenLog();

            if (_mode == SmashSyncMode.Record)
            {
                string path = SmashSyncConfig.ResolveDataPath(_config.RecordFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                _record = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            }
            else if (_mode == SmashSyncMode.Replay)
            {
                LoadReplay();
            }
            else if (_mode == SmashSyncMode.Netplay)
            {
                OpenNetwork();
            }

            Log($"mode={_mode} localPlayer={_config.LocalPlayer} syncHz={_config.SyncHz} delay={_config.InputDelayTicks} readyChord={_config.RequireReadyChord} preGameAccepted={preGameAccepted}");
            if (preGameAccepted)
            {
                Log("accepted pre-game lobby detected; automatic game synchronization armed");
            }
        }

        public void AttachDevice(Ryujinx.HLE.Switch device)
        {
            if (_mode != SmashSyncMode.Netplay || device == null)
            {
                return;
            }

            try
            {
                _localStateFingerprint = device.GetActiveApplicationStateFingerprint();
                _stateFingerprintReady = true;
                Log($"local start-state fingerprint=0x{_localStateFingerprint:x16}");
            }
            catch (Exception ex)
            {
                _stateFingerprintReady = false;
                Log($"start-state fingerprint failed: {ex.Message}");
            }
        }

        public static SmashSyncSession TryCreate()
        {
            SmashSyncConfig config = SmashSyncConfig.LoadOrOff();

            if (config.ParsedMode == SmashSyncMode.Off)
            {
                return null;
            }

            try
            {
                return new SmashSyncSession(config);
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Application, $"SmashSync initialization failed: {ex}");
                return null;
            }
        }

        public void PumpControl()
        {
            if (_disposed || _mode != SmashSyncMode.Netplay || _state == RunState.Failed)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (!_peerHello && now - _lastHelloSendMs >= 250)
            {
                SendControl(PacketType.Hello, 0, 0);
                _lastHelloSendMs = now;
            }

            if (_state == RunState.ReadyBarrier)
            {
                if (now - _lastReadySendMs >= 100)
                {
                    SendControl(PacketType.Ready, _sessionId, 0);
                    _lastReadySendMs = now;
                }

                if (_stateFingerprintReady && now - _lastStateFingerprintSendMs >= 100)
                {
                    SendControl(PacketType.StateFingerprint, 0, unchecked((long)_localStateFingerprint));
                    _lastStateFingerprintSendMs = now;
                }

                if (_stateFingerprintReceived && _stateFingerprintReady &&
                    _remoteStateFingerprint != _localStateFingerprint)
                {
                    _state = RunState.Failed;
                    _pauseRequested = false;
                    _resumeRequested = false;
                    Log($"START STATE MISMATCH local=0x{_localStateFingerprint:x16} remote=0x{_remoteStateFingerprint:x16}; keeping guest paused");
                    return;
                }

                // Measure RTT while both guest processes are still frozen. This is
                // used only to align the one-time release barrier, never as gameplay
                // authority.
                if (now - _lastBarrierPingMs >= 50 && _sessionId == 0)
                {
                    SendPing();
                    _lastBarrierPingMs = now;
                }

                if (_peerReady && _stateFingerprintReady && _stateFingerprintReceived)
                {
                    if (LocalPlayerIndex == 0)
                    {
                        // Give the RTT probe a short opportunity to complete before
                        // assigning the gameplay session. Do not let missing ping
                        // diagnostics block startup indefinitely.
                        if (_sessionId == 0 && (_lastRttMs > 0 || now - _barrierEnteredMs >= 250))
                        {
                            _sessionId = DateTime.UtcNow.Ticks ^ Stopwatch.GetTimestamp() ^ Environment.ProcessId;
                            if (_sessionId == 0) _sessionId = 1;
                            Log($"peer READY; leader created session={_sessionId} barrierRttMs={_lastRttMs:F2}");
                        }

                        if (_sessionId != 0 && !_startAckReceived && now - _lastStartSendMs >= 50)
                        {
                            SendControl(PacketType.Start, _sessionId, 0);
                            _lastStartSendMs = now;
                        }

                        if (_startAckReceived)
                        {
                            if (_resumeTargetStamp == 0)
                            {
                                _resumeTargetStamp = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(ReleaseLeadMs);
                                SendControl(PacketType.Release, _sessionId, 0);
                                _lastReleaseSendMs = now;
                                Log($"sent RELEASE session={_sessionId} leadMs={ReleaseLeadMs}");
                            }
                            else if (!_releaseAckReceived && now - _lastReleaseSendMs >= 20)
                            {
                                SendControl(PacketType.Release, _sessionId, 0);
                                _lastReleaseSendMs = now;
                            }

                            if (_releaseAckReceived && Stopwatch.GetTimestamp() >= _resumeTargetStamp)
                            {
                                RequestResumeFromBarrier();
                            }
                        }
                    }
                    else if (_startReceived)
                    {
                        if (now - _lastStartSendMs >= 50)
                        {
                            SendControl(PacketType.StartAck, _sessionId, 0);
                            _lastStartSendMs = now;
                        }

                        if (_releaseReceived && _resumeTargetStamp != 0 && Stopwatch.GetTimestamp() >= _resumeTargetStamp)
                        {
                            RequestResumeFromBarrier();
                        }
                    }
                }
            }
            else if (_state == RunState.Running && IsTickDue() && _tick >= (ulong)_config.InputDelayTicks)
            {
                bool haveRemote;
                lock (_remoteLock)
                {
                    haveRemote = _remoteHistory.ContainsKey(_tick);
                }

                if (!haveRemote)
                {
                    _pauseRequested = true;

                    // If both sides paused because the same UDP input was lost,
                    // there may be no newer packet to carry a redundant copy.
                    // Retransmit this exact logical tick while the barrier is held.
                    if (now - _lastRetransmitMs >= 4 && _localHistory.ContainsKey(_tick))
                    {
                        SendInputs(_tick);
                        _lastRetransmitMs = now;
                    }
                }
                else if (_ownsPause)
                {
                    _pauseRequested = false;
                    _resumeRequested = true;
                }
            }
        }

        public void NotifyPaused()
        {
            if (_disposed)
            {
                return;
            }

            _ownsPause = true;
            _pauseRequested = false;

            if (_state == RunState.PausingForReady)
            {
                _state = RunState.ReadyBarrier;
                _peerReady = false;
                _startReceived = false;
                _startAckReceived = false;
                _releaseReceived = false;
                _releaseAckReceived = false;
                _stateFingerprintReceived = false;
                _remoteStateFingerprint = 0;
                _lastStateFingerprintSendMs = 0;
                _sessionId = 0;
                _lastReadySendMs = 0;
                _lastStartSendMs = 0;
                _lastReleaseSendMs = 0;
                _lastBarrierPingMs = 0;
                _barrierEnteredMs = Environment.TickCount64;
                _resumeTargetStamp = 0;
                _tick = 0;
                _nextTickStamp = Stopwatch.GetTimestamp();
                _lastP1 = Neutral(PlayerIndex.Player1);
                _lastP2 = Neutral(PlayerIndex.Player2);
                _haveLastCombined = true;
                _localHistory.Clear();
                lock (_remoteLock) _remoteHistory.Clear();
                SendControl(PacketType.Ready, 0, 0);
                Log("local READY: emulation paused at synchronization barrier");
            }
        }

        public void NotifyResumed()
        {
            if (_disposed)
            {
                return;
            }

            _ownsPause = false;
            _resumeRequested = false;

            if (_state == RunState.WaitingForResume)
            {
                _state = RunState.Running;
                _tick = 0;
                _localHistory.Clear();
                lock (_remoteLock) _remoteHistory.Clear();
                Log($"shared tick 0 started session={_sessionId}");
            }
        }

        private void RequestResumeFromBarrier()
        {
            if (_state != RunState.ReadyBarrier)
            {
                return;
            }

            _state = RunState.WaitingForResume;
            _pauseRequested = false;
            _resumeRequested = true;
            Log($"barrier complete session={_sessionId}; requesting resume");
        }

        public void ProcessInputs(List<GamepadInput> states, List<SixAxisInput> motion)
        {
            if (_disposed)
            {
                return;
            }

            if (_mode == SmashSyncMode.Netplay && _state == RunState.WaitingForReady)
            {
                NetplayTick(states, motion);
                return;
            }

            if ((_mode is SmashSyncMode.Replay or SmashSyncMode.Netplay) && _haveLastCombined && !IsTickDue())
            {
                ReplaceTwoPlayers(states, _lastP1, _lastP2);
                NeutralizeMotion(motion);
                return;
            }

            if (!IsTickDue())
            {
                return;
            }

            switch (_mode)
            {
                case SmashSyncMode.Record:
                    RecordTick(states);
                    AdvanceTick();
                    break;
                case SmashSyncMode.Replay:
                    ReplayTick(states, motion);
                    AdvanceTick();
                    break;
                case SmashSyncMode.Netplay:
                    NetplayTick(states, motion);
                    break;
            }
        }

        private void NetplayTick(List<GamepadInput> states, List<SixAxisInput> motion)
        {
            PlayerIndex localPlayer = (PlayerIndex)LocalPlayerIndex;
            GamepadInput physicalLocal = GetOrNeutral(states, (PlayerIndex)PhysicalPlayerIndex);
            physicalLocal.PlayerId = localPlayer;

            if (_state == RunState.WaitingForReady)
            {
                if ((physicalLocal.Buttons & ReadyChord) == ReadyChord)
                {
                    // Suppress the synchronization chord from both the configured
                    // physical slot and the canonical local-player slot.
                    SetPlayer(states, Neutral((PlayerIndex)PhysicalPlayerIndex));
                    SetPlayer(states, Neutral(localPlayer));
                    _state = RunState.PausingForReady;
                    _pauseRequested = true;
                    Log("ready chord detected; requesting pause");
                }

                return;
            }

            if (_state != RunState.Running)
            {
                return;
            }

            ulong targetTick = _tick + (ulong)_config.InputDelayTicks;
            physicalLocal.PlayerId = localPlayer;
            _localHistory[targetTick] = physicalLocal;
            SendInputs(targetTick);

            GamepadInput local = _localHistory.TryGetValue(_tick, out GamepadInput scheduled)
                ? scheduled
                : Neutral(localPlayer);

            GamepadInput remote;
            if (_tick < (ulong)_config.InputDelayTicks)
            {
                remote = Neutral((PlayerIndex)RemotePlayerIndex);
            }
            else
            {
                lock (_remoteLock)
                {
                    if (!_remoteHistory.TryGetValue(_tick, out remote))
                    {
                        _pauseRequested = true;
                        if (_haveLastCombined)
                        {
                            ReplaceTwoPlayers(states, _lastP1, _lastP2);
                            NeutralizeMotion(motion);
                        }
                        else
                        {
                            ReplaceTwoPlayers(states, Neutral(PlayerIndex.Player1), Neutral(PlayerIndex.Player2));
                            NeutralizeMotion(motion);
                        }
                        return;
                    }
                }
            }

            GamepadInput p1 = LocalPlayerIndex == 0 ? local : remote;
            GamepadInput p2 = LocalPlayerIndex == 1 ? local : remote;
            p1.PlayerId = PlayerIndex.Player1;
            p2.PlayerId = PlayerIndex.Player2;

            ReplaceTwoPlayers(states, p1, p2);
            NeutralizeMotion(motion);
            _lastP1 = p1;
            _lastP2 = p2;
            _haveLastCombined = true;

            UpdateDigest(p1, p2);
            if ((_tick % 60) == 0)
            {
                SendPing();
                LogDigest($"netplay rttMs={_lastRttMs:F2}");
                Trim();
            }

            AdvanceTick();
        }

        private void RecordTick(List<GamepadInput> states)
        {
            GamepadInput p1 = GetOrNeutral(states, PlayerIndex.Player1);
            GamepadInput p2 = GetOrNeutral(states, PlayerIndex.Player2);

            ReplayFrame frame = new()
            {
                Tick = _tick,
                P1 = ReplayInput.From(p1),
                P2 = ReplayInput.From(p2),
            };

            _record.WriteLine(JsonSerializer.Serialize(frame));

            UpdateDigest(p1, p2);
            if ((_tick % 60) == 0)
            {
                _record.Flush();
                LogDigest("record");
            }
        }

        private void ReplayTick(List<GamepadInput> states, List<SixAxisInput> motion)
        {
            GamepadInput p1 = Neutral(PlayerIndex.Player1);
            GamepadInput p2 = Neutral(PlayerIndex.Player2);

            if (_replay.TryGetValue(_tick, out ReplayFrame frame))
            {
                p1 = frame.P1.To(PlayerIndex.Player1);
                p2 = frame.P2.To(PlayerIndex.Player2);
            }

            ReplaceTwoPlayers(states, p1, p2);
            NeutralizeMotion(motion);
            _lastP1 = p1;
            _lastP2 = p2;
            _haveLastCombined = true;

            UpdateDigest(p1, p2);
            if ((_tick % 60) == 0)
            {
                LogDigest("replay");
            }
        }

        private static long MillisecondsToStopwatchTicks(double milliseconds) =>
            (long)(milliseconds * Stopwatch.Frequency / 1000.0);

        private bool IsTickDue() => Stopwatch.GetTimestamp() >= _nextTickStamp;

        private void AdvanceTick()
        {
            _tick++;
            _nextTickStamp = Stopwatch.GetTimestamp() + _tickInterval;
        }

        private void OpenNetwork()
        {
            if (string.IsNullOrWhiteSpace(_config.PeerAddress))
            {
                throw new InvalidOperationException("PeerAddress is required in Netplay mode.");
            }

            IPAddress address = IPAddress.TryParse(_config.PeerAddress, out IPAddress parsed)
                ? parsed
                : Dns.GetHostAddresses(_config.PeerAddress).First(x => x.AddressFamily == AddressFamily.InterNetwork);

            _peer = new IPEndPoint(address, _config.PeerPort);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 1000,
                ReceiveBufferSize = 65536,
                SendBufferSize = 65536,
            };
            _socket.Bind(new IPEndPoint(IPAddress.Any, _config.LocalPort));

            _receiver = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "SmashSync.UDP",
                Priority = ThreadPriority.AboveNormal,
            };
            _receiver.Start();

            Log($"udp local={_config.LocalPort} peer={_peer}");
        }

        private void ReceiveLoop()
        {
            byte[] packet = new byte[HeaderSize + RecordSize * MaxRedundancy];
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

            while (!_disposed)
            {
                try
                {
                    int length = _socket.ReceiveFrom(packet, 0, packet.Length, SocketFlags.None, ref sender);
                    if (length < HeaderSize || !PeerMatches(sender))
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> data = packet.AsSpan(0, length);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic || data[4] != Version)
                    {
                        continue;
                    }

                    PacketType type = (PacketType)data[5];
                    int player = data[6];
                    int count = data[7];
                    long session = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
                    uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
                    long stamp = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);

                    switch (type)
                    {
                        case PacketType.Hello:
                            if (!_peerHello) Log($"received HELLO from P{player + 1}");
                            _peerHello = true;
                            break;
                        case PacketType.Ready:
                            if (!_peerReady) Log($"received READY from P{player + 1}");
                            _peerReady = true;
                            break;
                        case PacketType.Start:
                            if (LocalPlayerIndex == 1 && session != 0)
                            {
                                _sessionId = session;
                                if (!_startReceived) Log($"received START session={session}");
                                _startReceived = true;
                            }
                            break;
                        case PacketType.StartAck:
                            if (LocalPlayerIndex == 0 && session == _sessionId)
                            {
                                if (!_startAckReceived) Log($"received START_ACK session={session}");
                                _startAckReceived = true;
                            }
                            break;
                        case PacketType.Release:
                            if (LocalPlayerIndex == 1 && session == _sessionId)
                            {
                                if (!_releaseReceived)
                                {
                                    double oneWayMs = Math.Clamp(_lastRttMs * 0.5, 0.0, ReleaseLeadMs - 20.0);
                                    double remainingMs = Math.Max(20.0, ReleaseLeadMs - oneWayMs);
                                    _resumeTargetStamp = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(remainingMs);
                                    _releaseReceived = true;
                                    Log($"received RELEASE session={session} rttMs={_lastRttMs:F2} resumeInMs={remainingMs:F2}");
                                }

                                SendControl(PacketType.ReleaseAck, session, 0);
                            }
                            break;
                        case PacketType.ReleaseAck:
                            if (LocalPlayerIndex == 0 && session == _sessionId)
                            {
                                if (!_releaseAckReceived) Log($"received RELEASE_ACK session={session}");
                                _releaseAckReceived = true;
                            }
                            break;
                        case PacketType.StateFingerprint:
                            _remoteStateFingerprint = unchecked((ulong)stamp);
                            if (!_stateFingerprintReceived)
                            {
                                Log($"received start-state fingerprint=0x{_remoteStateFingerprint:x16}");
                            }
                            _stateFingerprintReceived = true;
                            break;
                        case PacketType.Input:
                            if (session == _sessionId && player == RemotePlayerIndex)
                            {
                                ReadInputs(data, count, sequence);
                            }
                            break;
                        case PacketType.Ping:
                            if (session == _sessionId) SendControl(PacketType.Pong, session, stamp);
                            break;
                        case PacketType.Pong:
                            if (session == _sessionId && stamp == _lastPingStamp && stamp != 0)
                            {
                                _lastRttMs = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
                            }
                            break;
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.Interrupted or SocketError.OperationAborted or SocketError.ConnectionReset) { }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!_disposed) Log($"receive error: {ex.Message}");
                }
            }
        }

        private bool PeerMatches(EndPoint sender) =>
            sender is IPEndPoint actual &&
            _peer is IPEndPoint expected &&
            actual.Port == expected.Port &&
            actual.Address.Equals(expected.Address);

        private void ReadInputs(ReadOnlySpan<byte> data, int count, uint sequence)
        {
            int records = Math.Min(Math.Clamp(count, 0, MaxRedundancy), (data.Length - HeaderSize) / RecordSize);

            if (_lastRemoteSequence != 0 && sequence > _lastRemoteSequence + 1)
            {
                Log($"packet gap previous={_lastRemoteSequence} current={sequence}");
            }
            if (sequence > _lastRemoteSequence) _lastRemoteSequence = sequence;

            lock (_remoteLock)
            {
                for (int i = 0; i < records; i++)
                {
                    int o = HeaderSize + i * RecordSize;
                    ulong tick = BinaryPrimitives.ReadUInt64LittleEndian(data[o..]);
                    GamepadInput remoteInput = new()
                    {
                        PlayerId = (PlayerIndex)RemotePlayerIndex,
                        Buttons = (ControllerKeys)BinaryPrimitives.ReadInt64LittleEndian(data[(o + 8)..]),
                        LStick = new JoystickPosition
                        {
                            Dx = BinaryPrimitives.ReadInt32LittleEndian(data[(o + 16)..]),
                            Dy = BinaryPrimitives.ReadInt32LittleEndian(data[(o + 20)..]),
                        },
                        RStick = new JoystickPosition
                        {
                            Dx = BinaryPrimitives.ReadInt32LittleEndian(data[(o + 24)..]),
                            Dy = BinaryPrimitives.ReadInt32LittleEndian(data[(o + 28)..]),
                        },
                    };
                    _remoteHistory[tick] = remoteInput;
                    if (i == 0)
                    {
                        SmashSyncLobbyService.UpdateRemoteInput(remoteInput);
                    }
                }
            }
        }

        private void SendInputs(ulong newestTick)
        {
            byte[] packet = new byte[HeaderSize + RecordSize * MaxRedundancy];
            Span<byte> data = packet;
            uint sequence = unchecked(++_sendInputSequence);
            WriteHeader(data, PacketType.Input, (byte)LocalPlayerIndex, 0, _sessionId, sequence, 0);

            int count = 0;
            for (int i = 0; i < _config.Redundancy && i < MaxRedundancy; i++)
            {
                if (newestTick < (ulong)i) break;
                ulong tick = newestTick - (ulong)i;
                if (!_localHistory.TryGetValue(tick, out GamepadInput input)) continue;

                int o = HeaderSize + count * RecordSize;
                BinaryPrimitives.WriteUInt64LittleEndian(data[o..], tick);
                BinaryPrimitives.WriteInt64LittleEndian(data[(o + 8)..], (long)input.Buttons);
                BinaryPrimitives.WriteInt32LittleEndian(data[(o + 16)..], input.LStick.Dx);
                BinaryPrimitives.WriteInt32LittleEndian(data[(o + 20)..], input.LStick.Dy);
                BinaryPrimitives.WriteInt32LittleEndian(data[(o + 24)..], input.RStick.Dx);
                BinaryPrimitives.WriteInt32LittleEndian(data[(o + 28)..], input.RStick.Dy);
                count++;
            }

            data[7] = (byte)count;
            Send(packet, HeaderSize + count * RecordSize);
        }

        private void SendPing()
        {
            _lastPingStamp = Stopwatch.GetTimestamp();
            SendControl(PacketType.Ping, _sessionId, _lastPingStamp);
        }

        private void SendControl(PacketType type, long session, long stamp)
        {
            byte[] packet = new byte[HeaderSize];
            WriteHeader(packet, type, (byte)LocalPlayerIndex, 0, session, unchecked(++_sendControlSequence), stamp);
            Send(packet, packet.Length);
        }

        private static void WriteHeader(Span<byte> data, PacketType type, byte player, byte count, long session, uint sequence, long stamp)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
            data[4] = Version;
            data[5] = (byte)type;
            data[6] = player;
            data[7] = count;
            BinaryPrimitives.WriteInt64LittleEndian(data[8..], session);
            BinaryPrimitives.WriteUInt32LittleEndian(data[16..], sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(data[20..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(data[24..], stamp);
        }

        private void Send(byte[] packet, int length)
        {
            if (_disposed || _socket == null || _peer == null) return;
            try
            {
                lock (_sendLock) _socket.SendTo(packet, 0, length, SocketFlags.None, _peer);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (!_disposed) Log($"send error: {ex.Message}");
            }
        }

        private void LoadReplay()
        {
            string path = SmashSyncConfig.ResolveDataPath(_config.ReplayFile);
            if (!File.Exists(path)) throw new FileNotFoundException("Replay file not found.", path);

            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ReplayFrame frame = JsonSerializer.Deserialize<ReplayFrame>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (frame != null) _replay[frame.Tick] = frame;
            }

            Log($"loaded replay frames={_replay.Count} path={path}");
        }

        private void OpenLog()
        {
            try
            {
                string path = SmashSyncConfig.ResolveDataPath(_config.LogFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
            catch { }
        }

        private void Log(string message)
        {
            Logger.Info?.PrintMsg(LogClass.Application, $"SmashSync: {message}");
            try { _log?.WriteLine($"{DateTime.UtcNow:O} {message}"); } catch { }
        }

        private static GamepadInput GetOrNeutral(List<GamepadInput> states, PlayerIndex player)
        {
            for (int i = 0; i < states.Count; i++) if (states[i].PlayerId == player) return states[i];
            return Neutral(player);
        }

        private static GamepadInput Neutral(PlayerIndex player) => new() { PlayerId = player };

        private static void SetPlayer(List<GamepadInput> states, GamepadInput input)
        {
            int index = states.FindIndex(x => x.PlayerId == input.PlayerId);
            if (index >= 0) states[index] = input;
            else states.Add(input);
        }

        private static void ReplaceTwoPlayers(List<GamepadInput> states, GamepadInput p1, GamepadInput p2)
        {
            states.RemoveAll(x => x.PlayerId is PlayerIndex.Player1 or PlayerIndex.Player2);
            states.Add(p1);
            states.Add(p2);
        }

        private static void NeutralizeMotion(List<SixAxisInput> states)
        {
            states.RemoveAll(x => x.PlayerId is PlayerIndex.Player1 or PlayerIndex.Player2);
            states.Add(new SixAxisInput { PlayerId = PlayerIndex.Player1, Orientation = new float[9] });
            states.Add(new SixAxisInput { PlayerId = PlayerIndex.Player2, Orientation = new float[9] });
        }

        private void UpdateDigest(GamepadInput p1, GamepadInput p2)
        {
            Hash(_tick);
            HashInput(p1);
            HashInput(p2);
        }

        private void LogDigest(string context)
        {
            Log($"tick={_tick} inputDigest=0x{_digest:X16} {context}");
        }

        private void HashInput(GamepadInput input)
        {
            Hash((ulong)input.Buttons);
            Hash(unchecked((uint)input.LStick.Dx));
            Hash(unchecked((uint)input.LStick.Dy));
            Hash(unchecked((uint)input.RStick.Dx));
            Hash(unchecked((uint)input.RStick.Dy));
        }

        private void Hash(ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                _digest ^= (byte)value;
                _digest *= 1099511628211UL;
                value >>= 8;
            }
        }

        private void Trim()
        {
            ulong keep = _tick > 240 ? _tick - 240 : 0;
            foreach (ulong key in _localHistory.Keys.Where(x => x < keep).ToArray()) _localHistory.Remove(key);
            lock (_remoteLock)
            {
                foreach (ulong key in _remoteHistory.Keys.Where(x => x < keep).ToArray()) _remoteHistory.Remove(key);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _socket?.Close(); } catch { }
            try { _receiver?.Join(500); } catch { }
            _record?.Flush();
            _record?.Dispose();
            _log?.Dispose();
        }
    }
}
