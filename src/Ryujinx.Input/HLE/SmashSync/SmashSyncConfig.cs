using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace Ryujinx.Input.HLE.SmashSync
{
    internal enum SmashSyncMode
    {
        Off,
        Record,
        Replay,
        Netplay,
    }

    internal sealed class SmashSyncConfig
    {
        public string Mode { get; set; } = "Off";
        public int LocalPlayer { get; set; } = 1;
        public int PhysicalPlayer { get; set; } = 1;
        public string PeerAddress { get; set; } = "";
        public int LocalPort { get; set; } = 27888;
        public int PeerPort { get; set; } = 27888;
        public int SyncHz { get; set; } = 60;
        public int InputDelayTicks { get; set; } = 2;
        public int LockstepTimeoutMs { get; set; } = 5000;
        public int HandshakeTimeoutMs { get; set; } = 30000;
        public int Redundancy { get; set; } = 3;
        public string ReplayFile { get; set; } = "smashsync-replay.jsonl";
        public string RecordFile { get; set; } = "smashsync-record.jsonl";
        public string LogFile { get; set; } = "smashsync-session.log";
        public bool ConfigureTwoPlayers { get; set; } = true;
        public bool RequireReadyChord { get; set; } = true;

        public SmashSyncMode ParsedMode =>
            Enum.TryParse(Mode, true, out SmashSyncMode mode) ? mode : SmashSyncMode.Off;

        public static SmashSyncConfig LoadOrOff()
        {
            string path = Path.Combine(AppDataManager.BaseDirPath, "smashsync.json");

            if (!File.Exists(path))
            {
                return new SmashSyncConfig();
            }

            try
            {
                SmashSyncConfig config = JsonSerializer.Deserialize<SmashSyncConfig>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) ?? new SmashSyncConfig();

                config.LocalPlayer = Math.Clamp(config.LocalPlayer, 1, 2);
                config.PhysicalPlayer = Math.Clamp(config.PhysicalPlayer, 1, 8);
                config.LocalPort = Math.Clamp(config.LocalPort, 1, 65535);
                config.PeerPort = Math.Clamp(config.PeerPort, 1, 65535);
                config.SyncHz = Math.Clamp(config.SyncHz, 30, 240);
                config.InputDelayTicks = Math.Clamp(config.InputDelayTicks, 0, 12);
                config.LockstepTimeoutMs = Math.Clamp(config.LockstepTimeoutMs, 250, 60000);
                config.HandshakeTimeoutMs = Math.Clamp(config.HandshakeTimeoutMs, 1000, 120000);
                config.Redundancy = Math.Clamp(config.Redundancy, 1, 3);

                return config;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"SmashSync: failed to load {path}: {ex.Message}");
                return new SmashSyncConfig();
            }
        }

        public static string ResolveDataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return AppDataManager.BaseDirPath;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDataManager.BaseDirPath, path);
        }
    }
}
