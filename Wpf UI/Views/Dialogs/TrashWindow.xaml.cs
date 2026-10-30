using System.IO;
using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Pages;

using Microsoft.Win32;

namespace LLM_Wiki.Views.Dialogs;

/// <summary>
/// 资料回收站窗口：不用系统 MessageBox（00§9.8），反馈走窗口内状态栏文字；
/// 永久删除/清空用就地两段式确认（D06），恢复同名冲突用就地另存名称（D05）。
/// </summary>
public partial class TrashWindow : Window
{
    private readonly SourceViewModel _vm;
    private TrashEntry? _selected;
    private string _pendingAction = "";
    private bool _restoreArmed;

    public TrashWindow(SourceViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.OpenTrash();
        EntryList.SelectedIndex = vm.TrashEntries.Count > 0 ? 0 : -1;
        RefreshSummary();
    }

    /// <summary>窗口内状态栏：错误红色、信息次要前景色，随主题资源更新。</summary>
    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "DangerBrush" : "TextFillColorSecondaryBrush");
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshSummary()
    {
        SummaryText.Text = $"共 {_vm.TrashEntries.Count} 项 · 占用 {SourceRules.SizeText(_vm.TrashTotalBytes)}";
        var enabled = _selected != null && _selected.CanRestore;
        DisarmRestore();
        RestoreButton.IsEnabled = enabled;
        ExportButton.IsEnabled = _selected != null;
        PurgeButton.IsEnabled = _selected != null;
        RenamePanel.Visibility = Visibility.Collapsed;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>两段式确认复位：按钮文字/颜色还原。</summary>
    private void DisarmRestore()
    {
        _restoreArmed = false;
        RestoreButton.Content = "恢复";
        RestoreButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = EntryList.SelectedItem as TrashEntry;
        RefreshSummary();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        // 两段式确认：第一次出现确认文字并变色，再点执行（不弹独立 MessageBox）
        if (!_restoreArmed)
        {
            _restoreArmed = true;
            RestoreButton.Content = "确认恢复";
            RestoreButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Caution;
            SetStatus($"将把“{_selected.OriginalName}”恢复到原位置 {_selected.OriginalRel}。再次点击“恢复”执行。", false);
            return;
        }
        DisarmRestore();
        if (_vm.RestoreTrash(_selected.Id, null, out var message))
        {
            SetStatus("已恢复：" + message, false);
            _vm.OpenTrash();
            EntryList.SelectedIndex = -1;
            RefreshSummary();
            return;
        }
        if (message.Contains("同名", StringComparison.Ordinal))
        {
            // D05：预填首个可用名“原名（恢复1）.扩展名”，初焦新名称输入
            RenamePanel.Visibility = Visibility.Visible;
            RenameBox.Text = _vm.SuggestRestoreName(_selected.OriginalRel, _selected.OriginalName);
            SetStatus("原位置已有同名文件，请使用下方另存名称恢复（不会覆盖现有文件）。", true);
            RenameBox.Focus();
            RenameBox.SelectAll();
            return;
        }
        SetStatus("无法恢复：" + message, true);
    }

    private void OnRestoreWithName(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var name = RenameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("请输入另存名称。", true);
            return;
        }
        // D05：提交前重查存在性，仍冲突留框
        var target = _vm.RestoreTargetPath(_selected.OriginalRel, name);
        if (target != null && File.Exists(target))
        {
            SetStatus($"“{name}”在原位置已存在，请改用其他名称。", true);
            RenameBox.Focus();
            RenameBox.SelectAll();
            return;
        }
        if (_vm.RestoreTrash(_selected.Id, name, out var message))
        {
            SetStatus("已恢复为：" + message, false);
            _vm.OpenTrash();
            EntryList.SelectedIndex = -1;
            RefreshSummary();
        }
        else
        {
            SetStatus("无法恢复：" + message, true);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var dialog = new SaveFileDialog { Title = "导出回收站文件", FileName = _selected.OriginalName, Filter = "所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (_vm.ExportTrash(_selected.Id, dialog.FileName, out var error))
            SetStatus("已导出：" + Path.GetFileName(dialog.FileName), false);
        else
            SetStatus("导出失败：" + error, true);
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _pendingAction = "selected";
        ConfirmText.Text = $"将永久删除“{_selected.OriginalName}”（{_selected.SizeText}），删除后无法通过本应用恢复。";
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private void OnPurgeAll(object sender, RoutedEventArgs e)
    {
        if (_vm.TrashEntries.Count == 0) return;
        _pendingAction = "all";
        // D06：数量、大小与文件名摘要（前 5 个 + “等 n 项”）
        var names = _vm.TrashEntries.Take(5).Select(entry => entry.OriginalName).ToList();
        var summary = string.Join("、", names)
            + (_vm.TrashEntries.Count > names.Count ? $" 等 {_vm.TrashEntries.Count} 项" : "");
        ConfirmText.Text = $"将永久删除回收站内 {_vm.TrashEntries.Count} 项，共 {SourceRules.SizeText(_vm.TrashTotalBytes)}，删除后无法通过本应用恢复。"
            + $"包括：{summary}。";
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
            SetStatus("已清空回收站。", false);
            return;
        }
        var error = "";
        if (_selected != null && _vm.PurgeTrash(_selected.Id, out error))
        {
            ConfirmBar.Visibility = Visibility.Collapsed;
            EntryList.SelectedIndex = -1;
            RefreshSummary();
            SetStatus("已永久删除。", false);
        }
        else if (_selected != null)
        {
            // 部分失败留在同框：状态栏显示原因，可重试
            SetStatus("删除失败：" + error, true);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
