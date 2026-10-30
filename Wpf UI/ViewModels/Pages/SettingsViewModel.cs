using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;

namespace LLM_Wiki.ViewModels.Pages
{
    /// <summary>
    /// 设置页（设计文档 06§5）：外观主题（即时生效并记住）、版本信息、检查更新。
    /// 只展示真实数据，不伪造检查结果。
    /// </summary>
    public partial class SettingsViewModel : ObservableObject, INavigationAware
    {
        private readonly ConfigService _configService;
        private readonly UpdateService _updateService;
        private readonly ToastService _toastService;

        public SettingsViewModel(ConfigService configService, UpdateService updateService, ToastService toastService)
        {
            _configService = configService;
            _updateService = updateService;
            _toastService = toastService;

            CurrentTheme = configService.Theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
            AppVersion = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0");
        }

        [ObservableProperty]
        private string _appVersion = "";

        [ObservableProperty]
        private ApplicationTheme _currentTheme = ApplicationTheme.Dark;

        /// <summary>更新检查状态文案：空 / 检查中… / 已是最新版本 / 发现新版本 / 检查失败。</summary>
        [ObservableProperty]
        private string _updateStatus = "";

        [ObservableProperty]
        private bool _isCheckingUpdate;

        /// <summary>发现新版本时展示"打开下载页"。</summary>
        [ObservableProperty]
        private bool _hasUpdate;

        public Task OnNavigatedToAsync() => Task.CompletedTask;

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        [RelayCommand]
        private void ChangeTheme(string parameter)
        {
            var target = parameter == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
            if (CurrentTheme == target)
            {
                return;
            }

            // 先写入偏好再应用，失败不改变当前视觉
            if (!_configService.SetTheme(target == ApplicationTheme.Light ? "Light" : "Dark", out var error))
            {
                _toastService.ShowError("保存主题失败：" + error);
                return;
            }

            AppThemeService.Apply(target == ApplicationTheme.Light ? "Light" : "Dark");
            CurrentTheme = target;
        }

        [RelayCommand]
        private async Task CheckUpdateAsync()
        {
            if (IsCheckingUpdate)
            {
                return;
            }

            IsCheckingUpdate = true;
            UpdateStatus = "正在检查…";
            HasUpdate = false;
            try
            {
                var result = await _updateService.CheckResultAsync();
                UpdateStatus = result.Success
                    ? (result.LatestVersion == "" ? result.Message : $"{result.Message}（最新 v{result.LatestVersion}）")
                    : result.Message;
                HasUpdate = result.Success && result.Message.StartsWith("发现新版本", StringComparison.Ordinal);
            }
            finally
            {
                IsCheckingUpdate = false;
            }
        }

        [RelayCommand]
        private void OpenDownloadPage() => _updateService.OpenDownloadPage();
    }
}
