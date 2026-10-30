using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Windows;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.ViewModels.Pages
{
    /// <summary>
    /// 分享页（局域网只读访问）：状态卡 + 地址卡 + PIN 卡 + 只读范围说明（设计文档 05）。
    /// 所有状态读取 ShareService 真实字段，不在 UI 侧臆断服务已启动。
    /// </summary>
    public partial class ShareViewModel : ObservableObject, INavigationAware
    {
        private readonly ShareService _shareService;
        private readonly ConfigService _configService;
        private readonly MainWindowViewModel _mainViewModel;
        private readonly ToastService _toastService;

        public ShareViewModel(
            ShareService shareService,
            ConfigService configService,
            MainWindowViewModel mainViewModel,
            ToastService toastService)
        {
            _shareService = shareService;
            _configService = configService;
            _mainViewModel = mainViewModel;
            _toastService = toastService;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
            SelectedKb = _mainViewModel.CurrentKb;

            Refresh();
        }

        private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                SelectedKb = _mainViewModel.CurrentKb;
            }
        }

        #region 状态

        [ObservableProperty]
        private bool _isRunning;

        /// <summary>启动中/停止中：期间禁用开关，防重复点击（设计文档 05§3）。</summary>
        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private int _port;

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

        /// <summary>服务状态文案：未启动 / 运行中 / 启动失败（设计文档 05§3）。</summary>
        [ObservableProperty]
        private string _statusText = "未启动";

        /// <summary>启动失败原因；无错误时为空。</summary>
        [ObservableProperty]
        private string _lastError = "";

        [ObservableProperty]
        private string _selectedKb = "";

        /// <summary>当前知识库展示文案：空选择时明确写「未选择」，不显示空白（设计文档 05§5）。</summary>
        public string SelectedKbText => string.IsNullOrWhiteSpace(SelectedKb) ? "未选择" : SelectedKb;

        /// <summary>当前暴露的知识库根目录（只读范围）。</summary>
        public string StoragePath => string.IsNullOrWhiteSpace(SelectedKb)
            ? _configService.WikisDir
            : System.IO.Path.Combine(_configService.WikisDir, SelectedKb);

        /// <summary>
        /// 分享范围展示文案：未选库时不能显示 wikis 根目录——
        /// 那会让用户误以为整个存储目录都被暴露。服务根本不会启动（CanStart=false）。
        /// </summary>
        public string StoragePathText => string.IsNullOrWhiteSpace(SelectedKb)
            ? "未选择知识库，不会暴露任何目录"
            : StoragePath;

        /// <summary>没有选中知识库时不允许启动分享（设计文档 05§6）。</summary>
        public bool CanStart => !string.IsNullOrWhiteSpace(SelectedKb);

        /// <summary>开关可用性：无库或处理中时禁用，避免无效点击。</summary>
        public bool CanToggle => CanStart && !IsBusy;

        public bool HasError => !string.IsNullOrEmpty(LastError);

        /// <summary>运行中才显示地址/PIN 卡（未启动时这些值无意义）。</summary>
        public bool ShowConnectionInfo => IsRunning;

        public string EmptyKbHint => CanStart ? "" : "请先在首页选择或创建知识库，再启动分享。";

        partial void OnIsRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowConnectionInfo));
            ToggleText = value ? "停止分享" : "启动分享";
        }

        partial void OnSelectedKbChanged(string value)
        {
            OnPropertyChanged(nameof(SelectedKbText));
            OnPropertyChanged(nameof(StoragePath));
            OnPropertyChanged(nameof(StoragePathText));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(EmptyKbHint));
        }

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

        partial void OnLastErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

        /// <summary>从服务读取真实状态；由页面进入、启动/停止后调用。</summary>
        public void Refresh()
        {
            IsRunning = _shareService.Running;
            Port = _shareService.Port;
            Pin = _shareService.Pin;
            LocalAddress = _shareService.LocalAddress();
            FriendlyAddress = _shareService.FriendlyAddress();
            LanAddresses = new ObservableCollection<string>(_shareService.LanAddresses());
            StatusText = IsRunning ? "运行中" : "未启动";
            UpdateAccessUrl();
        }

        /// <summary>进入页面时向服务核对一次状态（可能在别处启动/停止了）。</summary>
        public Task OnNavigatedToAsync()
        {
            Refresh();
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        private void UpdateAccessUrl()
        {
            var addr = string.IsNullOrEmpty(FriendlyAddress) ? LocalAddress : FriendlyAddress;
            AccessUrl = _shareService.AccessUrl(addr);
        }

        #endregion

        #region 命令

        [RelayCommand]
        private void ToggleShare()
        {
            // 单一入口：按真实服务状态决定启动还是停止
            if (_shareService.Running)
            {
                StopShare();
            }
            else
            {
                StartShare();
            }
        }

        [RelayCommand]
        private void StartShare()
        {
            if (_shareService.Running || IsBusy)
            {
                return;
            }

            if (!CanStart)
            {
                LastError = "请先在首页选择知识库";
                StatusText = "启动失败";
                return;
            }

            IsBusy = true;
            try
            {
                var ok = _shareService.Start(8765, out var error);
                if (!ok)
                {
                    LastError = error;
                    StatusText = "启动失败";
                    _toastService.ShowError("分享启动失败：" + error);
                    Refresh();
                    // 强制通知一次，让 ToggleSwitch 回到真实关闭状态（失败不能显示已开启）
                    OnPropertyChanged(nameof(IsRunning));
                    return;
                }

                LastError = "";
                Refresh();
                _toastService.ShowShareStarted(_shareService.Port);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void StopShare()
        {
            if (!_shareService.Running)
            {
                return;
            }

            IsBusy = true;
            try
            {
                _shareService.Stop();
                LastError = "";
                Refresh();
                _toastService.ShowShareStopped();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>重新生成 PIN：旧 PIN 立即失效，已连接设备需要重新输入（设计文档 05§4）。</summary>
        [RelayCommand]
        private void RegeneratePin()
        {
            if (!_shareService.RegeneratePin(out var error))
            {
                _toastService.ShowWarning(error);
                return;
            }

            Refresh();
            _toastService.ShowSuccess("已生成新 PIN，旧 PIN 立即失效，已连接设备需重新输入");
        }

        [RelayCommand]
        private void CopyUrl()
        {
            if (string.IsNullOrEmpty(AccessUrl))
            {
                return;
            }

            CopyToClipboard(AccessUrl, "已复制访问地址");
        }

        [RelayCommand]
        private void CopyPin()
        {
            if (string.IsNullOrEmpty(Pin))
            {
                return;
            }

            CopyToClipboard(Pin, "已复制 PIN");
        }

        /// <summary>在浏览器中打开本机地址（仅运行中可用，设计文档 05§4）。</summary>
        [RelayCommand]
        private void OpenLocalUrl()
        {
            if (!IsRunning || string.IsNullOrEmpty(LocalAddress))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(LocalAddress) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _toastService.ShowError("打开浏览器失败：" + ex.Message);
            }
        }

        private void CopyToClipboard(string text, string successMessage)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                _toastService.ShowSuccess(successMessage);
            }
            catch
            {
                _toastService.ShowError("复制剪贴板失败");
            }
        }

        #endregion
    }
}
