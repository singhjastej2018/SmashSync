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
            System.TogglePauseEmulation(true);
        }

        public void DisarmApplicationStartPause()
        {
            Configuration.SuspendApplicationOnStart = false;
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
            AddText(hash, Configuration.SystemLanguage.ToString());
            AddText(hash, Configuration.Region.ToString());
            AddText(hash, Configuration.EnableDockedMode ? "docked" : "handheld");

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

                    string saveRoot = Path.Combine(VirtualFileSystem.GetNandPath(), $"user/save/{saveId:x16}");
                    if (!Directory.Exists(saveRoot))
                    {
                        AddText(hash, "missing");
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(saveRoot, "*", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.Ordinal))
                    {
                        string relative = Path.GetRelativePath(saveRoot, file).Replace('\\', '/');
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
