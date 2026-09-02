using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
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

        // MCP tool 側から Documents を documentId で解決するために self-register する。
        Services.Mcp.DocumentRegistry.Instance.Bind(this);
    }

    // MCP サーバー (Kudaki.App プロセス内) が現在の VM を掴むための単純ブリッジ。
    // MVVM 純粋派の静的シングルトンは避けたいが、DI コンテナを持たない Kudaki では
    // 「Kudaki プロセスに MainViewModel は常に1個」を活用してこれで足りる。
    // MainWindow の ctor から一度だけセットする。
    public static MainViewModel? Current { get; internal set; }

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

    // ArrowDiagramService は View 層由来なので保持しつつ現ドキュメントと将来の新規タブに配る。
    private IArrowDiagramService? _arrowDiagramService;
    internal void SetArrowDiagramService(IArrowDiagramService s)
    {
        _arrowDiagramService = s;
        foreach (var doc in Documents) doc.SetArrowDiagramService(s);
    }

    // ----- app-global コマンド -----

    [RelayCommand]
    private void OpenPreferences() => _preferencesDialog.Show();

    // Ctrl+N / ファイル→新規。空タブが既にあればそれを再利用、なければ新規タブを作ってアクティブ化。
    [RelayCommand]
    private void NewDocument()
    {
        if (TryReuseEmptyTab(out var empty))
        {
            ActiveDocument.Value = empty;
            return;
        }
        var doc = CreateDocument();
        Documents.Add(doc);
        ActiveDocument.Value = doc;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await _dialogs.ShowOpenAsync(YamlStorageService.OpenFilter, Strings.Dialog_Open_Title);
        if (path is null) return;
        await OpenInNewTabAsync(path).ConfigureAwait(true);
    }

    // FileDropBehavior が Command として呼び出す (string 引数)。
    // 起動引数 / Named Pipe forward からも同じ経路で来る (App.xaml.cs 経由)。
    [RelayCommand]
    internal Task LoadFromPathAsync(string path) => OpenInNewTabAsync(path);

    // t-tab-open-command: 新規タブとして開く。
    // - 既に同 path のタブがあればそのタブに切り替え (重複タブは作らない)
    // - アクティブタブが「空 doc (path なし + 未編集 + タスク 0)」ならそのタブを使い回す
    //   (最初の 1 個目の空タブが Ctrl+O のたびに空のまま残るのを防ぐ)
    // - それ以外は新規タブを追加してアクティブ化
    public async Task OpenInNewTabAsync(string path)
    {
        var absolute = Path.GetFullPath(path);

        // 既存タブ検索: 絶対パス正規化 + 大文字小文字無視 (Windows)
        var existing = Documents.FirstOrDefault(d =>
            d.CurrentFilePath is not null &&
            string.Equals(Path.GetFullPath(d.CurrentFilePath), absolute, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument.Value = existing;
            return;
        }

        DocumentViewModel target;
        if (TryReuseEmptyTab(out var empty))
        {
            target = empty;
            ActiveDocument.Value = empty;
        }
        else
        {
            target = CreateDocument();
            Documents.Add(target);
            ActiveDocument.Value = target;
        }

        await target.LoadFromPathAsync(path).ConfigureAwait(true);
    }

    // ArrowDiagramService を注入した新規 DocumentViewModel を生成する。
    private DocumentViewModel CreateDocument()
    {
        var doc = new DocumentViewModel(_dialogs);
        if (_arrowDiagramService is not null) doc.SetArrowDiagramService(_arrowDiagramService);
        return doc;
    }

    // アクティブタブが「使い回して良い空 doc」か判定して返す。
    // 判定: 保存パスなし + 未編集 (dirty=false) + タスク 0 個。
    private bool TryReuseEmptyTab(out DocumentViewModel empty)
    {
        if (ActiveDocument.Value is { } cur
            && !cur.HasCurrentFilePath
            && !cur.IsDirty.Value
            && cur.RootTasks.Count == 0)
        {
            empty = cur;
            return true;
        }
        empty = null!;
        return false;
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
