using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LlmWiki.App.Services;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;

        public DashboardViewModel(KbService kbService, FileBridge fileBridge)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            LoadKbs();
        }

        [ObservableProperty]
        private ObservableCollection<string> _kbs = new();

        [ObservableProperty]
        private ObservableCollection<string> _files = new();

        [ObservableProperty]
        private string _selectedKb = "";

        public void LoadKbs()
        {
            Kbs.Clear();
            foreach (var kb in _kbService.ListKbs())
            {
                Kbs.Add(kb);
            }
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
                Files.Add(item.Name);
            }
        }

        [RelayCommand]
        private void CreateKb(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _kbService.CreateKb(name);
            LoadKbs();
        }

        [RelayCommand]
        private void DeleteKb(string name)
        {
            _kbService.DeleteKb(name);
            LoadKbs();
        }
    }
}
