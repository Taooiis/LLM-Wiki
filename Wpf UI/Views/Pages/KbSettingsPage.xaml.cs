using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

namespace LLM_Wiki.Views.Pages;

public partial class KbSettingsPage
{
    public KbSettingsViewModel ViewModel { get; }
    private readonly UnsavedChangesDialog _unsavedDlg;
    private readonly ConfirmationDialog _confirmDlg;

    public KbSettingsPage(KbSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        ViewModel.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(null, CancellationToken.None);
        ViewModel.ConfirmReplaceDraft = ConfirmReplaceDraftAsync;
        ViewModel.ConfirmLoadDiskVersion = ConfirmLoadDiskVersionAsync;
        ViewModel.ConfirmDeleteLibrary = ConfirmDeleteLibraryAsync;
        ViewModel.MarkVisited();
    }

    /// <summary>D14 默认模板替换草稿（05§8.1）：520 宽，取消侧“保留当前草稿”，detail 为将填入的模板摘要，初焦在取消侧。</summary>
    private Task<bool> ConfirmReplaceDraftAsync(string name, string templateSummary) => _confirmDlg.ShowAsync(
        $"将替换“{name}”当前未保存输入，不会立刻写入文件。",
        "用默认模板替换当前草稿？",
        "载入默认模板",
        "保留当前草稿",
        false,
        520,
        templateSummary);

    /// <summary>D04 磁盘版本冲突（05§8.1）：danger 520 宽，确认后重读磁盘并丢弃该草稿；初焦在取消侧。</summary>
    private Task<bool> ConfirmLoadDiskVersionAsync(string name) => _confirmDlg.ShowAsync(
        $"磁盘上的{name}已被其他程序修改。载入磁盘版本将丢弃当前未保存草稿，且不会自动保存；也可以先把草稿另存副本。",
        "载入磁盘版本？",
        "载入磁盘版本",
        "取消",
        true,
        520);

    /// <summary>D07 永久删除知识库（05§8.2）：640 宽危险确认，detail 列删除范围与服务停止说明；初焦在取消侧。</summary>
    private Task<bool> ConfirmDeleteLibraryAsync() => _confirmDlg.ShowAsync(
        $"知识库：{ViewModel.SelectedKb}\n完整路径：{ViewModel.KbPath}",
        "永久删除知识库",
        "永久删除此知识库",
        "取消",
        true,
        640,
        "资料、知识、任务状态、库内回收站将一并删除，无法通过本应用恢复。\n将先停止绑定此库的浏览分享与 MCP 服务；停止失败则不删除。");

    private void OnRulesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PurposeCard == null || SchemaCard == null) return;
        var wide = e.NewSize.Width >= 980;
        if (wide)
        {
            Grid.SetColumn(SchemaCard, 2);
            Grid.SetRow(SchemaCard, 0);
            Grid.SetColumnSpan(PurposeCard, 1);
            Grid.SetColumnSpan(SchemaCard, 1);
            PurposeColumn.Width = new GridLength(1, GridUnitType.Star);
            SchemaColumn.Width = new GridLength(1, GridUnitType.Star);
            RulesGapColumn.Width = new GridLength(16);
            RulesGapRow.Height = new GridLength(0);
        }
        else
        {
            Grid.SetColumn(SchemaCard, 0);
            Grid.SetRow(SchemaCard, 2);
            Grid.SetColumnSpan(PurposeCard, 3);
            Grid.SetColumnSpan(SchemaCard, 3);
            PurposeColumn.Width = new GridLength(1, GridUnitType.Star);
            SchemaColumn.Width = new GridLength(0);
            RulesGapColumn.Width = new GridLength(0);
            RulesGapRow.Height = new GridLength(12);
        }
    }
}
