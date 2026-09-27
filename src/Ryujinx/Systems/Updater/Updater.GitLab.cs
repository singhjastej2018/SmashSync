using Gommon;
using Ryujinx.Common.Logging;
using System;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    internal static partial class Updater
    {
        private sealed class SmashSyncVersionResponse
        {
            public string ArtifactUrl { get; set; } = string.Empty;
            public string ReleaseUrlFormat { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
        }

        private static SmashSyncVersionResponse _versionResponse = new();

        public static Task<Optional<(Version Current, Version Incoming)>> CheckVersionAsync(bool showVersionUpToDate = false)
        {
            Logger.Info?.Print(LogClass.Application, "SmashSync updater is disabled for this experimental build.");
            return Task.FromResult(default(Optional<(Version Current, Version Incoming)>));
        }
    }
}
