using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LlmWiki.App.Services;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly KbService _kbService;

        public DashboardViewModel(KbService kbService)
        {
            _kbService = kbService;
            LoadKbs();
        }

        [ObservableProperty]
        private ObservableCollection<string> _kbs = new();

        public void LoadKbs()
        {
            Kbs.Clear();
            foreach (var kb in _kbService.ListKbs())
            {
                Kbs.Add(kb);
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
