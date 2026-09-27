using LibHac.Common;
using LibHac.Ns;
using Ryujinx.Audio.Backends.CompatLayer;
using Ryujinx.Audio.Integration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Cpu;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Apm;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.HLE.UI;
using Ryujinx.Memory;
using System;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Shim;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE
{
    public class Switch : IDisposable
    {
        /// <summary>
        /// Currently running emulated Switch, if there is one.
        /// <para>
        /// Proper usage of this property null checks it before use, unless the caller is certain that the emulation is running.
        /// </para>
        /// <para>
        /// In case the emulation is running, there might be a way to directly pass the <see cref="Switch" /> instance, which is preferred.
        /// </para>
        /// <para>
        /// The instance is set to <c>this</c> on any <see cref="Switch" /> instantiation, and set to <c>null</c> on any <see cref="Switch" /> disposal.
        /// </para>
        /// </summary>
        public static Switch Shared { get; private set; }

        public HleConfiguration Configuration { get; }
        public IHardwareDeviceDriver AudioDeviceDriver { get; }
        public MemoryBlock Memory { get; }
        public GpuContext Gpu { get; }
        public VirtualFileSystem FileSystem { get; }
        public HOS.Horizon System { get; }

        public bool TurboMode = false;

        private string _smashSyncSaveOverrideRoot;
        private string _smashSyncSaveBackupRoot;

        public long TickScalar
        {
            get => System?.TickSource?.TickScalar ?? ITickSource.RealityTickScalar;
            set => System.TickSource.TickScalar = value;
        }

        public ProcessLoader Processes { get; }
        public PerformanceStatistics Statistics { get; }
        public Hid Hid { get; }
        public TamperMachine TamperMachine { get; }
        public IHostUIHandler UIHandler { get; }
        public Debugger.Debugger Debugger { get; }

        public int CpuCoresCount = 4; // Switch has a quad-core Tegra X1 SoC

        public VSyncMode VSyncMode { get; set; }
        public bool CustomVSyncIntervalEnabled { get; set; }
        public int CustomVSyncInterval { get; set; }
        public long TargetVSyncInterval { get; set; } = 60;

        public bool IsFrameAvailable => Gpu.Window.IsFrameAvailable;

        public DirtyHacks DirtyHacks { get; }

        public Switch(HleConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration.GpuRenderer);
            ArgumentNullException.ThrowIfNull(configuration.AudioDeviceDriver);
            ArgumentNullException.ThrowIfNull(configuration.UserChannelPersistence);

            Configuration = configuration;

            // Recover a P2 save if a previous SmashSync process terminated before
            // the temporary P1-authoritative override could be restored.
            RecoverStaleSmashSyncAuthoritativeSave();

            FileSystem = Configuration.VirtualFileSystem;
            UIHandler = Configuration.HostUIHandler;

            MemoryAllocationFlags memoryAllocationFlags = configuration.MemoryManagerMode == MemoryManagerMode.SoftwarePageTable
                ? MemoryAllocationFlags.Reserve
                : MemoryAllocationFlags.Reserve | MemoryAllocationFlags.Mirrorable;

#pragma warning disable IDE0055 // Disable formatting
            DirtyHacks        = new DirtyHacks(Configuration.Hacks);
            AudioDeviceDriver = new CompatLayerHardwareDeviceDriver(Configuration.AudioDeviceDriver);
            Memory            = new MemoryBlock(Configuration.MemoryConfiguration.DramSize, memoryAllocationFlags);
            Gpu               = new GpuContext(Configuration.GpuRenderer, DirtyHacks);
            Debugger          = Configuration.EnableGdbStub ? new Debugger.Debugger(this, Configuration.GdbStubPort) : null;
            System            = new HOS.Horizon(this);

            // For SmashSync, stop the guest monotonic clock immediately after it is
            // created, before services/application loading can accumulate a different
            // host-dependent startup offset on each PC.
            if (Configuration.SuspendApplicationOnStart)
            {
                System.TogglePauseEmulation(true);
            }

            Statistics        = new PerformanceStatistics(this);
            Hid               = new Hid(this, System.HidStorage);
            Processes         = new ProcessLoader(this);
            TamperMachine     = new TamperMachine();

            System.InitializeServices();
            System.State.SetLanguage(Configuration.SystemLanguage);
            System.State.SetRegion(Configuration.Region);

            VSyncMode                               = Configuration.VSyncMode;
            CustomVSyncInterval                     = Configuration.CustomVSyncInterval;
            TickScalar                              = TurboMode ? Configuration.TickScalar : ITickSource.RealityTickScalar;
            System.State.DockedMode                 = Configuration.EnableDockedMode;
            System.PerformanceState.PerformanceMode = System.State.DockedMode ? PerformanceMode.Boost : PerformanceMode.Default;
            System.EnablePtc                        = Configuration.EnablePtc;
            System.FsIntegrityCheckLevel            = Configuration.FsIntegrityCheckLevel;
            System.GlobalAccessLogMode              = Configuration.FsGlobalAccessLogMode;
            
            UpdateVSyncInterval();
#pragma warning restore IDE0055

            Shared = this;
        }

        public void ProcessFrame()
        {
            Gpu.ProcessShaderCacheQueue();
            Gpu.Renderer.PreFrame();
            Gpu.GPFifo.DispatchCalls();
        }

        public int IncrementCustomVSyncInterval()
        {
            CustomVSyncInterval += 1;
            UpdateVSyncInterval();

            return CustomVSyncInterval;
        }

        public int DecrementCustomVSyncInterval()
        {
            CustomVSyncInterval -= 1;
            UpdateVSyncInterval();

            return CustomVSyncInterval;
        }

        public void UpdateVSyncInterval()
        {
            switch (VSyncMode)
            {
                case VSyncMode.Custom:
                    TargetVSyncInterval = CustomVSyncInterval;
                    break;
                case VSyncMode.Switch:
                    TargetVSyncInterval = 60;
                    break;
                case VSyncMode.Unbounded:
                    TargetVSyncInterval = 1;
                    break;
            }
        }

        public void ToggleTurbo()
        {
            TurboMode = !TurboMode;
            TickScalar = TurboMode ? Configuration.TickScalar : ITickSource.RealityTickScalar;
        }

        public bool LoadCart(string exeFsDir, string romFsFile = null) => Processes.LoadUnpackedNca(exeFsDir, romFsFile);
        public bool LoadXci(string xciFile, ulong applicationId = 0) => Processes.LoadXci(xciFile, applicationId);
        public bool LoadNca(string ncaFile, BlitStruct<ApplicationControlProperty>? customNacpData = null) => Processes.LoadNca(ncaFile, customNacpData);
        public bool LoadNsp(string nspFile, ulong applicationId = 0) => Processes.LoadNsp(nspFile, applicationId);
        public bool LoadProgram(string fileName) => Processes.LoadNxo(fileName);

        public void ArmApplicationStartPause()
        {
            Configuration.SuspendApplicationOnStart = true;
            if (!System.IsPaused)
            {
                System.TogglePauseEmulation(true);
            }
        }

        public void DisarmApplicationStartPause()
        {
            Configuration.SuspendApplicationOnStart = false;
        }

        private ulong GetActiveAccountSaveId()
        {
            ulong programId = Processes.ActiveApplication?.ProgramId ?? 0UL;
            if (programId == 0)
            {
                throw new InvalidOperationException("No active application is loaded.");
            }

            HorizonClient client = System.LibHacHorizonManager.FsClient;
            var accountUserId = System.AccountManager.LastOpenedUser.UserId;
            LibHac.Fs.UserId userId = new((ulong)accountUserId.High, (ulong)accountUserId.Low);

            SaveDataFilter filter = SaveDataFilter.Make(
                programId: default,
                saveType: SaveDataType.Account,
                userId: userId,
                saveDataId: default,
                index: default);

            using UniqueRef<SaveDataIterator> iterator = new();
            client.Fs.OpenSaveDataIterator(ref iterator.Ref, SaveDataSpaceId.User, in filter).ThrowIfFailure();

            Span<SaveDataInfo> infos = stackalloc SaveDataInfo[16];

            while (true)
            {
                iterator.Get.ReadSaveDataInfo(out long readCount, infos).ThrowIfFailure();
                if (readCount == 0)
                {
                    break;
                }

                for (int i = 0; i < readCount; i++)
                {
                    SaveDataInfo info = infos[i];
                    if (info.ProgramId.Value == programId)
                    {
                        return info.SaveDataId;
                    }
                }
            }

            throw new InvalidOperationException($"No account save exists for active title {programId:x16}.");
        }

        private string GetActiveAccountSaveRoot()
        {
            ulong saveId = GetActiveAccountSaveId();
            return global::System.IO.Path.Combine(VirtualFileSystem.GetNandPath(), $"user/save/{saveId:x16}");
        }

        public byte[] CreateActiveApplicationSaveArchive()
        {
            string saveRoot = GetActiveAccountSaveRoot();
            if (!Directory.Exists(saveRoot))
            {
                throw new DirectoryNotFoundException($"Active title save directory was not found: {saveRoot}");
            }

            using MemoryStream output = new();
            using (global::System.IO.Compression.ZipArchive zip = new(output, global::System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string file in Directory.EnumerateFiles(saveRoot, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal))
                {
                    string relative = global::System.IO.Path.GetRelativePath(saveRoot, file).Replace('\\', '/');
                    global::System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(relative, global::System.IO.Compression.CompressionLevel.Fastest);

                    using Stream source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using Stream destination = entry.Open();
                    source.CopyTo(destination);
                }
            }

            return output.ToArray();
        }

        private static string SmashSyncSaveOverrideMarkerPath =>
            global::System.IO.Path.Combine(AppDataManager.BaseDirPath, "smashsync-save-override.txt");

        private static void RecoverStaleSmashSyncAuthoritativeSave()
        {
            string marker = SmashSyncSaveOverrideMarkerPath;
            if (!File.Exists(marker))
            {
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(marker);
                if (lines.Length < 2)
                {
                    throw new InvalidDataException("SmashSync save recovery marker is incomplete.");
                }

                string baseRoot = global::System.IO.Path.GetFullPath(AppDataManager.BaseDirPath) +
                    global::System.IO.Path.DirectorySeparatorChar;
                string saveRoot = global::System.IO.Path.GetFullPath(lines[0]);
                string backupRoot = global::System.IO.Path.GetFullPath(lines[1]);

                if (!saveRoot.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase) ||
                    !backupRoot.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SmashSync save recovery marker points outside the application data directory.");
                }

                if (Directory.Exists(backupRoot))
                {
                    if (Directory.Exists(saveRoot))
                    {
                        Directory.Delete(saveRoot, recursive: true);
                    }

                    CopyDirectory(backupRoot, saveRoot);
                    Directory.Delete(backupRoot, recursive: true);
                }

                File.Delete(marker);
                Logger.Info?.Print(LogClass.Application, "SmashSync: recovered P2 local save from an interrupted authoritative-save session.");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"SmashSync: automatic save recovery failed; recovery marker kept at '{marker}': {ex.Message}");
            }
        }

        public void ApplySmashSyncAuthoritativeSave(byte[] archive)
        {
            ArgumentNullException.ThrowIfNull(archive);

            if (_smashSyncSaveOverrideRoot != null)
            {
                RestoreSmashSyncAuthoritativeSave();
            }

            string saveRoot = GetActiveAccountSaveRoot();
            ulong programId = Processes.ActiveApplication?.ProgramId ?? 0UL;
            string backupBase = global::System.IO.Path.Combine(AppDataManager.BaseDirPath, "smashsync-save-backups");
            Directory.CreateDirectory(backupBase);

            string backupRoot = global::System.IO.Path.Combine(
                backupBase,
                $"{programId:x16}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");

            if (Directory.Exists(saveRoot))
            {
                CopyDirectory(saveRoot, backupRoot);
            }
            else
            {
                Directory.CreateDirectory(backupRoot);
            }

            File.WriteAllLines(SmashSyncSaveOverrideMarkerPath, [saveRoot, backupRoot]);

            try
            {
                if (Directory.Exists(saveRoot))
                {
                    Directory.Delete(saveRoot, recursive: true);
                }

                Directory.CreateDirectory(saveRoot);
                string canonicalRoot = global::System.IO.Path.GetFullPath(saveRoot) + global::System.IO.Path.DirectorySeparatorChar;

                using MemoryStream input = new(archive, writable: false);
                using global::System.IO.Compression.ZipArchive zip = new(input, global::System.IO.Compression.ZipArchiveMode.Read);

                foreach (global::System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    string destination = global::System.IO.Path.GetFullPath(
                        global::System.IO.Path.Combine(saveRoot, entry.FullName.Replace('/', global::System.IO.Path.DirectorySeparatorChar)));

                    if (!destination.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Authoritative save archive contains an invalid path.");
                    }

                    Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(destination));
                    using Stream source = entry.Open();
                    using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                    source.CopyTo(target);
                }

                _smashSyncSaveOverrideRoot = saveRoot;
                _smashSyncSaveBackupRoot = backupRoot;
                Logger.Info?.Print(LogClass.Application, $"SmashSync: P1 authoritative save applied for {programId:x16}; P2 backup={backupRoot}");
            }
            catch
            {
                try
                {
                    if (Directory.Exists(saveRoot))
                    {
                        Directory.Delete(saveRoot, recursive: true);
                    }

                    CopyDirectory(backupRoot, saveRoot);
                    Directory.Delete(backupRoot, recursive: true);
                    if (File.Exists(SmashSyncSaveOverrideMarkerPath))
                    {
                        File.Delete(SmashSyncSaveOverrideMarkerPath);
                    }
                }
                catch { }

                throw;
            }
        }

        public void RestoreSmashSyncAuthoritativeSave()
        {
            string saveRoot = _smashSyncSaveOverrideRoot;
            string backupRoot = _smashSyncSaveBackupRoot;

            _smashSyncSaveOverrideRoot = null;
            _smashSyncSaveBackupRoot = null;

            if (string.IsNullOrWhiteSpace(saveRoot) || string.IsNullOrWhiteSpace(backupRoot))
            {
                return;
            }

            try
            {
                if (Directory.Exists(saveRoot))
                {
                    Directory.Delete(saveRoot, recursive: true);
                }

                CopyDirectory(backupRoot, saveRoot);
                Directory.Delete(backupRoot, recursive: true);
                if (File.Exists(SmashSyncSaveOverrideMarkerPath))
                {
                    File.Delete(SmashSyncSaveOverrideMarkerPath);
                }
                Logger.Info?.Print(LogClass.Application, "SmashSync: restored P2 local save after authoritative P1 session");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"SmashSync: failed to restore P2 local save; backup kept at '{backupRoot}': {ex.Message}");
            }
        }

        private static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);

            if (!Directory.Exists(sourceRoot))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = global::System.IO.Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(global::System.IO.Path.Combine(destinationRoot, relative));
            }

            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = global::System.IO.Path.GetRelativePath(sourceRoot, file);
                string destination = global::System.IO.Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(destination));
                File.Copy(file, destination, overwrite: true);
            }
        }

        public ulong GetActiveApplicationStateFingerprint()
        {
            ulong programId = Processes.ActiveApplication?.ProgramId ?? 0UL;
            string version = Processes.ActiveApplication?.DisplayVersion ?? string.Empty;

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            static void AddText(IncrementalHash destination, string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                destination.AppendData(bytes);
                destination.AppendData(new byte[] { 0 });
            }

            AddText(hash, programId.ToString("x16"));
            AddText(hash, version);

            // Hash guest-visible platform/configuration inputs that can change game
            // behavior even when the title and persistent save are identical.
            AddText(hash, System.ContentManager.GetCurrentFirmwareVersion()?.VersionString ?? "no-firmware");
            AddText(hash, Configuration.SystemLanguage.ToString());
            AddText(hash, Configuration.Region.ToString());
            AddText(hash, Configuration.EnableDockedMode ? "docked" : "handheld");
            AddText(hash, Configuration.MemoryConfiguration.ToString());
            AddText(hash, Configuration.MemoryManagerMode.ToString());
            AddText(hash, TickScalar.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            AddText(hash, Configuration.UseHypervisor ? "hypervisor" : "jit");
            AddText(hash, Configuration.EnableInternetAccess ? "internet" : "offline");
            AddText(hash, Configuration.SystemTimeOffset.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            AddText(hash, Configuration.TimeZone ?? string.Empty);

            foreach (EnabledDirtyHack hack in Configuration.Hacks.OrderBy(h => h.ToString(), StringComparer.Ordinal))
            {
                AddText(hash, $"hack:{hack}");
            }

            try
            {
                HorizonClient client = System.LibHacHorizonManager.FsClient;
                var accountUserId = System.AccountManager.LastOpenedUser.UserId;
                LibHac.Fs.UserId userId = new((ulong)accountUserId.High, (ulong)accountUserId.Low);

                SaveDataFilter filter = SaveDataFilter.Make(
                    programId: default,
                    saveType: SaveDataType.Account,
                    userId: userId,
                    saveDataId: default,
                    index: default);

                using UniqueRef<SaveDataIterator> iterator = new();
                client.Fs.OpenSaveDataIterator(ref iterator.Ref, SaveDataSpaceId.User, in filter).ThrowIfFailure();

                Span<SaveDataInfo> infos = stackalloc SaveDataInfo[16];
                List<ulong> saveIds = [];

                while (true)
                {
                    iterator.Get.ReadSaveDataInfo(out long readCount, infos).ThrowIfFailure();
                    if (readCount == 0)
                    {
                        break;
                    }

                    for (int i = 0; i < readCount; i++)
                    {
                        SaveDataInfo info = infos[i];
                        if (info.ProgramId.Value == programId)
                        {
                            saveIds.Add(info.SaveDataId);
                        }
                    }
                }

                foreach (ulong saveId in saveIds.OrderBy(id => id))
                {
                    AddText(hash, "save");

                    string saveRoot = global::System.IO.Path.Combine(VirtualFileSystem.GetNandPath(), $"user/save/{saveId:x16}");
                    if (!Directory.Exists(saveRoot))
                    {
                        AddText(hash, "missing");
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(saveRoot, "*", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.Ordinal))
                    {
                        string relative = global::System.IO.Path.GetRelativePath(saveRoot, file).Replace('\\', '/');
                        AddText(hash, relative);

                        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"SmashSync: could not fingerprint active save data: {ex.Message}");
                AddText(hash, "save-fingerprint-error");
            }

            byte[] digest = hash.GetHashAndReset();
            return BitConverter.ToUInt64(digest, 0);
        }

        public void SetVolume(float volume) => AudioDeviceDriver.Volume = Math.Clamp(volume, 0f, 1f);
        public float GetVolume() => AudioDeviceDriver.Volume;
        public bool IsAudioMuted() => AudioDeviceDriver.Volume == 0;

        public void EnableCheats() => ModLoader.EnableCheats(Processes.ActiveApplication.ProgramId, TamperMachine);

        public bool WaitFifo() => Gpu.GPFifo.WaitForCommands();
        public bool ConsumeFrameAvailable() => Gpu.Window.ConsumeFrameAvailable();
        public void PresentFrame(Action swapBuffersCallback) => Gpu.Window.Present(swapBuffersCallback);
        public void DisposeGpu() => Gpu.Dispose();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Processes.ClearAllProcesses();

                // P2 uses P1's save only for the lifetime of the SmashSync session.
                // Restore P2's local save after all guest processes and save handles
                // have been torn down, before the filesystem itself is disposed.
                RestoreSmashSyncAuthoritativeSave();

                System.Dispose();
                AudioDeviceDriver.Dispose();
                FileSystem.Dispose();
                Memory.Dispose();
                Debugger?.Dispose();

                TitleIDs.CurrentApplication.Value = null;
                Shared = null;
            }
        }
    }
}
