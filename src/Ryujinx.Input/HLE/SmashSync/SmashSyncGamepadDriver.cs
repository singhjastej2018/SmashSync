using Ryujinx.Common.Configuration.Hid;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Ryujinx.Input.HLE.SmashSync
{
    public sealed class SmashSyncCompositeGamepadDriver : IGamepadDriver
    {
        private readonly IGamepadDriver _primary;
        private readonly SmashSyncRemoteGamepadDriver _remote;
        private bool _disposed;

        public string DriverName => $"{_primary.DriverName}+SmashSync";

        public ReadOnlySpan<string> GamepadsIds
        {
            get
            {
                List<string> ids = [];
                ids.AddRange(_primary.GamepadsIds.ToArray());
                ids.AddRange(_remote.GamepadsIds.ToArray());
                return ids.ToArray();
            }
        }

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public SmashSyncCompositeGamepadDriver(IGamepadDriver primary)
        {
            _primary = primary;
            _remote = new SmashSyncRemoteGamepadDriver();

            _primary.OnGamepadConnected += PrimaryConnected;
            _primary.OnGamepadDisconnected += PrimaryDisconnected;
            _remote.OnGamepadConnected += RemoteConnected;
            _remote.OnGamepadDisconnected += RemoteDisconnected;
        }

        private void PrimaryConnected(string id) => OnGamepadConnected?.Invoke(id);
        private void PrimaryDisconnected(string id) => OnGamepadDisconnected?.Invoke(id);
        private void RemoteConnected(string id) => OnGamepadConnected?.Invoke(id);
        private void RemoteDisconnected(string id) => OnGamepadDisconnected?.Invoke(id);

        public IGamepad GetGamepad(string id)
        {
            IGamepad gamepad = _primary.GetGamepad(id);
            return gamepad ?? _remote.GetGamepad(id);
        }

        public IEnumerable<IGamepad> GetGamepads() => _primary.GetGamepads().Concat(_remote.GetGamepads());

        public void Clear()
        {
            _primary.Clear();
            _remote.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _primary.OnGamepadConnected -= PrimaryConnected;
            _primary.OnGamepadDisconnected -= PrimaryDisconnected;
            _remote.OnGamepadConnected -= RemoteConnected;
            _remote.OnGamepadDisconnected -= RemoteDisconnected;

            _remote.Dispose();
            _primary.Dispose();
        }
    }

    public sealed class SmashSyncRemoteGamepadDriver : IGamepadDriver
    {
        public const string RemoteId = "smashsync-remote-controller";

        private bool _connected;
        private bool _disposed;

        public string DriverName => "SmashSync";

        public ReadOnlySpan<string> GamepadsIds => _connected ? new[] { RemoteId } : Array.Empty<string>();

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public SmashSyncRemoteGamepadDriver()
        {
            SmashSyncLobbyService.Initialize();
            _connected = SmashSyncLobbyService.IsConnected;
            SmashSyncLobbyService.ConnectionChanged += HandleConnectionChanged;
        }

        private void HandleConnectionChanged()
        {
            bool connected = SmashSyncLobbyService.IsConnected;
            if (connected == _connected) return;

            _connected = connected;
            if (connected) OnGamepadConnected?.Invoke(RemoteId);
            else OnGamepadDisconnected?.Invoke(RemoteId);
        }

        public IGamepad GetGamepad(string id)
        {
            if (_connected && string.Equals(id, RemoteId, StringComparison.Ordinal))
            {
                return new SmashSyncRemoteGamepad();
            }

            return null;
        }

        public IEnumerable<IGamepad> GetGamepads()
        {
            if (_connected)
            {
                yield return new SmashSyncRemoteGamepad();
            }
        }

        public void Clear() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SmashSyncLobbyService.ConnectionChanged -= HandleConnectionChanged;
        }
    }

    public sealed class SmashSyncRemoteGamepad : IGamepad
    {
        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;
        public string Id => SmashSyncRemoteGamepadDriver.RemoteId;
        public string Name => $"SmashSync Remote P{SmashSyncLobbyService.RemotePlayer} — {SmashSyncLobbyService.PeerAddress}";
        public bool IsConnected => SmashSyncLobbyService.IsConnected;

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            ControllerKeys keys = SmashSyncLobbyService.GetLatestRemoteInput().Buttons;
            ControllerKeys key = inputId switch
            {
                GamepadButtonInputId.A => ControllerKeys.A,
                GamepadButtonInputId.B => ControllerKeys.B,
                GamepadButtonInputId.X => ControllerKeys.X,
                GamepadButtonInputId.Y => ControllerKeys.Y,
                GamepadButtonInputId.LeftStick => ControllerKeys.LStick,
                GamepadButtonInputId.RightStick => ControllerKeys.RStick,
                GamepadButtonInputId.LeftShoulder => ControllerKeys.L,
                GamepadButtonInputId.RightShoulder => ControllerKeys.R,
                GamepadButtonInputId.LeftTrigger => ControllerKeys.Zl,
                GamepadButtonInputId.RightTrigger => ControllerKeys.Zr,
                GamepadButtonInputId.DpadUp => ControllerKeys.DpadUp,
                GamepadButtonInputId.DpadDown => ControllerKeys.DpadDown,
                GamepadButtonInputId.DpadLeft => ControllerKeys.DpadLeft,
                GamepadButtonInputId.DpadRight => ControllerKeys.DpadRight,
                GamepadButtonInputId.Minus => ControllerKeys.Minus,
                GamepadButtonInputId.Plus => ControllerKeys.Plus,
                _ => 0,
            };

            return key != 0 && (keys & key) != 0;
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            GamepadInput input = SmashSyncLobbyService.GetLatestRemoteInput();

            return inputId switch
            {
                StickInputId.Left => (NormalizeAxis(input.LStick.Dx), NormalizeAxis(input.LStick.Dy)),
                StickInputId.Right => (NormalizeAxis(input.RStick.Dx), NormalizeAxis(input.RStick.Dy)),
                _ => (0f, 0f),
            };
        }

        private static float NormalizeAxis(int value) =>
            Math.Clamp(value / (float)short.MaxValue, -1f, 1f);

        public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;
        public void SetTriggerThreshold(float triggerThreshold) { }
        public void SetConfiguration(InputConfig configuration) { }
        public void SetLed(uint packedRgb) { }
        public bool HDRumble(VibrationValue left, VibrationValue right) => false;
        public bool Rumble(float lowFrequency, float highFrequency, uint durationMs) => false;
        public GamepadStateSnapshot GetMappedStateSnapshot() => IGamepad.GetStateSnapshot(this);
        public GamepadStateSnapshot GetStateSnapshot() => IGamepad.GetStateSnapshot(this);
        public void Dispose() { }
    }
}
