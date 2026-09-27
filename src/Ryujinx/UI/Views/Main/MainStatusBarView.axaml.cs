using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input.HLE.SmashSync;
using System;

namespace Ryujinx.Ava.UI.Views.Main
{
    public partial class MainStatusBarView : RyujinxControl<MainWindowViewModel>
    {
        public MainWindow Window;
        private readonly DispatcherTimer _smashSyncTimer;

        public MainStatusBarView()
        {
            InitializeComponent();

            SmashSyncLobbyService.Initialize();

            _smashSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _smashSyncTimer.Tick += (_, _) => UpdateSmashSyncStatus();
            _smashSyncTimer.Start();

            UpdateSmashSyncStatus();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (VisualRoot is MainWindow window)
            {
                Window = window;
                ViewModel = window.ViewModel;
                LocaleManager.Instance.LocaleChanged += () => Dispatcher.UIThread.Post(() =>
                {
                    if (Window.ViewModel.EnableNonGameRunningControls)
                        Window.LoadApplications();
                });
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _smashSyncTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void UpdateSmashSyncStatus()
        {
            bool enabled = SmashSyncLobbyService.NetplayEnabled;

            SmashSyncPanel.IsVisible = enabled;
            if (!enabled)
            {
                return;
            }

            SmashSyncStatusText.Text = SmashSyncLobbyService.StatusText;

            bool connected = SmashSyncLobbyService.IsConnected;
            bool incoming = SmashSyncLobbyService.HasIncomingRequest;
            bool requesting = SmashSyncLobbyService.State == SmashSyncLobbyState.RequestSent;

            SmashSyncRequestButton.IsVisible = !connected && !incoming;
            SmashSyncRequestButton.IsEnabled = !requesting;
            SmashSyncAcceptButton.IsVisible = incoming;
            SmashSyncRejectButton.IsVisible = incoming;
            SmashSyncDisconnectButton.IsVisible = connected;
        }

        private void SmashSyncRequest_OnClick(object sender, RoutedEventArgs e)
        {
            SmashSyncLobbyService.RequestConnection();
            UpdateSmashSyncStatus();
        }

        private void SmashSyncAccept_OnClick(object sender, RoutedEventArgs e)
        {
            SmashSyncLobbyService.AcceptConnection();
            UpdateSmashSyncStatus();
        }

        private void SmashSyncReject_OnClick(object sender, RoutedEventArgs e)
        {
            SmashSyncLobbyService.RejectConnection();
            UpdateSmashSyncStatus();
        }

        private void SmashSyncDisconnect_OnClick(object sender, RoutedEventArgs e)
        {
            SmashSyncLobbyService.Disconnect();
            UpdateSmashSyncStatus();
        }

        private void VSyncMode_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            Window.ViewModel.ToggleVSyncMode();
            Logger.Info?.PrintMsg(LogClass.Application, $"VSync Mode toggled to: {Window.ViewModel.AppHost.Device.VSyncMode}");
        }

        private void DockedStatus_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            ConfigurationState.Instance.System.EnableDockedMode.Toggle();
        }

        private void AspectRatioStatus_OnClick(object sender, RoutedEventArgs e)
        {
            AspectRatio aspectRatio = ConfigurationState.Instance.Graphics.AspectRatio.Value;
            ConfigurationState.Instance.Graphics.AspectRatio.Value = (int)aspectRatio + 1 > Enum.GetNames<AspectRatio>().Length - 1 ? AspectRatio.Fixed4x3 : aspectRatio + 1;
        }

        private void Refresh_OnClick(object sender, RoutedEventArgs e) => Window.LoadApplications();

        private void VolumeStatus_OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            // Change the volume by 5% at a time
            float newValue = Window.ViewModel.Volume + (float)e.Delta.Y * 0.05f;

            Window.ViewModel.Volume = Math.Clamp(newValue, 0, 1);

            e.Handled = true;
        }
    }
}
