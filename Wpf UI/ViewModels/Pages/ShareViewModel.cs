using System.Collections.ObjectModel;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Share;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class ShareViewModel : ObservableObject
    {
        private readonly ShareService _shareService;

        public ShareViewModel(ShareService shareService)
        {
            _shareService = shareService;
            Refresh();
        }

        [ObservableProperty]
        private bool _isRunning = false;

        [ObservableProperty]
        private int _port = 0;

        [ObservableProperty]
        private string _pin = "";

        [ObservableProperty]
        private string _localAddress = "";

        [ObservableProperty]
        private string _friendlyAddress = "";

        [ObservableProperty]
        private ObservableCollection<string> _lanAddresses = new();

        [ObservableProperty]
        private string _accessUrl = "";

        [ObservableProperty]
        private string _toggleText = "启动分享";

        public void Refresh()
        {
            IsRunning = _shareService.Running;
            Port = _shareService.Port;
            Pin = _shareService.Pin;
            LocalAddress = _shareService.LocalAddress();
            FriendlyAddress = _shareService.FriendlyAddress();
            LanAddresses = new ObservableCollection<string>(_shareService.LanAddresses());
            ToggleText = IsRunning ? "停止分享" : "启动分享";
            UpdateAccessUrl();
        }

        private void UpdateAccessUrl()
        {
            var addr = string.IsNullOrEmpty(FriendlyAddress) ? LocalAddress : FriendlyAddress;
            AccessUrl = _shareService.AccessUrl(addr);
        }

        [RelayCommand]
        private void StartShare()
        {
            if (_shareService.Running) return;

            var ok = _shareService.Start(8765, out var error);
            if (!ok)
            {
                MessageBox.Show("分享启动失败：" + error, "分享", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Refresh();
        }

        [RelayCommand]
        private void StopShare()
        {
            if (!_shareService.Running) return;

            _shareService.Stop();
            Refresh();
        }

        [RelayCommand]
        private void CopyUrl()
        {
            if (string.IsNullOrEmpty(AccessUrl)) return;

            try
            {
                System.Windows.Clipboard.SetText(AccessUrl);
                MessageBox.Show("已复制访问地址：" + AccessUrl, "复制", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                MessageBox.Show("复制剪贴板失败", "复制", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
