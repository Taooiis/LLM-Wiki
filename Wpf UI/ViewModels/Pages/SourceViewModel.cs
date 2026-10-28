using System.Collections.ObjectModel;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class SourceViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly MainWindowViewModel _mainViewModel;

        public SourceViewModel(KbService kbService, FileBridge fileBridge, MainWindowViewModel mainViewModel)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;

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
    }

    public record ChangeItem(string File, string Action, string Time, string Hash);
}
