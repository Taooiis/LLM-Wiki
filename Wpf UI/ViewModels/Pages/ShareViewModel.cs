using System.Collections.ObjectModel;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class ShareViewModel : ObservableObject
    {
        private readonly ShareService _shareService;
        private readonly ToastService _toastService;

        public ShareViewModel(ShareService shareService, ToastService toastService)
        {
            _shareService = shareService;
            _toastService = toastService;
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
        private void ToggleShare()
        {
            // 单一开关入口：按当前状态启动或停止（替代原先非法的三元 Command 绑定）
            if (_shareService.Running)
            {
                StopShareCommand.Execute(null);
            }
            else
            {
                StartShareCommand.Execute(null);
            }
        }

        [RelayCommand]
        private void StartShare()
        {
            if (_shareService.Running) return;

            var ok = _shareService.Start(8765, out var error);
            if (!ok)
            {
                _toastService.ShowError("分享启动失败：" + error);
                return;
            }

            Refresh();
            _toastService.ShowShareStarted(_shareService.Port);
        }

        [RelayCommand]
        private void StopShare()
        {
            if (!_shareService.Running) return;

            _shareService.Stop();
            Refresh();
            _toastService.ShowShareStopped();
        }

        [RelayCommand]
        private void CopyUrl()
        {
            if (string.IsNullOrEmpty(AccessUrl)) return;

            try
            {
                System.Windows.Clipboard.SetText(AccessUrl);
                _toastService.ShowSuccess("已复制访问地址");
            }
            catch
            {
                _toastService.ShowError("复制剪贴板失败");
            }
        }
    }
}
