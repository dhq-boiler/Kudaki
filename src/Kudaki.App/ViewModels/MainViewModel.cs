using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Properties;
using Kudaki.App.Services;
using R3;

namespace Kudaki.App.ViewModels;

// v0.3 手術 (t-doc-vm-extract):
//   従来「1 個の Document を直接保持」だった MainViewModel を「Documents コレクション + ActiveDocument」に組み替えた。
//   per-doc の state / コマンド (SelectedTask / SaveAsync / ツリー操作等) は DocumentViewModel に移譲。
//   app-global の state / コマンド (Landing / Update check / Preferences / 新規タブ作成) はここに残る。
//
// XAML 互換について:
//   XAML 側の bind path (SelectedTask.Value 等) を無変更で使えるように per-doc プロパティを委譲で再エクスポート。
//   後続 t-tab-control で XAML を DataTemplate 化するときに委譲層を削除する。
//   ActiveDocument が切り替わっても現状は 1 個固定なので notify なし。切替を導入したら OnPropertyChanged 一斉発火が要る。
public sealed partial class MainViewModel : ObservableObject
{
    private readonly UpdateCheckService _updateCheck = new();
    private readonly IFileDialogService _dialogs;
    private readonly IUpdatePromptService _updatePrompt;
    private readonly IPreferencesDialogService _preferencesDialog;

    // 開いてるドキュメント一覧 (現状は常に 1 個)。t-tab-control で TabControl.ItemsSource に bind する。
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    // アクティブなドキュメント (TabControl.SelectedItem 相当)。現状は 1 個固定。
    public BindableReactiveProperty<DocumentViewModel?> ActiveDocument { get; } = new(null);

    // 起動時ランディング (splash 相当)。ロード完了までは Landing overlay が上に乗る。
    // 進捗は各起動フェーズが ReportLoading で 0→100 を報告して埋めていく。
    public BindableReactiveProperty<bool> IsLoading { get; } = new(true);
    public BindableReactiveProperty<int> LoadingPercent { get; } = new(0);
    public BindableReactiveProperty<string> LoadingStatus { get; } = new(Strings.Landing_Status_Startup);

    public BindableReactiveProperty<UpdateInfo?> AvailableUpdate { get; } = new(null);

    public MainViewModel(
        IFileDialogService dialogs,
        IUpdatePromptService updatePrompt,
        IPreferencesDialogService preferencesDialog)
    {
        _dialogs = dialogs;
        _updatePrompt = updatePrompt;
        _preferencesDialog = preferencesDialog;

        // 初期の空ドキュメントを開いた状態で起動する。
        var initial = new DocumentViewModel(dialogs);
        Documents.Add(initial);
        ActiveDocument.Value = initial;

        WirePendingChangesQueue();
    }

    // MCP サーバー (Kudaki.App プロセス内) が現在の VM を掴むための単純ブリッジ。
    // MVVM 純粋派の静的シングルトンは避けたいが、DI コンテナを持たない Kudaki では
    // 「Kudaki プロセスに MainViewModel は常に1個」を活用してこれで足りる。
    // MainWindow の ctor から一度だけセットする。
    public static MainViewModel? Current { get; internal set; }

    // MCP propose_changes 経由で PendingChangesService.Pending に投入された PendingChangeSet を
    // ActiveDocument.CurrentPendingSet に流し込む。
    // t-doc-diffoverlay-routing でこの単純ルーティングを「documentId → 該当 doc」に置き換える。
    private void WirePendingChangesQueue()
    {
        var svc = Services.Mcp.PendingChangesService.Instance;
        UpdateActiveDocumentPending();
        ((INotifyCollectionChanged)svc.Pending).CollectionChanged += (_, _) => UpdateActiveDocumentPending();

        void UpdateActiveDocumentPending()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(UpdateActiveDocumentPending));
                return;
            }
            var target = ActiveDocument.Value;
            if (target is null) return;
            target.CurrentPendingSet.Value = svc.Pending.Count > 0 ? svc.Pending[0] : null;
        }
    }

    // MCP propose_changes 経由で承認された Document を ActiveDocument に反映する。
    // Kestrel 側から UI thread に marshal 済みで呼ばれる。
    public void ApplyProposedDocument(WbsDocument proposed)
        => ActiveDocument.Value?.ApplyProposedDocument(proposed);

    // MCP get_document 用: ActiveDocument の YAML スナップショットを返す。
    public string GetDocumentYamlSnapshot()
        => ActiveDocument.Value?.GetDocumentYamlSnapshot() ?? string.Empty;

    // 起動シーケンスの各フェーズが呼ぶ進捗レポート。100 に達したら Landing を消す。
    // UI スレッド外から呼ばれる可能性があるので必要なら Dispatcher で戻す。
    public void ReportLoading(int percent, string? status = null)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ReportLoading(percent, status)));
            return;
        }
        if (percent > LoadingPercent.Value) LoadingPercent.Value = percent;
        if (status is not null) LoadingStatus.Value = status;
        if (LoadingPercent.Value >= 100) IsLoading.Value = false;
    }

    // ArrowDiagramService は View 層由来なので DocumentViewModel に転送する。
    internal void SetArrowDiagramService(IArrowDiagramService s)
    {
        foreach (var doc in Documents) doc.SetArrowDiagramService(s);
    }

    // ----- app-global コマンド -----

    [RelayCommand]
    private void OpenPreferences() => _preferencesDialog.Show();

    [RelayCommand]
    private void NewDocument()
    {
        // 現状はマルチドキュメント UI (タブ) 未実装なので、ActiveDocument を空 doc に置き換える。
        // t-tab-open-command で「新規タブとして開く」に変更する。
        ActiveDocument.Value?.NewDocumentInPlace();
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await _dialogs.ShowOpenAsync(YamlStorageService.OpenFilter, Strings.Dialog_Open_Title);
        if (path is null) return;
        if (ActiveDocument.Value is { } doc)
        {
            await doc.LoadFromPathAsync(path).ConfigureAwait(true);
        }
    }

    // FileDropBehavior が Command として呼び出す (string 引数)。
    // ActiveDocument に対して LoadFromPathAsync を委譲する。
    [RelayCommand]
    internal async Task LoadFromPathAsync(string path)
    {
        if (ActiveDocument.Value is { } doc)
        {
            await doc.LoadFromPathAsync(path).ConfigureAwait(true);
        }
    }

    // 起動時に GitHub Releases を非同期確認、新しいのがあれば AvailableUpdate に載せる。
    // 失敗は静か。App.OnStartup から fire-and-forget で叩く。
    internal async Task CheckForUpdatesAsync()
    {
        var latest = await _updateCheck.CheckAsync().ConfigureAwait(true);
        if (latest is not null)
        {
            AvailableUpdate.Value = latest;
        }
    }

    [RelayCommand]
    private void OpenRepository()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("https://github.com/dhq-boiler/Kudaki")
                {
                    UseShellExecute = true
                });
        }
        catch { }
    }

    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        var update = AvailableUpdate.Value;
        if (update is null) return;
        var launched = await _updatePrompt.PromptAndInstallAsync(update).ConfigureAwait(true);
        if (launched)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    // ===================================================================
    // XAML 互換のための per-doc プロパティ / コマンド委譲層 (t-tab-control で削除)
    // ===================================================================
    //
    // XAML と code-behind (MainWindow.xaml.cs) の bind path を変えずに済ませるため、
    // ActiveDocument.Value のプロパティ・コマンドをここから再エクスポートする。
    // ActiveDocument は現状 1 個固定 (Documents.Count == 1) なので Value! で安全。
    // タブ導入で切替可能になったら DataTemplate 側に責務を移してこの層を削除する。

    private DocumentViewModel Active => ActiveDocument.Value!;

    public BindableReactiveProperty<TaskNodeViewModel?> SelectedTask => Active.SelectedTask;
    public BindableReactiveProperty<string> WindowTitle => Active.WindowTitle;
    public BindableReactiveProperty<bool> IsDirty => Active.IsDirty;
    public BindableReactiveProperty<string?> StatusMessage => Active.StatusMessage;
    public BindableReactiveProperty<IReadOnlyList<TaskNodeViewModel>> SelectablePredecessors
        => Active.SelectablePredecessors;
    public BindableReactiveProperty<Services.Mcp.PendingChangeSet?> CurrentPendingSet
        => Active.CurrentPendingSet;
    public ObservableCollection<TaskNodeViewModel> RootTasks => Active.RootTasks;

    internal WbsDocument Document => Active.Document;
    internal string? CurrentFilePath => Active.CurrentFilePath;
    internal bool HasCurrentFilePath => Active.HasCurrentFilePath;

    // 委譲コマンド (XAML の {Binding SaveCommand} 等をそのまま動かすため)
    public ICommand SaveCommand => Active.SaveCommand;
    public ICommand SaveAsCommand => Active.SaveAsCommand;
    public ICommand ExportMarkdownCommand => Active.ExportMarkdownCommand;
    public ICommand ApproveCurrentPendingCommand => Active.ApproveCurrentPendingCommand;
    public ICommand RejectCurrentPendingCommand => Active.RejectCurrentPendingCommand;
    public ICommand AddSiblingToSelectedCommand => Active.AddSiblingToSelectedCommand;
    public ICommand AddChildToSelectedCommand => Active.AddChildToSelectedCommand;
    public ICommand DeleteSelectedCommand => Active.DeleteSelectedCommand;
    public ICommand IndentSelectedCommand => Active.IndentSelectedCommand;
    public ICommand OutdentSelectedCommand => Active.OutdentSelectedCommand;
    public ICommand MoveSelectedUpCommand => Active.MoveSelectedUpCommand;
    public ICommand MoveSelectedDownCommand => Active.MoveSelectedDownCommand;
    public ICommand SelectNextTaskCommand => Active.SelectNextTaskCommand;
    public ICommand SelectPreviousTaskCommand => Active.SelectPreviousTaskCommand;
    public ICommand SelectFirstTaskCommand => Active.SelectFirstTaskCommand;
    public ICommand SelectLastTaskCommand => Active.SelectLastTaskCommand;
    public ICommand AddPredecessorToSelectedCommand => Active.AddPredecessorToSelectedCommand;
    public ICommand RemovePredecessorFromSelectedCommand => Active.RemovePredecessorFromSelectedCommand;
    public ICommand ShowArrowDiagramCommand => Active.ShowArrowDiagramCommand;

    // ActiveDocument 内部にファイル一切開いてない状態にリセットするヘルパー。委譲コマンドから呼ばれる。
    public IEnumerable<TaskNodeViewModel> GetSelectablePredecessorsForSelected()
        => Active.GetSelectablePredecessorsForSelected();
}
