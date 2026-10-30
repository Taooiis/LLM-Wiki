using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class SourceViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly MainWindowViewModel _mainViewModel;
        private readonly INavigationService _navigationService;
        private readonly ToastService _toastService;

        public SourceViewModel(KbService kbService, FileBridge fileBridge, MainWindowViewModel mainViewModel, INavigationService navigationService, ToastService toastService)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;
            _navigationService = navigationService;
            _toastService = toastService;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        }

        private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                LoadKb(_mainViewModel.CurrentKb);
            }
        }

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private ObservableCollection<string> _files = new();

        [ObservableProperty]
        private string _selectedFile = "";

        [ObservableProperty]
        private string _documentContent = "";

        [ObservableProperty]
        private ObservableCollection<ChangeItem> _changes = new();

        public void LoadKb(string kb)
        {
            SelectedKb = kb;
            LoadFiles(kb);
            LoadChanges(kb);
        }

        public void LoadFiles(string kb)
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            // 只列源文档（raw/sources/ 下 md/txt），不再取树顶层
            foreach (var name in _fileBridge.Sources(kb))
            {
                Files.Add(name);
            }
        }

        public void LoadFileContent(string kb, string file)
        {
            if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(file))
            {
                DocumentContent = string.Empty;
                SelectedFile = string.Empty;
                return;
            }

            var error = "";
            var content = _fileBridge.ReadText(kb, file, out error);
            DocumentContent = content ?? string.Empty;
            SelectedFile = file;
        }

        public void LoadChanges(string kb)
        {
            Changes.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            foreach (var change in _fileBridge.Changes(kb))
            {
                Changes.Add(new ChangeItem(change.File, change.Action, change.Time, change.Hash));
            }
        }

        [RelayCommand]
        private void DeleteFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            // 列表存的是文件名，源文档统一位于 raw/sources/ 下
            _fileBridge.Delete(SelectedKb, "raw/sources/" + fileName, out _);
            LoadFiles(SelectedKb);
            DocumentContent = string.Empty;
            SelectedFile = string.Empty;
        }

        [RelayCommand]
        private void SaveDocument(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(SelectedFile))
            {
                return;
            }

            _fileBridge.WriteText(SelectedKb, SelectedFile, content, out _);
            LoadChanges(SelectedKb);
        }

        public bool SaveFile(string kb, string rel, string content, out string error)
        {
            return _fileBridge.WriteText(kb, rel, content, out error);
        }

        [RelayCommand]
        private void Refresh()
        {
            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
        }

        [RelayCommand]
        private void GenerateIngestForSelected()
        {
            // 勾选变更的 File 为含 raw/sources/ 前缀的相对路径；
            // 归一化统一在 WorkflowViewModel.GenerateIngest 入口做（见 ledger Ruling T4）
            var selected = Changes.Where(c => c.IsSelected).Select(c => c.File).ToList();
            if (selected.Count == 0)
            {
                _toastService.ShowWarning("请先勾选要处理的变更");
                return;
            }

            _mainViewModel.PendingIngestFiles = string.Join(",", selected);
            _mainViewModel.PendingIngestKb = SelectedKb;

            _navigationService.Navigate(typeof(Views.Pages.WorkflowPage));
        }

        [RelayCommand]
        private void ClearSelectedChanges()
        {
            var selected = Changes.Where(c => c.IsSelected).Select(c => c.File).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            _fileBridge.ClearChanges(SelectedKb, selected.ToArray());
            foreach (var c in Changes.Where(c => c.IsSelected).ToList())
            {
                c.IsSelected = false;
            }
            LoadChanges(SelectedKb);
        }

        [RelayCommand]
        private async Task UploadFile()
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择一个知识库");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = "所有支持的文件 (*.md;*.txt;*.docx;*.xlsx)|*.md;*.txt;*.docx;*.xlsx|文本文件 (*.md;*.txt)|*.md;*.txt|Office 文件 (*.docx;*.xlsx)|*.docx;*.xlsx",
                Multiselect = false
            };

            if (dialog.ShowDialog(Application.Current.MainWindow) != true)
            {
                return;
            }

            var sourcePath = dialog.FileName;
            var fileName = Path.GetFileName(sourcePath);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            // 读取文件内容
            byte[] rawBytes;
            string content;
            string? error;
            try
            {
                rawBytes = File.ReadAllBytes(sourcePath);
                if (ext == ".docx" || ext == ".xlsx")
                {
                    // Office 文件：先保存原件到 raw/original/，Markdown 副本写 raw/sources/
                    var originalRel = "raw/original/" + fileName;
                    File.Copy(sourcePath, Path.Combine(GetKbRoot(), originalRel), overwrite: true);

                    var (md, convErr) = ext == ".docx"
                        ? OfficeConverter.ConvertDocx(fileName, rawBytes)
                        : OfficeConverter.ConvertXlsx(fileName, rawBytes);
                    if (md == null)
                    {
                        _toastService.ShowError("Office 转换失败：" + convErr);
                        return;
                    }
                    content = md;
                    error = "";
                }
                else
                {
                    content = Encoding.UTF8.GetString(rawBytes);
                    error = "";
                }
            }
            catch (Exception ex)
            {
                _toastService.ShowError("读取文件失败：" + ex.Message);
                return;
            }

            // Markdown 副本写入 raw/sources/（文件名 + .md 后缀，避免与已有 md 重名）
            var rel = "raw/sources/" + fileName;
            if (ext == ".docx" || ext == ".xlsx")
                rel = "raw/sources/" + Path.GetFileNameWithoutExtension(fileName) + ".md";

            var ok = _fileBridge.WriteText(SelectedKb, rel, content, out error);
            if (!ok)
            {
                _toastService.ShowError("上传失败：" + error);
                return;
            }

            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
            _toastService.ShowSuccess($"已上传：{fileName}");
        }

        private string GetKbRoot()
        {
            return _fileBridge.KbRoot(SelectedKb);
        }
    }

    public record ChangeItem(string File, string Action, string Time, string Hash)
    {
        public bool IsSelected { get; set; }
    }
}
