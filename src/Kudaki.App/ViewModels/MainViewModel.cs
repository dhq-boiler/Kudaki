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
    private readonly IConfirmDialogService _confirm;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IApprovalNotificationService _approvalNotification;
    private readonly IAboutDialogService _aboutDialog;

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
        IPreferencesDialogService preferencesDialog,
        IConfirmDialogService confirm,
        IAppSettingsStore settingsStore,
        IApprovalNotificationService approvalNotification,
        IAboutDialogService aboutDialog)
    {
        _dialogs = dialogs;
        _updatePrompt = updatePrompt;
        _preferencesDialog = preferencesDialog;
        _confirm = confirm;
        _settingsStore = settingsStore;
        _approvalNotification = approvalNotification;
        _aboutDialog = aboutDialog;

        // タブの増減に追従して承認待ち通知の購読を張り替える。
        // Add/Remove の経路が複数ある (新規 / 開く / 復元 / 閉じる) ので、
        // 各経路に散らさず CollectionChanged 1 箇所で面倒を見る。
        Documents.CollectionChanged += OnDocumentsCollectionChanged;

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

    [RelayCommand]
    private void OpenAbout() => _aboutDialog.Show();

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

    // ----- タブ復元 / 永続化 (t-tab-restore-on-launch) -----

    // 起動時に settings.json の OpenDocuments を順次 open して、ActiveDocumentPath へ切替。
    // App.OnStartup から MainWindow ctor 完了後に呼ぶ。壊れたファイルはスキップして継続。
    // 起動 arg (hasStartupFile) の LoadFromPathAsync とは順序で共存する (OpenInNewTabAsync が
    // 同 path 重複を防ぐので、arg と settings が被っても 1 タブに収まる)。
    // 設定が読めなかった session では一切 persist しない。
    // 既定値 (openDocuments 空) を書き戻すと、開いていたタブ一覧を永久に失うため。
    // 次回起動で正しく読めればそのまま元に戻る。
    private bool _settingsUnreadable;

    public async Task RestoreOpenDocumentsAsync()
    {
        var loaded = _settingsStore.LoadDetailed();
        if (loaded.Failed)
        {
            // 2026-09-03 の事故: アップデート時に新旧プロセスが重なり、書き込み途中の
            // settings.json を読んで既定値に落ち、その直後 watcher が [] を書いて
            // タブ 4 個が消えた。読めなかったときは触らないのが唯一の正解。
            _settingsUnreadable = true;
            System.Diagnostics.Debug.WriteLine("[Kudaki.Restore] settings unreadable; persistence disabled for this session");
            return;
        }

        var settings = loaded.Settings;
        if (settings.OpenDocuments is null || settings.OpenDocuments.Count == 0) return;

        foreach (var path in settings.OpenDocuments)
        {
            if (!File.Exists(path)) continue;
            try
            {
                await OpenInNewTabAsync(path).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 個別 file の失敗 (YAML パース etc.) が全 tab 復元を停止しないよう吸収
                System.Diagnostics.Debug.WriteLine($"[Kudaki.Restore] failed to open {path}: {ex}");
            }
        }

        if (settings.ActiveDocumentPath is not null)
        {
            var absActive = Path.GetFullPath(settings.ActiveDocumentPath);
            var target = Documents.FirstOrDefault(d =>
                d.CurrentFilePath is not null &&
                string.Equals(Path.GetFullPath(d.CurrentFilePath), absActive, StringComparison.OrdinalIgnoreCase));
            if (target is not null) ActiveDocument.Value = target;
        }
    }

    // App.OnExit で呼ぶ。Documents の全 path と現アクティブ path を settings.json に書き出す。
    // Language 等の既存 field を保つため Load → 部分上書き → Save の順で操作する。
    public void PersistOpenDocuments()
    {
        if (_settingsUnreadable) return;
        try
        {
            var loaded = _settingsStore.LoadDetailed();
            if (loaded.Failed)
            {
                // 書き戻し直前の読みでも失敗したら、この session はもう settings.json に触らない。
                _settingsUnreadable = true;
                return;
            }
            var settings = loaded.Settings;
            settings.OpenDocuments = Documents
                .Where(d => d.CurrentFilePath is not null)
                .Select(d => d.CurrentFilePath!)
                .ToList();
            settings.ActiveDocumentPath = ActiveDocument.Value?.CurrentFilePath;
            _settingsStore.Save(settings);
        }
        catch
        {
            // 保存失敗しても shutdown を止めない (次回起動で復元が空になるだけ)
        }
    }

    // タブ追加 / close / 切替のたびに PersistOpenDocuments を呼ぶための watcher。
    // App.OnStartup の RestoreOpenDocumentsAsync 完了後に有効化する
    // (復元中の中間状態が settings.json に書かれるのを避けるため)。
    // これで crash 時 (OnClosing が呼ばれない) でも直近のタブ構成が settings.json に残る
    // (先ほど ScrollBar クラッシュで openDocuments が空になる事故が発生したので追加)。
    private bool _persistWatcherEnabled;
    public void EnablePersistWatcher()
    {
        if (_persistWatcherEnabled) return;
        _persistWatcherEnabled = true;
        Documents.CollectionChanged += (_, _) => TryPersistOpenDocuments();
        ActiveDocument.Subscribe(_ => TryPersistOpenDocuments());
    }

    private void TryPersistOpenDocuments()
    {
        try { PersistOpenDocuments(); }
        catch { /* silent — 頻繁に呼ばれるので個別のエラーは握りつぶす */ }
    }

    // t-tab-close: タブヘッダの × ボタンから呼ばれる。dirty なら保存確認、
    // 最後のタブは削除せず空 doc にリセットしてアプリ終了を防ぐ。
    [RelayCommand]
    private async Task CloseDocument(DocumentViewModel? doc)
    {
        if (doc is null) return;

        if (doc.IsDirty.Value)
        {
            var message = doc.CurrentFilePath is null
                ? Strings.CloseTab_Confirm_Message_Untitled
                : string.Format(Strings.CloseTab_Confirm_Message_Format, Path.GetFileName(doc.CurrentFilePath));
            var choice = _confirm.ShowSaveDiscardCancel(message, Strings.CloseTab_Confirm_Title);
            if (choice == ConfirmResult.Cancel) return;
            if (choice == ConfirmResult.Save)
            {
                await doc.ExecuteSaveAsync().ConfigureAwait(true);
                // SaveAs でキャンセルされた等で dirty のままなら close 中断
                if (doc.IsDirty.Value) return;
            }
            // Discard の場合はそのまま削除に進む
        }

        // 最後のタブは削除せず空 doc にリセット (アプリ終了しない)
        if (Documents.Count == 1)
        {
            doc.NewDocumentInPlace();
            return;
        }

        var index = Documents.IndexOf(doc);
        var wasActive = ReferenceEquals(ActiveDocument.Value, doc);
        Documents.Remove(doc);
        if (wasActive && Documents.Count > 0)
        {
            var newIndex = index >= Documents.Count ? Documents.Count - 1 : index;
            ActiveDocument.Value = Documents[newIndex];
        }
    }

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

    // ----- 承認待ち通知 (v03-approval-attention) -----

    // doc ごとの HasPendingApproval 購読。タブを閉じたときに解除するため保持する。
    private readonly Dictionary<DocumentViewModel, IDisposable> _approvalSubscriptions = new();

    private void OnDocumentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var doc in e.OldItems?.OfType<DocumentViewModel>() ?? Enumerable.Empty<DocumentViewModel>())
        {
            doc.PendingApprovalArrived -= OnPendingApprovalArrived;
            if (_approvalSubscriptions.Remove(doc, out var sub)) sub.Dispose();
        }
        foreach (var doc in e.NewItems?.OfType<DocumentViewModel>() ?? Enumerable.Empty<DocumentViewModel>())
        {
            doc.PendingApprovalArrived += OnPendingApprovalArrived;
            // 保存先が変わる (新規保存 / 名前を付けて保存) とタブ名も変わるので、
            // 同名判定を張り直す必要がある。
            _approvalSubscriptions[doc] = Disposable.Combine(
                doc.HasPendingApproval.Subscribe(_ => RefreshApprovalNotification()),
                doc.DocumentName.Subscribe(_ => RefreshTabDisambiguators()));
        }
        RefreshApprovalNotification();
        RefreshTabDisambiguators();
    }

    // 同じファイル名のタブが複数あるときだけ、区別できる祖先フォルダ名を添える
    // (VS Code と同じ考え方)。docs/tasks.wbs.yaml が 2 つ開かれていると
    // どちらも "tasks.wbs.yaml" になってしまい選べないため。
    //
    // 手順は 2 段:
    //   1. 全員に共通する直上フォルダを飛ばす (どちらも docs/ なら docs は区別に寄与しない)
    //   2. 残りから、全員がユニークになる最小の階層数だけ取る
    // 1 だけだと 3 つ以上のときに割れ残る (A/docs, B/docs, A/legacy で docs が 2 つ並ぶ)。
    // 2 だけだと共通の docs が全ラベルに付いて冗長になる。両方やって初めて
    // "VisualAudioRoutingApp" / "ClassDesign" のような最短で一意なラベルになる。
    internal void RefreshTabDisambiguators()
    {
        // 保存前のタブは常に素のまま。
        foreach (var d in Documents.Where(d => d.CurrentFilePath is null))
        {
            d.TabDisambiguator.Value = "";
        }

        var groups = Documents
            .Where(d => d.CurrentFilePath is not null)
            .GroupBy(d => Path.GetFileName(d.CurrentFilePath!), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var docs = group.ToList();
            if (docs.Count < 2)
            {
                foreach (var d in docs) d.TabDisambiguator.Value = "";
                continue;
            }

            var paths = docs.Select(d => AncestorNames(d.CurrentFilePath!)).ToList();
            var depth = paths.Min(p => p.Count);

            // 1. 全員一致している直上フォルダを飛ばす。
            var skip = 0;
            while (skip < depth && paths.All(p =>
                       string.Equals(p[skip], paths[0][skip], StringComparison.OrdinalIgnoreCase)))
            {
                skip++;
            }

            if (skip >= depth)
            {
                // 比較できる範囲が全部同じ。これ以上は足しても区別できないので諦める。
                foreach (var d in docs) d.TabDisambiguator.Value = "";
                continue;
            }

            // 2. ユニークになるまで階層を積む。上限まで行っても割れないならそこで打ち切る。
            var take = 1;
            while (skip + take < depth && !AllUnique(paths, skip, take)) take++;

            for (var i = 0; i < docs.Count; i++)
            {
                docs[i].TabDisambiguator.Value = FormatSegments(paths[i], skip, take);
            }
        }
    }

    private static bool AllUnique(List<IReadOnlyList<string>> paths, int skip, int take)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!seen.Add(SegmentKey(path, skip, take))) return false;
        }
        return true;
    }

    // 区切り文字に \0 を使う。フォルダ名には現れないので "a\b" と "a", "b" が衝突しない。
    private static string SegmentKey(IReadOnlyList<string> path, int skip, int take) =>
        string.Join('\0', Enumerable.Range(skip, take).Select(i => path[i]));

    // 表示用。AncestorNames は「近い順」なので、パス順 (外側 → 内側) に戻して連結する。
    // 区切りは '/' 固定。Windows の '\' は日本語フォントだと '¥' に見えて
    // "A¥docs" が通貨表記のように読めてしまうため。
    private static string FormatSegments(IReadOnlyList<string> path, int skip, int take) =>
        string.Join('/', Enumerable.Range(skip, take).Select(i => path[i]).Reverse());

    // ファイルの親フォルダから上へ向かってフォルダ名を並べる (直近が先頭)。
    private static IReadOnlyList<string> AncestorNames(string filePath)
    {
        var names = new List<string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            // ルート (C:\) は GetFileName が空になるのでドライブ表記に落とす。
            names.Add(string.IsNullOrEmpty(name) ? dir.TrimEnd(Path.DirectorySeparatorChar) : name);
            dir = Path.GetDirectoryName(dir);
        }
        return names;
    }

    private void OnPendingApprovalArrived(DocumentViewModel doc) => _approvalNotification.NotifyPendingArrived();

    // どの doc にも承認待ちが無くなったら鳴り物を止める。
    // 1 個でも残っていれば「まだ待っている」状態なので何もしない
    // (再催促タイマーは通知サービス側が持っている)。
    private void RefreshApprovalNotification()
    {
        if (!Documents.Any(d => d.HasPendingApproval.Value))
        {
            _approvalNotification.Clear();
        }
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
