using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Text;

namespace Ryujinx.Input.HLE.SmashSync
{
    public enum SmashSyncLobbyState
    {
        Disabled,
        Idle,
        RequestSent,
        IncomingRequest,
        Connected,
        Rejected,
        Error,
    }

    public static class SmashSyncLobbyService
    {
        private const int ProtocolVersion = 1;
        private const string ProtocolName = "SMASHSYNC";

        private static readonly object Sync = new();
        private static readonly object WriteSync = new();
        private static readonly object InputSync = new();
        private static readonly object SaveSync = new();
        private static readonly ManualResetEventSlim SaveReceivedEvent = new(false);
        private const int MaxSaveSnapshotBytes = 256 * 1024 * 1024;
        private const int SaveChunkBytes = 24 * 1024;

        private static SmashSyncConfig _config;
        private static IPAddress _peerAddress;
        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static Thread _heartbeatThread;

        private static TcpClient _incomingClient;
        private static StreamReader _incomingReader;
        private static StreamWriter _incomingWriter;
        private static long _incomingNonce;

        private static TcpClient _connectionClient;
        private static StreamWriter _connectionWriter;
        private static long _connectionNonce;

        private static volatile bool _initialized;
        private static volatile bool _disposed;
        private static volatile SmashSyncLobbyState _state = SmashSyncLobbyState.Disabled;
        private static string _lastError = "";
        private static GamepadInput _latestRemoteInput;
        private static MemoryStream _saveReceiveStream;
        private static string _saveReceiveTitle;
        private static long _saveExpectedLength;
        private static string _saveExpectedSha;
        private static int _saveNextSequence;
        private static byte[] _receivedSaveSnapshot;
        private static string _receivedSaveTitle;

        public static event Action ConnectionChanged;

        public static bool NetplayEnabled => _config?.ParsedMode == SmashSyncMode.Netplay;
        public static bool IsConnected => _state == SmashSyncLobbyState.Connected && _connectionClient?.Connected == true;
        public static bool HasIncomingRequest => _state == SmashSyncLobbyState.IncomingRequest && _incomingClient != null;
        public static SmashSyncLobbyState State => _state;
        public static int LocalPlayer => _config?.LocalPlayer ?? 1;
        public static int RemotePlayer => LocalPlayer == 1 ? 2 : 1;
        public static string PeerAddress => _config?.PeerAddress ?? "";
        public static int Port => _config?.LocalPort ?? 27888;
        public static string LastError => _lastError;

        public static string StatusText
        {
            get
            {
                if (!NetplayEnabled)
                {
                    return "SmashSync: off";
                }

                string peer = string.IsNullOrWhiteSpace(PeerAddress) ? "(no peer)" : PeerAddress;

                return _state switch
                {
                    SmashSyncLobbyState.Idle => $"SmashSync P{LocalPlayer}: {peer} — not connected",
                    SmashSyncLobbyState.RequestSent => $"SmashSync P{LocalPlayer}: requesting {peer}",
                    SmashSyncLobbyState.IncomingRequest => $"SmashSync P{LocalPlayer}: request from {peer}",
                    SmashSyncLobbyState.Connected => $"SmashSync P{LocalPlayer} ↔ P{RemotePlayer}: {peer} — connected",
                    SmashSyncLobbyState.Rejected => $"SmashSync: {peer} rejected request",
                    SmashSyncLobbyState.Error => $"SmashSync: {_lastError}",
                    _ => "SmashSync: off",
                };
            }
        }

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                _disposed = false;
                _config = SmashSyncConfig.LoadOrOff();

                if (_config.ParsedMode != SmashSyncMode.Netplay)
                {
                    SetState(SmashSyncLobbyState.Disabled);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_config.PeerAddress))
                {
                    SetError("PeerAddress is required");
                    return;
                }

                try
                {
                    _peerAddress = IPAddress.TryParse(_config.PeerAddress, out IPAddress parsed)
                        ? parsed
                        : Dns.GetHostAddresses(_config.PeerAddress).First(x => x.AddressFamily == AddressFamily.InterNetwork);

                    _listener = new TcpListener(IPAddress.Any, _config.LocalPort);
                    _listener.Start(4);

                    _acceptThread = new Thread(AcceptLoop)
                    {
                        IsBackground = true,
                        Name = "SmashSync.Lobby.Accept",
                    };
                    _acceptThread.Start();

                    _heartbeatThread = new Thread(HeartbeatLoop)
                    {
                        IsBackground = true,
                        Name = "SmashSync.Lobby.Heartbeat",
                    };
                    _heartbeatThread.Start();

                    SetState(SmashSyncLobbyState.Idle);
                    Log($"lobby listening tcp={_config.LocalPort} peer={_peerAddress}:{_config.PeerPort} localPlayer={_config.LocalPlayer}");
                }
                catch (Exception ex)
                {
                    SetError($"lobby failed: {ex.Message}");
                }
            }
        }

        public static void RequestConnection()
        {
            Initialize();

            lock (Sync)
            {
                if (!NetplayEnabled || _disposed || IsConnected || HasIncomingRequest || _state == SmashSyncLobbyState.RequestSent)
                {
                    return;
                }

                _lastError = "";
                SetState(SmashSyncLobbyState.RequestSent);

                Thread thread = new(RequestConnectionWorker)
                {
                    IsBackground = true,
                    Name = "SmashSync.Lobby.Request",
                };
                thread.Start();
            }
        }

        public static void AcceptConnection()
        {
            lock (Sync)
            {
                if (!HasIncomingRequest || _incomingWriter == null || _incomingClient == null)
                {
                    return;
                }

                try
                {
                    WriteLine(_incomingWriter, $"ACCEPT|{ProtocolName}|{ProtocolVersion}|{_incomingNonce}|{LocalPlayer}");

                    _connectionClient = _incomingClient;
                    _connectionWriter = _incomingWriter;
                    _connectionNonce = _incomingNonce;

                    _incomingClient = null;
                    _incomingReader = null;
                    _incomingWriter = null;
                    _incomingNonce = 0;

                    SetState(SmashSyncLobbyState.Connected);
                    Log($"lobby accepted peer={PeerAddress} remotePlayer=P{RemotePlayer}");
                }
                catch (Exception ex)
                {
                    CloseIncoming();
                    SetError($"accept failed: {ex.Message}");
                }
            }
        }

        public static void RejectConnection()
        {
            lock (Sync)
            {
                if (!HasIncomingRequest)
                {
                    return;
                }

                try
                {
                    if (_incomingWriter != null)
                    {
                        WriteLine(_incomingWriter, $"REJECT|{ProtocolName}|{ProtocolVersion}|{_incomingNonce}|{LocalPlayer}");
                    }
                }
                catch { }

                CloseIncoming();
                SetState(SmashSyncLobbyState.Idle);
            }
        }

        public static void Disconnect()
        {
            lock (Sync)
            {
                try
                {
                    if (_connectionWriter != null)
                    {
                        WriteLine(_connectionWriter, $"DISCONNECT|{ProtocolName}|{ProtocolVersion}|{_connectionNonce}|{LocalPlayer}");
                    }
                }
                catch { }

                CloseConnection();
                SetState(NetplayEnabled ? SmashSyncLobbyState.Idle : SmashSyncLobbyState.Disabled);
                Log("lobby disconnected");
            }
        }

        internal static void UpdateRemoteInput(GamepadInput input)
        {
            lock (InputSync)
            {
                _latestRemoteInput = input;
            }
        }

        internal static GamepadInput GetLatestRemoteInput()
        {
            lock (InputSync)
            {
                GamepadInput input = _latestRemoteInput;
                input.PlayerId = (PlayerIndex)(RemotePlayer - 1);
                return input;
            }
        }

        internal static void SendAuthoritativeSave(string titleId, byte[] archive)
        {
            if (LocalPlayer != 1 || !IsConnected)
            {
                throw new InvalidOperationException("Only connected P1 can send the authoritative save.");
            }

            ArgumentNullException.ThrowIfNull(archive);
            if (archive.Length > MaxSaveSnapshotBytes)
            {
                throw new InvalidOperationException($"Save snapshot is too large ({archive.Length} bytes).");
            }

            StreamWriter writer;
            long nonce;
            lock (Sync)
            {
                writer = _connectionWriter;
                nonce = _connectionNonce;
            }

            if (writer == null)
            {
                throw new IOException("SmashSync lobby connection is not available.");
            }

            string sha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

            lock (WriteSync)
            {
                writer.WriteLine($"SAVE_BEGIN|{ProtocolName}|{ProtocolVersion}|{nonce}|1|{titleId}|{archive.Length}|{sha}");

                int sequence = 0;
                for (int offset = 0; offset < archive.Length; offset += SaveChunkBytes)
                {
                    int count = Math.Min(SaveChunkBytes, archive.Length - offset);
                    string payload = Convert.ToBase64String(archive, offset, count);
                    writer.WriteLine($"SAVE_DATA|{ProtocolName}|{ProtocolVersion}|{nonce}|1|{sequence}|{payload}");
                    sequence++;
                }

                writer.WriteLine($"SAVE_END|{ProtocolName}|{ProtocolVersion}|{nonce}|1|{titleId}|{sha}");
                writer.Flush();
            }

            Log($"authoritative P1 save sent title={titleId} bytes={archive.Length} sha256={sha[..16]}...");
        }

        internal static bool WaitForAuthoritativeSave(string titleId, int timeoutMs, out byte[] archive)
        {
            archive = null;

            if (LocalPlayer != 2 || !IsConnected)
            {
                return false;
            }

            long deadline = Environment.TickCount64 + Math.Max(1000, timeoutMs);

            while (IsConnected)
            {
                lock (SaveSync)
                {
                    if (_receivedSaveSnapshot != null &&
                        string.Equals(_receivedSaveTitle, titleId, StringComparison.OrdinalIgnoreCase))
                    {
                        archive = _receivedSaveSnapshot;
                        _receivedSaveSnapshot = null;
                        _receivedSaveTitle = null;
                        SaveReceivedEvent.Reset();
                        return true;
                    }
                }

                int remaining = (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
                if (remaining <= 0 || !SaveReceivedEvent.Wait(Math.Min(remaining, 250)))
                {
                    if (remaining <= 0)
                    {
                        break;
                    }
                }
            }

            return false;
        }

        private static void RequestConnectionWorker()
        {
            TcpClient client = null;
            StreamReader reader = null;
            StreamWriter writer = null;

            try
            {
                client = new TcpClient(AddressFamily.InterNetwork)
                {
                    NoDelay = true,
                    ReceiveTimeout = _config.HandshakeTimeoutMs,
                    SendTimeout = _config.HandshakeTimeoutMs,
                };

                client.Connect(_peerAddress, _config.PeerPort);

                NetworkStream stream = client.GetStream();
                reader = new StreamReader(stream);
                writer = new StreamWriter(stream) { AutoFlush = true };

                long nonce = DateTime.UtcNow.Ticks ^ Environment.ProcessId ^ Environment.TickCount64;
                if (nonce == 0) nonce = 1;

                WriteLine(writer, $"REQUEST|{ProtocolName}|{ProtocolVersion}|{nonce}|{LocalPlayer}");
                Log($"lobby request sent peer={PeerAddress}:{_config.PeerPort}");

                string response = reader.ReadLine();
                string[] parts = response?.Split('|') ?? [];

                if (parts.Length >= 5 &&
                    parts[1] == ProtocolName &&
                    int.TryParse(parts[2], out int version) &&
                    version == ProtocolVersion &&
                    long.TryParse(parts[3], out long responseNonce) &&
                    responseNonce == nonce &&
                    int.TryParse(parts[4], out int remotePlayer) &&
                    remotePlayer != LocalPlayer)
                {
                    if (parts[0] == "ACCEPT")
                    {
                        lock (Sync)
                        {
                            if (_disposed)
                            {
                                client.Dispose();
                                return;
                            }

                            client.ReceiveTimeout = 0;
                            client.SendTimeout = 0;
                            _connectionClient = client;
                            _connectionWriter = writer;
                            _connectionNonce = nonce;
                            SetState(SmashSyncLobbyState.Connected);
                            Log($"lobby accepted by peer={PeerAddress} remotePlayer=P{remotePlayer}");
                        }

                        ConnectedReadLoop(client, reader, nonce);
                        return;
                    }

                    if (parts[0] == "REJECT")
                    {
                        client.Dispose();
                        SetState(SmashSyncLobbyState.Rejected);
                        return;
                    }
                }

                client.Dispose();
                SetError("peer returned an invalid handshake response");
            }
            catch (Exception ex)
            {
                try { client?.Dispose(); } catch { }

                if (!_disposed)
                {
                    SetError($"cannot connect to {PeerAddress}:{_config?.PeerPort}: {ex.Message}");
                }
            }
        }

        private static void AcceptLoop()
        {
            while (!_disposed)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;

                    if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !_peerAddress.Equals(endpoint.Address))
                    {
                        client.Dispose();
                        continue;
                    }

                    Thread thread = new(() => HandleIncoming(client))
                    {
                        IsBackground = true,
                        Name = "SmashSync.Lobby.Incoming",
                    };
                    thread.Start();
                }
                catch (SocketException) when (_disposed) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        Log($"lobby accept error: {ex.Message}");
                    }
                }
            }
        }

        private static void HandleIncoming(TcpClient client)
        {
            StreamReader reader = null;
            StreamWriter writer = null;

            try
            {
                client.ReceiveTimeout = _config.HandshakeTimeoutMs;
                client.SendTimeout = _config.HandshakeTimeoutMs;

                NetworkStream stream = client.GetStream();
                reader = new StreamReader(stream);
                writer = new StreamWriter(stream) { AutoFlush = true };

                string request = reader.ReadLine();
                string[] parts = request?.Split('|') ?? [];

                if (parts.Length < 5 ||
                    parts[0] != "REQUEST" ||
                    parts[1] != ProtocolName ||
                    !int.TryParse(parts[2], out int version) ||
                    version != ProtocolVersion ||
                    !long.TryParse(parts[3], out long nonce) ||
                    !int.TryParse(parts[4], out int remotePlayer) ||
                    remotePlayer == LocalPlayer)
                {
                    try { WriteLine(writer, $"REJECT|{ProtocolName}|{ProtocolVersion}|0|{LocalPlayer}"); } catch { }
                    client.Dispose();
                    return;
                }

                lock (Sync)
                {
                    if (_disposed || IsConnected || HasIncomingRequest)
                    {
                        try { WriteLine(writer, $"REJECT|{ProtocolName}|{ProtocolVersion}|{nonce}|{LocalPlayer}"); } catch { }
                        client.Dispose();
                        return;
                    }

                    _incomingClient = client;
                    _incomingReader = reader;
                    _incomingWriter = writer;
                    _incomingNonce = nonce;
                    SetState(SmashSyncLobbyState.IncomingRequest);
                    Log($"lobby incoming request peer={PeerAddress} remotePlayer=P{remotePlayer}");
                }

                while (!_disposed)
                {
                    lock (Sync)
                    {
                        if (ReferenceEquals(_connectionClient, client))
                        {
                            client.ReceiveTimeout = 0;
                            client.SendTimeout = 0;
                            break;
                        }

                        if (!ReferenceEquals(_incomingClient, client))
                        {
                            return;
                        }
                    }

                    Thread.Sleep(50);
                }

                if (!_disposed)
                {
                    ConnectedReadLoop(client, reader, nonce);
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_incomingClient, client))
                    {
                        CloseIncoming();
                        if (!_disposed) SetError($"incoming handshake failed: {ex.Message}");
                    }
                    else if (ReferenceEquals(_connectionClient, client))
                    {
                        CloseConnection();
                        if (!_disposed) SetState(SmashSyncLobbyState.Idle);
                    }
                }
            }
        }

        private static void ConnectedReadLoop(TcpClient client, StreamReader reader, long nonce)
        {
            try
            {
                while (!_disposed && client.Connected)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    string[] parts = line.Split('|');
                    if (parts.Length >= 4 && parts[1] == ProtocolName && long.TryParse(parts[3], out long messageNonce) && messageNonce == nonce)
                    {
                        if (parts[0] == "DISCONNECT")
                        {
                            break;
                        }

                        if (parts[0] == "SAVE_BEGIN")
                        {
                            HandleSaveBegin(parts);
                            continue;
                        }

                        if (parts[0] == "SAVE_DATA")
                        {
                            HandleSaveData(parts);
                            continue;
                        }

                        if (parts[0] == "SAVE_END")
                        {
                            HandleSaveEnd(parts);
                            continue;
                        }

                        // HEARTBEAT remains control-only. Save synchronization is a
                        // one-time, explicitly scoped P1 -> P2 pre-launch transfer.
                    }
                }
            }
            catch { }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_connectionClient, client))
                    {
                        CloseConnection();
                        if (!_disposed) SetState(SmashSyncLobbyState.Idle);
                    }
                }
            }
        }

        private static void HandleSaveBegin(string[] parts)
        {
            if (LocalPlayer != 2 ||
                parts.Length < 8 ||
                !int.TryParse(parts[4], out int senderPlayer) ||
                senderPlayer != 1 ||
                !long.TryParse(parts[6], out long length) ||
                length < 0 ||
                length > MaxSaveSnapshotBytes)
            {
                return;
            }

            lock (SaveSync)
            {
                _saveReceiveStream?.Dispose();
                _saveReceiveStream = new MemoryStream((int)length);
                _saveReceiveTitle = parts[5];
                _saveExpectedLength = length;
                _saveExpectedSha = parts[7];
                _saveNextSequence = 0;
                _receivedSaveSnapshot = null;
                _receivedSaveTitle = null;
                SaveReceivedEvent.Reset();
            }

            Log($"receiving authoritative P1 save title={parts[5]} bytes={length}");
        }

        private static void HandleSaveData(string[] parts)
        {
            if (LocalPlayer != 2 || parts.Length < 7 || !int.TryParse(parts[5], out int sequence))
            {
                return;
            }

            lock (SaveSync)
            {
                if (_saveReceiveStream == null || sequence != _saveNextSequence)
                {
                    return;
                }

                try
                {
                    byte[] chunk = Convert.FromBase64String(parts[6]);
                    if (_saveReceiveStream.Length + chunk.Length > _saveExpectedLength ||
                        _saveReceiveStream.Length + chunk.Length > MaxSaveSnapshotBytes)
                    {
                        ResetSaveReceiveLocked();
                        return;
                    }

                    _saveReceiveStream.Write(chunk, 0, chunk.Length);
                    _saveNextSequence++;
                }
                catch
                {
                    ResetSaveReceiveLocked();
                }
            }
        }

        private static void HandleSaveEnd(string[] parts)
        {
            if (LocalPlayer != 2 || parts.Length < 7)
            {
                return;
            }

            lock (SaveSync)
            {
                if (_saveReceiveStream == null ||
                    !string.Equals(parts[5], _saveReceiveTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                byte[] snapshot = _saveReceiveStream.ToArray();
                string actualSha = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
                bool valid = snapshot.LongLength == _saveExpectedLength &&
                    string.Equals(actualSha, _saveExpectedSha, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(actualSha, parts[6], StringComparison.OrdinalIgnoreCase);

                if (valid)
                {
                    _receivedSaveSnapshot = snapshot;
                    _receivedSaveTitle = _saveReceiveTitle;
                    Log($"authoritative P1 save received title={_receivedSaveTitle} bytes={snapshot.Length} sha256={actualSha[..16]}...");
                    SaveReceivedEvent.Set();
                }
                else
                {
                    Log("authoritative P1 save failed length/hash validation");
                }

                ResetSaveReceiveLocked(preserveCompleted: valid);
            }
        }

        private static void ResetSaveReceiveLocked(bool preserveCompleted = false)
        {
            _saveReceiveStream?.Dispose();
            _saveReceiveStream = null;
            _saveReceiveTitle = null;
            _saveExpectedLength = 0;
            _saveExpectedSha = null;
            _saveNextSequence = 0;

            if (!preserveCompleted)
            {
                _receivedSaveSnapshot = null;
                _receivedSaveTitle = null;
                SaveReceivedEvent.Reset();
            }
        }

        private static void ResetSaveTransfer()
        {
            lock (SaveSync)
            {
                ResetSaveReceiveLocked();
            }
        }

        private static void HeartbeatLoop()
        {
            while (!_disposed)
            {
                try
                {
                    StreamWriter writer;
                    long nonce;

                    lock (Sync)
                    {
                        writer = _connectionWriter;
                        nonce = _connectionNonce;
                    }

                    if (writer != null && IsConnected)
                    {
                        WriteLine(writer, $"HEARTBEAT|{ProtocolName}|{ProtocolVersion}|{nonce}|{LocalPlayer}");
                    }
                }
                catch
                {
                    lock (Sync)
                    {
                        CloseConnection();
                        if (!_disposed) SetState(SmashSyncLobbyState.Idle);
                    }
                }

                Thread.Sleep(1000);
            }
        }

        private static void WriteLine(StreamWriter writer, string line)
        {
            lock (WriteSync)
            {
                writer.WriteLine(line);
                writer.Flush();
            }
        }

        private static void SetState(SmashSyncLobbyState state)
        {
            _state = state;
            try { ConnectionChanged?.Invoke(); } catch { }
        }

        private static void SetError(string message)
        {
            _lastError = message;
            SetState(SmashSyncLobbyState.Error);
            Log(message);
        }

        private static void CloseIncoming()
        {
            try { _incomingClient?.Close(); } catch { }
            _incomingClient = null;
            _incomingReader = null;
            _incomingWriter = null;
            _incomingNonce = 0;
        }

        private static void CloseConnection()
        {
            try { _connectionClient?.Close(); } catch { }
            _connectionClient = null;
            _connectionWriter = null;
            _connectionNonce = 0;
            lock (InputSync) _latestRemoteInput = default;
            ResetSaveTransfer();
        }

        private static void Log(string message)
        {
            Logger.Info?.PrintMsg(LogClass.Application, $"SmashSync: {message}");
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized || _disposed)
                {
                    return;
                }

                _disposed = true;

                try { _listener?.Stop(); } catch { }
                CloseIncoming();
                CloseConnection();

                try { _acceptThread?.Join(500); } catch { }
                try { _heartbeatThread?.Join(500); } catch { }

                _listener = null;
                _acceptThread = null;
                _heartbeatThread = null;
                _initialized = false;
                _config = null;
                _peerAddress = null;
                SetState(SmashSyncLobbyState.Disabled);
            }
        }
    }
}
