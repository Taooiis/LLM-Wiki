using System.IO;
using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Pages;

using Microsoft.Win32;

namespace LLM_Wiki.Views.Dialogs;

public partial class TrashWindow : Window
{
    private readonly SourceViewModel _vm;
    private TrashEntry? _selected;
    private string _pendingAction = "";

    public TrashWindow(SourceViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.OpenTrash();
        EntryList.SelectedIndex = vm.TrashEntries.Count > 0 ? 0 : -1;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        SummaryText.Text = $"共 {_vm.TrashEntries.Count} 项 · 占用 {SourceRules.SizeText(_vm.TrashTotalBytes)}";
        var enabled = _selected != null && _selected.CanRestore;
        RestoreButton.IsEnabled = enabled;
        ExportButton.IsEnabled = _selected != null;
        PurgeButton.IsEnabled = _selected != null;
        RenamePanel.Visibility = Visibility.Collapsed;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = EntryList.SelectedItem as TrashEntry;
        RefreshSummary();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (_vm.RestoreTrash(_selected.Id, null, out var message))
        {
            MessageBox.Show(this, "已恢复：" + message, "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            _vm.OpenTrash();
            EntryList.SelectedIndex = -1;
            RefreshSummary();
            return;
        }
        if (message.Contains("同名", StringComparison.Ordinal))
        {
            RenamePanel.Visibility = Visibility.Visible;
            RenameBox.Text = _selected.OriginalName;
            RenameBox.Focus();
            return;
        }
        MessageBox.Show(this, message, "无法恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnRestoreWithName(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var name = RenameBox.Text.Trim();
        if (name.Length == 0) return;
        if (_vm.RestoreTrash(_selected.Id, name, out var message))
        {
            MessageBox.Show(this, "已恢复为：" + message, "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            _vm.OpenTrash();
            EntryList.SelectedIndex = -1;
            RefreshSummary();
        }
        else
        {
            MessageBox.Show(this, message, "无法恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var dialog = new SaveFileDialog { Title = "导出回收站文件", FileName = _selected.OriginalName, Filter = "所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (_vm.ExportTrash(_selected.Id, dialog.FileName, out var error))
            MessageBox.Show(this, "已导出：" + Path.GetFileName(dialog.FileName), "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(this, error, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _pendingAction = "selected";
        ConfirmText.Text = $"将永久删除“{_selected.OriginalName}”（{_selected.SizeText}），无法恢复。";
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private void OnPurgeAll(object sender, RoutedEventArgs e)
    {
        if (_vm.TrashEntries.Count == 0) return;
        _pendingAction = "all";
        ConfirmText.Text = $"将永久删除回收站内 {_vm.TrashEntries.Count} 项，共 {SourceRules.SizeText(_vm.TrashTotalBytes)}，无法恢复。";
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private void OnCancelConfirm(object sender, RoutedEventArgs e)
    {
        _pendingAction = "";
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmPurge(object sender, RoutedEventArgs e)
    {
        if (_pendingAction == "all")
        {
            _vm.PurgeAllTrash();
            ConfirmBar.Visibility = Visibility.Collapsed;
            EntryList.SelectedIndex = -1;
            RefreshSummary();
            return;
        }
        var error = "";
        if (_selected != null && _vm.PurgeTrash(_selected.Id, out error))
        {
            ConfirmBar.Visibility = Visibility.Collapsed;
            EntryList.SelectedIndex = -1;
            RefreshSummary();
        }
        else if (_selected != null)
        {
            MessageBox.Show(this, error, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
