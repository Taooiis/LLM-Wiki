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

        public SourceViewModel(KbService kbService, FileBridge fileBridge, MainWindowViewModel mainViewModel, INavigationService navigationService)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;
            _navigationService = navigationService;

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

            foreach (var item in _fileBridge.Tree(kb))
            {
                if (item.Type == "file")
                {
                    Files.Add(item.Name);
                }
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
        private void CreateFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var name = fileName.Trim();
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                name += ".md";
            }

            _fileBridge.WriteText(SelectedKb, name, "", out _);
            LoadFiles(SelectedKb);
        }

        [RelayCommand]
        private void DeleteFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            _fileBridge.Delete(SelectedKb, fileName, out _);
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

        [RelayCommand]
        private void ClearChange(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            _fileBridge.ClearChanges(SelectedKb, new[] { fileName });
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
        private void GenerateIngestForChange(string file)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            _mainViewModel.PendingIngestFiles.Clear();
            _mainViewModel.PendingIngestFiles.Add(file);
            _mainViewModel.PendingIngestKb = SelectedKb;

            _navigationService.Navigate(typeof(Views.Pages.WorkflowPage));
        }

        [RelayCommand]
        private void GenerateIngestForSelected()
        {
            var selected = Changes.Where(c => c.IsSelected).Select(c => c.File).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            _mainViewModel.PendingIngestFiles.Clear();
            foreach (var f in selected)
            {
                _mainViewModel.PendingIngestFiles.Add(f);
            }
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
                MessageBox.Show("请先选择一个知识库", "上传", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = "所有支持的文件 (*.md;*.txt;*.docx;*.xlsx)|*.md;*.txt;*.docx;*.xlsx|文本文件 (*.md;*.txt)|*.md;*.txt|Office 文件 (*.docx;*.xlsx)|*.docx;*.xlsx",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
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
                        MessageBox.Show(convErr, "Office 转换失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show("读取文件失败：" + ex.Message, "上传", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Markdown 副本写入 raw/sources/（文件名 + .md 后缀，避免与已有 md 重名）
            var rel = "raw/sources/" + fileName;
            if (ext == ".docx" || ext == ".xlsx")
                rel = "raw/sources/" + Path.GetFileNameWithoutExtension(fileName) + ".md";

            var ok = _fileBridge.WriteText(SelectedKb, rel, content, out error);
            if (!ok)
            {
                MessageBox.Show("上传失败：" + error, "上传", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
            MessageBox.Show($"已上传：{fileName}", "上传", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string GetKbRoot()
        {
            return Path.Combine(AppContext.BaseDirectory, "wikis", SelectedKb);
        }
    }

    public record ChangeItem(string File, string Action, string Time, string Hash)
    {
        public bool IsSelected { get; set; }
    }
}
