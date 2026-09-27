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
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Input.HLE.SmashSync
{
    internal sealed class SmashSyncSession : IDisposable
    {
        private const uint Magic = 0x504E5353; // SSNP
        private const byte Version = 3;
        private const int HeaderSize = 32;
        private const int RecordSize = 32;
        private const int MaxRedundancy = 3;
        private const int ClockSyncMinSamples = 6;
        private const int ClockSyncMaxWaitMs = 750;
        private const int ClockSyncIntervalMs = 20;
        private const int EpochLeadMs = 500;
        private const ulong MaxRemoteTickLead = 512;
        private const int ReleaseMinLeadMs = 10;
        private const int ClockSyncPacketSize = HeaderSize + 8;
        private const int EpochPacketSize = HeaderSize + 8;

        private enum PacketType : byte
        {
            Hello = 1,
            Ready = 2,
            Start = 3,
            StartAck = 4,
            Input = 5,
            Ping = 6,
            Pong = 7,
            Release = 8,
            ReleaseAck = 9,
            StateFingerprint = 10,
            ClockSyncRequest = 11,
            ClockSyncResponse = 12,
            Epoch = 13,
            EpochAck = 14,
        }
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
        private readonly object _clockLock = new();
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
        private volatile bool _epochReceived;
        private volatile bool _epochAckReceived;

        private volatile RunState _state;
        private volatile bool _pauseRequested;
        private volatile bool _resumeRequested;
        private volatile bool _ownsPause;
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
        private long _barrierEnteredMs;
        private long _lastRetransmitMs;
        private long _lockstepStallStartedMs;
        private long _lastClockSyncSendMs;
        private long _clockSyncStartedMs;
        private long _lastEpochSendMs;
        private long _clockBestRttNs = long.MaxValue;
        private long _clockOffsetNs;
        private int _clockSamples;
        private long _sharedEpochP1Ns;
        private long _localEpochNs;
        private long _lastStateFingerprintSendMs;
        private ulong _localStateFingerprint;
        private ulong _remoteStateFingerprint;
        private bool _stateFingerprintReady;
        private readonly long _lobbyToken;
        private readonly long _tickInterval;
        private readonly long _tickIntervalNs;
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
        public bool CanAdoptExistingPause => _state == RunState.PausingForReady;
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
            _tickIntervalNs = Math.Max(1, 1_000_000_000L / _config.SyncHz);
            _nextTickStamp = Stopwatch.GetTimestamp();
            SmashSyncLobbyService.Initialize();
            _lobbyToken = SmashSyncLobbyService.ConnectionToken;
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

            if (device.Processes?.ActiveApplication == null || device.Processes.ActiveApplication.ProgramId == 0)
            {
                _stateFingerprintReady = false;
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
                SendControl(PacketType.Hello, _lobbyToken, 0);
                _lastHelloSendMs = now;
            }

            if (_state == RunState.ReadyBarrier)
            {
                if (_barrierEnteredMs != 0 && now - _barrierEnteredMs > _config.HandshakeTimeoutMs)
                {
                    FailSession($"startup barrier timed out after {_config.HandshakeTimeoutMs} ms; keeping guest paused");
                    return;
                }

                if (now - _lastReadySendMs >= 100)
                {
                    SendControl(PacketType.Ready, _lobbyToken, 0);
                    _lastReadySendMs = now;
                }

                if (_stateFingerprintReady && now - _lastStateFingerprintSendMs >= 100)
                {
                    SendControl(PacketType.StateFingerprint, _lobbyToken, unchecked((long)_localStateFingerprint));
                    _lastStateFingerprintSendMs = now;
                }

                if (_stateFingerprintReceived && _stateFingerprintReady &&
                    _remoteStateFingerprint != _localStateFingerprint)
                {
                    FailSession($"START STATE MISMATCH local=0x{_localStateFingerprint:x16} remote=0x{_remoteStateFingerprint:x16}; keeping guest paused");
                    return;
                }

                if (_peerReady && _stateFingerprintReady && _stateFingerprintReceived)
                {
                    if (LocalPlayerIndex == 0)
                    {
                        if (_clockSyncStartedMs == 0)
                        {
                            _clockSyncStartedMs = now;
                            Log("starting P1-authoritative monotonic clock synchronization");
                        }

                        if (now - _lastClockSyncSendMs >= ClockSyncIntervalMs)
                        {
                            long t1Ns = MonotonicNowNs();
                            SendControl(PacketType.ClockSyncRequest, t1Ns, 0);
                            _lastClockSyncSendMs = now;
                        }

                        int samples;
                        long bestRttNs;
                        long offsetNs;
                        lock (_clockLock)
                        {
                            samples = _clockSamples;
                            bestRttNs = _clockBestRttNs;
                            offsetNs = _clockOffsetNs;
                        }

                        bool clockReady =
                            samples >= ClockSyncMinSamples ||
                            (samples > 0 && now - _clockSyncStartedMs >= ClockSyncMaxWaitMs);

                        if (_sessionId == 0 && clockReady)
                        {
                            _sessionId = CreateSessionId();

                            double bestRttMs = bestRttNs == long.MaxValue ? 0 : bestRttNs / 1_000_000.0;
                            Log($"shared clock locked samples={samples} bestRttMs={bestRttMs:F3} p2MinusP1Ms={offsetNs / 1_000_000.0:F3}; session={_sessionId}");
                        }

                        if (_sessionId != 0 && !_startAckReceived && now - _lastStartSendMs >= 50)
                        {
                            SendControl(PacketType.Start, _sessionId, 0);
                            _lastStartSendMs = now;
                        }

                        if (_startAckReceived)
                        {
                            if (_sharedEpochP1Ns == 0)
                            {
                                lock (_clockLock)
                                {
                                    offsetNs = _clockOffsetNs;
                                }

                                _sharedEpochP1Ns = MonotonicNowNs() + EpochLeadMs * 1_000_000L;
                                _localEpochNs = _sharedEpochP1Ns;
                                SendEpoch(_sharedEpochP1Ns, offsetNs);
                                _lastEpochSendMs = now;
                                Log($"scheduled shared epoch p1Ns={_sharedEpochP1Ns} leadMs={EpochLeadMs} p2MinusP1Ms={offsetNs / 1_000_000.0:F3}");
                            }
                            else if (!_epochAckReceived && now - _lastEpochSendMs >= 20)
                            {
                                lock (_clockLock)
                                {
                                    offsetNs = _clockOffsetNs;
                                }

                                SendEpoch(_sharedEpochP1Ns, offsetNs);
                                _lastEpochSendMs = now;
                            }

                            if (_epochAckReceived)
                            {
                                if (!_releaseAckReceived && now - _lastReleaseSendMs >= 20)
                                {
                                    SendControl(PacketType.Release, _sessionId, _sharedEpochP1Ns);
                                    _lastReleaseSendMs = now;
                                }

                                if (_releaseAckReceived)
                                {
                                    RequestResumeFromBarrier();
                                }
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

                        if (_epochReceived && _releaseReceived)
                        {
                            RequestResumeFromBarrier();
                        }
                    }
                }
            }
            else if (_state == RunState.Running && IsTickDue() && _tick >= (ulong)_config.InputDelayTicks)
            {
                // Never stall before the local input for this logical tick has been
                // captured and transmitted. This is required for zero-delay mode:
                // otherwise both peers can pause on tick 0 before either sends it.
                bool haveLocal = _localHistory.ContainsKey(_tick);
                if (!haveLocal)
                {
                    return;
                }

                bool haveRemote;
                lock (_remoteLock)
                {
                    haveRemote = _remoteHistory.ContainsKey(_tick);
                }

                if (!haveRemote)
                {
                    if (_lockstepStallStartedMs == 0)
                    {
                        _lockstepStallStartedMs = now;
                    }

                    if (now - _lockstepStallStartedMs > _config.LockstepTimeoutMs)
                    {
                        FailSession($"remote input tick {_tick} timed out after {_config.LockstepTimeoutMs} ms");
                        return;
                    }

                    _pauseRequested = true;

                    // If both sides paused because the same UDP input was lost,
                    // there may be no newer packet to carry a redundant copy.
                    // Retransmit this exact immutable logical tick while held.
                    if (now - _lastRetransmitMs >= 4)
                    {
                        SendInputs(_tick);
                        _lastRetransmitMs = now;
                    }
                }
                else
                {
                    _lockstepStallStartedMs = 0;
                    if (_ownsPause)
                    {
                        _pauseRequested = false;
                        _resumeRequested = true;
                    }
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
                _epochReceived = false;
                _epochAckReceived = false;
                _stateFingerprintReceived = false;
                _remoteStateFingerprint = 0;
                _lastStateFingerprintSendMs = 0;
                _sessionId = 0;
                _lastReadySendMs = 0;
                _lastStartSendMs = 0;
                _lastReleaseSendMs = 0;
                _lastClockSyncSendMs = 0;
                _clockSyncStartedMs = 0;
                _lastEpochSendMs = 0;
                _barrierEnteredMs = Environment.TickCount64;
                _lockstepStallStartedMs = 0;
                _sharedEpochP1Ns = 0;
                _localEpochNs = 0;
                lock (_clockLock)
                {
                    _clockSamples = 0;
                    _clockBestRttNs = long.MaxValue;
                    _clockOffsetNs = 0;
                }
                _tick = 0;
                _nextTickStamp = Stopwatch.GetTimestamp();
                _lastP1 = Neutral(PlayerIndex.Player1);
                _lastP2 = Neutral(PlayerIndex.Player2);
                _haveLastCombined = true;
                _localHistory.Clear();
                lock (_remoteLock) _remoteHistory.Clear();
                SendControl(PacketType.Ready, _lobbyToken, 0);
                Log("local READY: emulation paused at synchronization barrier");
            }
        }

        public void WaitForResumeEpoch()
        {
            long targetNs = _localEpochNs;
            if (targetNs <= 0)
            {
                return;
            }

            while (!_disposed)
            {
                long remainingNs = targetNs - MonotonicNowNs();
                if (remainingNs <= 0)
                {
                    return;
                }

                if (remainingNs > 2_000_000)
                {
                    int sleepMs = (int)Math.Clamp(remainingNs / 1_000_000 - 1, 1, 10);
                    Thread.Sleep(sleepMs);
                }
                else
                {
                    Thread.SpinWait(128);
                }
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
                _lockstepStallStartedMs = 0;
                _localHistory.Clear();
                lock (_remoteLock) _remoteHistory.Clear();

                double epochErrorMs = _localEpochNs > 0
                    ? (MonotonicNowNs() - _localEpochNs) / 1_000_000.0
                    : 0;

                Log($"shared tick 0 started session={_sessionId} epochErrorMs={epochErrorMs:F3}");
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

        public bool ProcessInputs(List<GamepadInput> states, List<SixAxisInput> motion)
        {
            if (_disposed)
            {
                return true;
            }

            if (_mode == SmashSyncMode.Netplay && _state == RunState.WaitingForReady)
            {
                return NetplayTick(states, motion);
            }

            if ((_mode is SmashSyncMode.Replay or SmashSyncMode.Netplay) && _haveLastCombined && !IsTickDue())
            {
                ReplaceTwoPlayers(states, _lastP1, _lastP2);
                NeutralizeMotion(motion);
                return false;
            }

            if (!IsTickDue())
            {
                return _mode == SmashSyncMode.Record;
            }

            switch (_mode)
            {
                case SmashSyncMode.Record:
                    RecordTick(states);
                    AdvanceTick();
                    return true;
                case SmashSyncMode.Replay:
                    ReplayTick(states, motion);
                    AdvanceTick();
                    return true;
                case SmashSyncMode.Netplay:
                    return NetplayTick(states, motion);
                default:
                    return true;
            }
        }

        private bool NetplayTick(List<GamepadInput> states, List<SixAxisInput> motion)
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

                return true;
            }

            if (_state != RunState.Running)
            {
                return false;
            }

            ulong targetTick = _tick + (ulong)_config.InputDelayTicks;
            physicalLocal.PlayerId = localPlayer;

            // A logical input becomes immutable the first time it is assigned.
            // Host-loop retries during a network stall must never rewrite a tick
            // that may already have reached the peer.
            if (!_localHistory.TryGetValue(targetTick, out GamepadInput frozenLocal))
            {
                _localHistory[targetTick] = physicalLocal;
            }
            else
            {
                physicalLocal = frozenLocal;
            }

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
                        if (_lockstepStallStartedMs == 0)
                        {
                            _lockstepStallStartedMs = Environment.TickCount64;
                        }
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
                        return false;
                    }
                }
            }

            _lockstepStallStartedMs = 0;

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
            return true;
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

        private static long MonotonicNowNs()
        {
            long ticks = Stopwatch.GetTimestamp();
            long seconds = ticks / Stopwatch.Frequency;
            long remainder = ticks % Stopwatch.Frequency;
            return seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
        }

        private bool IsTickDue()
        {
            if (_mode == SmashSyncMode.Netplay && _localEpochNs > 0)
            {
                if (_tick > (ulong)(long.MaxValue / Math.Max(1, _tickIntervalNs)))
                {
                    return true;
                }

                long dueNs = _localEpochNs + (long)_tick * _tickIntervalNs;
                return MonotonicNowNs() >= dueNs;
            }

            return Stopwatch.GetTimestamp() >= _nextTickStamp;
        }

        private void AdvanceTick()
        {
            _tick++;

            if (_mode != SmashSyncMode.Netplay || _localEpochNs <= 0)
            {
                _nextTickStamp = Stopwatch.GetTimestamp() + _tickInterval;
            }
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
                            if (session == _lobbyToken && player == RemotePlayerIndex)
                            {
                                if (!_peerHello) Log($"received HELLO from P{player + 1}");
                                _peerHello = true;
                            }
                            break;
                        case PacketType.Ready:
                            if (session == _lobbyToken && player == RemotePlayerIndex)
                            {
                                if (!_peerReady) Log($"received READY from P{player + 1}");
                                _peerReady = true;
                            }
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
                        case PacketType.ClockSyncRequest:
                            if (LocalPlayerIndex == 1 && session > 0)
                            {
                                long p2ReceiveNs = MonotonicNowNs();
                                SendClockSyncResponse(session, p2ReceiveNs);
                            }
                            break;
                        case PacketType.ClockSyncResponse:
                            if (LocalPlayerIndex == 0 && session > 0 && stamp > 0 && length >= ClockSyncPacketSize)
                            {
                                long p1ReceiveNs = MonotonicNowNs();
                                long p2SendNs = BinaryPrimitives.ReadInt64LittleEndian(data[HeaderSize..]);

                                long remoteProcessingNs = Math.Max(0, p2SendNs - stamp);
                                long rttNs = (p1ReceiveNs - session) - remoteProcessingNs;

                                if (rttNs > 0 && rttNs < 2_000_000_000L)
                                {
                                    // Standard four-timestamp clock offset:
                                    // ((t2 - t1) + (t3 - t4)) / 2.
                                    long offsetNs = ((stamp - session) + (p2SendNs - p1ReceiveNs)) / 2;

                                    lock (_clockLock)
                                    {
                                        _clockSamples++;
                                        if (rttNs < _clockBestRttNs)
                                        {
                                            _clockBestRttNs = rttNs;
                                            _clockOffsetNs = offsetNs;
                                        }
                                    }
                                }
                            }
                            break;
                        case PacketType.Epoch:
                            if (LocalPlayerIndex == 1 && session == _sessionId && length >= EpochPacketSize)
                            {
                                long p2MinusP1Ns = BinaryPrimitives.ReadInt64LittleEndian(data[HeaderSize..]);
                                long localTargetNs = stamp + p2MinusP1Ns;
                                long remainingNs = localTargetNs - MonotonicNowNs();

                                if (remainingNs >= 20_000_000L)
                                {
                                    _sharedEpochP1Ns = stamp;
                                    _localEpochNs = localTargetNs;
                                    _epochReceived = true;
                                    SendControl(PacketType.EpochAck, session, stamp);
                                    Log($"received shared epoch p1Ns={stamp} localNs={localTargetNs} resumeInMs={remainingNs / 1_000_000.0:F3}");
                                }
                                else
                                {
                                    SendControl(PacketType.EpochAck, session, 0);
                                    Log($"rejected late shared epoch remainingMs={remainingNs / 1_000_000.0:F3}");
                                }
                            }
                            break;
                        case PacketType.EpochAck:
                            if (LocalPlayerIndex == 0 && session == _sessionId)
                            {
                                if (stamp == _sharedEpochP1Ns && stamp != 0)
                                {
                                    if (!_epochAckReceived)
                                    {
                                        Log($"received shared epoch ACK p1Ns={stamp}");
                                    }
                                    _epochAckReceived = true;
                                }
                                else if (stamp == 0)
                                {
                                    _epochAckReceived = false;
                                    _sharedEpochP1Ns = 0;
                                    _localEpochNs = 0;
                                    Log("peer rejected late epoch; scheduling a new shared epoch");
                                }
                            }
                            break;
                        case PacketType.Release:
                            if (LocalPlayerIndex == 1 &&
                                session == _sessionId &&
                                stamp == _sharedEpochP1Ns &&
                                _epochReceived)
                            {
                                long remainingNs = _localEpochNs - MonotonicNowNs();
                                if (remainingNs >= ReleaseMinLeadMs * 1_000_000L)
                                {
                                    _releaseReceived = true;
                                    SendControl(PacketType.ReleaseAck, session, stamp);
                                    Log($"received RELEASE for shared epoch; resumeInMs={remainingNs / 1_000_000.0:F3}");
                                }
                                else
                                {
                                    SendControl(PacketType.ReleaseAck, session, 0);
                                    Log($"rejected late RELEASE remainingMs={remainingNs / 1_000_000.0:F3}");
                                }
                            }
                            break;
                        case PacketType.ReleaseAck:
                            if (LocalPlayerIndex == 0 && session == _sessionId)
                            {
                                if (stamp == _sharedEpochP1Ns && stamp != 0)
                                {
                                    _releaseAckReceived = true;
                                }
                                else if (stamp == 0)
                                {
                                    _releaseAckReceived = false;
                                    _epochAckReceived = false;
                                    _sharedEpochP1Ns = 0;
                                    _localEpochNs = 0;
                                    _lastEpochSendMs = 0;
                                    Log("peer rejected late RELEASE; scheduling a new shared epoch");
                                }
                            }
                            break;
                        case PacketType.StateFingerprint:
                            if (session == _lobbyToken && player == RemotePlayerIndex)
                            {
                                _remoteStateFingerprint = unchecked((ulong)stamp);
                                if (!_stateFingerprintReceived)
                                {
                                    Log($"received start-state fingerprint=0x{_remoteStateFingerprint:x16}");
                                }
                                _stateFingerprintReceived = true;
                            }
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
                    ulong oldestAccepted = _tick > MaxRemoteTickLead ? _tick - MaxRemoteTickLead : 0;
                    if (tick < oldestAccepted || tick > _tick + MaxRemoteTickLead)
                    {
                        continue;
                    }

                    if (_remoteHistory.TryGetValue(tick, out GamepadInput existing))
                    {
                        if (!InputsEqual(existing, remoteInput))
                        {
                            FailSession($"conflicting remote input received for immutable tick {tick}");
                            return;
                        }

                        continue;
                    }

                    _remoteHistory.Add(tick, remoteInput);
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

        private void SendClockSyncResponse(long p1SendNs, long p2ReceiveNs)
        {
            byte[] packet = new byte[ClockSyncPacketSize];
            long p2SendNs = MonotonicNowNs();
            WriteHeader(packet, PacketType.ClockSyncResponse, (byte)LocalPlayerIndex, 0, p1SendNs, unchecked(++_sendControlSequence), p2ReceiveNs);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(HeaderSize, 8), p2SendNs);
            Send(packet, packet.Length);
        }

        private void SendEpoch(long p1EpochNs, long p2MinusP1Ns)
        {
            byte[] packet = new byte[EpochPacketSize];
            WriteHeader(packet, PacketType.Epoch, (byte)LocalPlayerIndex, 0, _sessionId, unchecked(++_sendControlSequence), p1EpochNs);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(HeaderSize, 8), p2MinusP1Ns);
            Send(packet, packet.Length);
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

        private static bool InputsEqual(GamepadInput left, GamepadInput right) =>
            left.Buttons == right.Buttons &&
            left.LStick.Dx == right.LStick.Dx &&
            left.LStick.Dy == right.LStick.Dy &&
            left.RStick.Dx == right.RStick.Dx &&
            left.RStick.Dy == right.RStick.Dy;

        private static long CreateSessionId()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            long value;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                value = BinaryPrimitives.ReadInt64LittleEndian(bytes) & long.MaxValue;
            }
            while (value == 0);

            return value;
        }

        private void FailSession(string message)
        {
            _state = RunState.Failed;
            _pauseRequested = true;
            _resumeRequested = false;
            Log(message);
        }

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
