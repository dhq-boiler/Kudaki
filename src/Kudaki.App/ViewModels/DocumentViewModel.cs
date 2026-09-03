using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Properties;
using Kudaki.App.Services;
using R3;

namespace Kudaki.App.ViewModels;

// v0.3 で「シングルトン + マルチドキュメント」アーキテクチャに移行するにあたり、
// MainViewModel が 1 個の WbsDocument を直接保持していた責務を切り出した VM。
//
// 1 タブ = 1 DocumentViewModel。将来的に MainViewModel.Documents に複数並ぶ。
// このクラスは per-doc の state と logic だけを持ち、以下は MainViewModel 側の責務:
//   - Landing (splash) 制御
//   - Update check
//   - Documents / ActiveDocument コレクション管理
//   - Preferences ダイアログ起動
//
// 現状 (t-doc-vm-extract 段階):
//   - MainViewModel が Documents = [1 個の DocumentViewModel] を保持
//   - XAML 互換のため MainViewModel が per-doc プロパティを委譲で再エクスポート
//   - t-tab-control で XAML を DataTemplate 化するときに委譲層を削除
public sealed partial class DocumentViewModel : ObservableObject
{
    private readonly YamlStorageService _storage = new();
    private readonly MarkdownExportService _markdown = new();
    private readonly IFileDialogService _dialogs;
    private WbsDocument _document = null!;
    private TaskNodeViewModel _rootVm = null!;
    private string? _currentFilePath;
    private IArrowDiagramService? _arrowDiagramService;

    public BindableReactiveProperty<TaskNodeViewModel?> SelectedTask { get; } = new(null);
    public BindableReactiveProperty<string> WindowTitle { get; } = new(Strings.Main_Title_Untitled);
    public BindableReactiveProperty<bool> IsDirty { get; } = new(false);
    public BindableReactiveProperty<string?> StatusMessage { get; } = new(null);

    // 先行タスク追加候補 (SelectedTask 変更・依存編集後に再計算)
    public BindableReactiveProperty<IReadOnlyList<TaskNodeViewModel>>
        SelectablePredecessors { get; } = new(Array.Empty<TaskNodeViewModel>());

    // Diff Overlay に晒す現在レビュー中の Set。
    // v0.3 t-doc-diffoverlay-routing 完了: PendingService は per-doc の instance で、
    // MCP からの propose_changes は DocumentRegistry.Resolve(documentId) 経由で
    // この instance だけに流れ込む (他タブへの混線なし)。
    public BindableReactiveProperty<Services.Mcp.PendingChangeSet?> CurrentPendingSet { get; } = new(null);

    // per-doc の承認キュー。MCP tool の propose_changes は DocumentRegistry から
    // 該当 doc を解決して doc.PendingService.SubmitAsync を呼ぶ。
    public Services.Mcp.PendingChangesService PendingService { get; } = new();

    // per-doc の「ユーザー → AI」依頼チャネル (タスクの分解依頼など)。
    // MCP tool の wait_for_request がここでブロックして依頼を待つ。
    public Services.Mcp.AgentRequestService AgentRequests { get; } = new();

    // この doc に対して AI が wait_for_request でブロックしているか。
    // 右クリックメニューの活性と、タブの「AI 待機中」表示に使う。
    public BindableReactiveProperty<bool> IsAgentWaiting { get; } = new(false);

    // 保存先パスを持っているか。internal な HasCurrentFilePath は XAML から bind できず
    // 変更通知も無いので、UI 用にはこの観測プロパティを使う (右クリックメニューの活性判定)。
    public BindableReactiveProperty<bool> HasFilePath { get; } = new(false);

    // 積まれたまま配送されていない依頼の件数。ContextMenu の活性判定用。
    // ReadOnlyObservableCollection.Count は変更通知を出さないのでここに写す。
    public BindableReactiveProperty<int> PendingAgentRequestCount { get; } = new(0);

    // v03-approval-attention t-doc-has-pending: このタブに承認待ちがあるか。
    // TabControl.ItemTemplate のバッジと、MainViewModel の「全 doc 解決済み判定」に使う。
    public BindableReactiveProperty<bool> HasPendingApproval { get; } = new(false);

    // 新しい承認待ちがこの doc に届いた瞬間に 1 回だけ発火する。
    // MainViewModel が購読して IApprovalNotificationService に鳴らしてもらう。
    public event Action<DocumentViewModel>? PendingApprovalArrived;

    // 既に通知した Set の Id。キューが A→B と連続したときに B でもちゃんと鳴らすため、
    // 「null からの遷移」ではなく「先頭 Set の同一性」で新着を判定する。
    private Guid? _notifiedPendingSetId;

    // TabHeader 表示用: WindowTitle + dirty マーク (*) の computed。
    // WindowTitle か IsDirty が変わったら再計算して push する。
    public BindableReactiveProperty<string> TabHeaderText { get; }
        = new(Strings.Main_Title_Untitled);

    public DocumentViewModel(IFileDialogService dialogs)
    {
        _dialogs = dialogs;
        LoadDocument(new WbsDocument());
        SelectedTask.Subscribe(_ =>
        {
            // 別のタスクへ移ったら編集は確定して抜ける (Excel でセル移動するとコミットされるのと同じ)。
            EndEditTitle(revert: false);
            RecomputeSelectablePredecessors();
        });
        WireOwnPendingQueue();

        // ウェイター数 0/1 以上を bool に落として UI に晒す。
        AgentRequests.WaiterCount.Subscribe(n => IsAgentWaiting.Value = n > 0);

        // 依頼キューの件数を観測プロパティに写す (コレクションの Count は通知を出さないため)。
        ((System.Collections.Specialized.INotifyCollectionChanged)AgentRequests.Queue)
            .CollectionChanged += (_, _) => PendingAgentRequestCount.Value = AgentRequests.Queue.Count;

        // WindowTitle と IsDirty のどちらかが動いたら TabHeaderText を更新。
        // CombineLatest は R3 の Observable 拡張。両方の最新値をペアで流す。
        WindowTitle.CombineLatest(IsDirty, (t, d) => FormatTabHeader(t, d))
            .Subscribe(v => TabHeaderText.Value = v);
    }

    private static string FormatTabHeader(string title, bool dirty) => dirty ? $"{title} *" : title;

    // t-tab-close 用: MainViewModel から this doc を保存させるための public 入口。
    // 内部の SaveAsync は [RelayCommand] 由来で private なのでラップして公開する。
    public Task ExecuteSaveAsync() => SaveAsync();

    // 自 PendingService.Pending の先頭を CurrentPendingSet に晒す。
    // v0.3 で MainViewModel からこの責務を DocumentViewModel に移した (per-doc 化)。
    private void WireOwnPendingQueue()
    {
        UpdateCurrentPending();
        ((System.Collections.Specialized.INotifyCollectionChanged)PendingService.Pending)
            .CollectionChanged += (_, _) => UpdateCurrentPending();

        void UpdateCurrentPending()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(UpdateCurrentPending));
                return;
            }
            var next = PendingService.Pending.Count > 0 ? PendingService.Pending[0] : null;
            CurrentPendingSet.Value = next;
            HasPendingApproval.Value = next is not null;

            if (next is null)
            {
                _notifiedPendingSetId = null;
            }
            else if (_notifiedPendingSetId != next.Id)
            {
                _notifiedPendingSetId = next.Id;
                PendingApprovalArrived?.Invoke(this);
            }
        }
    }

    // TreeView.ItemsSource がこれをバインドする。仮想ルート方式で top-level も VM 化。
    public ObservableCollection<TaskNodeViewModel> RootTasks => _rootVm.Children;

    internal WbsDocument Document => _document;
    internal string? CurrentFilePath => _currentFilePath;
    internal bool HasCurrentFilePath => _currentFilePath is not null;

    internal void SetArrowDiagramService(IArrowDiagramService s) => _arrowDiagramService = s;

    // 現ドキュメントを空 doc にリセットする (「新規作成」動作の in-place 版)。
    // マルチドキュメント UI 実装 (t-tab-open-command) 後は MainViewModel 側で新規タブ生成に切り替わり、
    // このヘルパーは使われなくなる。
    internal void NewDocumentInPlace()
    {
        LoadDocument(new WbsDocument());
        SetCurrentFilePath(null);
        StatusMessage.Value = null;
    }

    // MCP get_document 用: 現在の Document を YAML スナップショットとして返す。
    // Kestrel の別スレッドから呼ばれる想定なので UI スレッドに戻して serialize する。
    public string GetDocumentYamlSnapshot()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return _storage.SerializeToString(_document);
        }
        return dispatcher.Invoke(() => _storage.SerializeToString(_document));
    }

    // v03-mcp-auto-apply t-revision-check: YAML スナップショット から SHA-256 12 hex を作る。
    // 目的: list_documents で AI に返して、propose_changes 時に expectedRevision と照合する。
    // 現在 revision と不一致なら「AI がスナップショット取った後にユーザーや別 AI が編集した」ケースなので
    // 上書き事故を防ぐため reject。数十 KB の YAML SHA-256 は ~ms、呼び出し頻度低いので毎回計算で十分。
    public string GetRevision()
    {
        var yaml = GetDocumentYamlSnapshot();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml));
        var sb = new System.Text.StringBuilder(12);
        for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    // MCP propose_changes がユーザー承認を得た時に呼ばれる。
    // proposed を実 Document に差し替え、path が既に紐付いていれば file に即保存する。
    // path 無し (未保存 doc) の場合は dirty=true のまま (現状 documentId が絶対パスなので
    // MCP propose_changes は path 有り doc のみ対象、この分岐は実質使われない)。
    // UI スレッドから呼ぶ (Kestrel 側が Dispatcher で switching)。
    public async Task ApplyProposedDocumentAsync(WbsDocument proposed)
    {
        LoadDocument(proposed);
        if (_currentFilePath is not null)
        {
            // 自動保存: propose 承認 = 「Kudaki の状態と file 両方に反映する」に一体化。
            // 再起動時に承認結果が消える体験 (v0.3 dogfood で先生から要望) を回避する。
            await SaveToPathInternalAsync(_currentFilePath).ConfigureAwait(true);
        }
        else
        {
            IsDirty.Value = true;
            StatusMessage.Value = Strings.Status_AiProposalApplied;
        }
    }

    // ---- タイトル編集モード (F2 / ダブルクリック) ----
    // 編集中のノードと開始時のタイトルを保持する。Escape で開始時の値に戻すため。
    private TaskNodeViewModel? _editingTask;
    private string? _editingOriginalTitle;

    [RelayCommand]
    private void BeginEditSelectedTitle()
    {
        var task = SelectedTask.Value;
        if (task is null || ReferenceEquals(_editingTask, task)) return;
        EndEditTitle(revert: false);
        _editingTask = task;
        _editingOriginalTitle = task.Title;
        task.IsEditing.Value = true;
    }

    // 編集モードを抜ける。revert=true なら開始時のタイトルに戻す (Escape)。
    // Title の binding は UpdateSourceTrigger=PropertyChanged なので、確定側は何もしなくてよい。
    public void EndEditTitle(bool revert)
    {
        var task = _editingTask;
        if (task is null) return;
        _editingTask = null;
        if (revert && _editingOriginalTitle is not null) task.Title = _editingOriginalTitle;
        _editingOriginalTitle = null;
        task.IsEditing.Value = false;
    }

    [RelayCommand]
    private void ApproveCurrentPending()
    {
        var set = CurrentPendingSet.Value;
        if (set is null) return;
        PendingService.Approve(set.Id);
    }

    [RelayCommand]
    private void RejectCurrentPending()
    {
        var set = CurrentPendingSet.Value;
        if (set is null) return;
        PendingService.Reject(set.Id);
    }

    private void RecomputeSelectablePredecessors()
    {
        var t = SelectedTask.Value;
        SelectablePredecessors.Value = t is null
            ? Array.Empty<TaskNodeViewModel>()
            : DependencyValidator.EnumerateTasks(_rootVm)
                .Where(vm => DependencyValidator.CanAddPredecessor(t, vm).IsValid)
                .ToList();
    }

    internal void LoadDocument(WbsDocument document)
    {
        _document = document;
        var stubModel = new TaskNode { Id = "__root__", Children = document.Tasks };
        _rootVm = new TaskNodeViewModel(stubModel, parent: null);

        // ロード後の第 2 パス: PredecessorIds を VM 参照に解決して Predecessors コレクションに投入。
        // (第 1 パスの木構築時点では兄弟や他サブツリーの VM がまだ生成されていない)
        ResolvePredecessorReferences(_rootVm);

        // 手動編集で IsDirty が立つように subtree 全体を購読する。
        // v0.3 t-tab-close で判明した挙動穴 (タスク編集しても dirty flag が動かない = タブ * が付かない
        // = close 確認ダイアログも出ない) の修正。Load 中の Predecessors.Add で dirty が一時的に true に
        // なるので、購読は先にセットしてこの後の IsDirty.Value = false で確実にリセットする。
        HookDirtyTracking(_rootVm);

        OnPropertyChanged(nameof(RootTasks));
        SelectedTask.Value = null;
        IsDirty.Value = false;
    }

    // 木の全ノードの PropertyChanged / Children.CollectionChanged / Predecessors.CollectionChanged を
    // 購読して、変化があれば IsDirty = true にする。Children.Add で新規追加された VM も再帰的に購読。
    // LoadDocument で _rootVm が丸ごと差し替わるので古い購読は自然に外れる (root を持つ handler 経由の
    // 参照だけ残るが root は _rootVm 以外から参照されないので GC される)。
    private void HookDirtyTracking(TaskNodeViewModel root)
    {
        foreach (var vm in DependencyValidator.EnumerateTasks(root))
        {
            SubscribeVmForDirty(vm);
        }
        // 仮想ルート (root) 自身の Children (top-level タスク) の Add/Remove も購読
        root.Children.CollectionChanged += OnChildrenChangedForDirty;
    }

    private void SubscribeVmForDirty(TaskNodeViewModel vm)
    {
        vm.PropertyChanged += OnVmPropertyChangedForDirty;
        vm.Children.CollectionChanged += OnChildrenChangedForDirty;
        vm.Predecessors.CollectionChanged += OnPredecessorsChangedForDirty;
    }

    private void OnVmPropertyChangedForDirty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Title / EstimateHours / RemainingHours / Assignee / DueDate / Notes すべて OnPropertyChanged 経由。
        // rolled-up 系は derived で notify されないので誤検知の心配なし。
        IsDirty.Value = true;
    }

    private void OnChildrenChangedForDirty(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsDirty.Value = true;
        // 新規追加された VM を再帰的に購読 (深いサブツリーも捕捉)
        if (e.NewItems is not null)
        {
            foreach (TaskNodeViewModel added in e.NewItems)
            {
                foreach (var descendant in DependencyValidator.EnumerateTasks(added))
                {
                    SubscribeVmForDirty(descendant);
                }
            }
        }
    }

    private void OnPredecessorsChangedForDirty(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsDirty.Value = true;
    }

    private static void ResolvePredecessorReferences(TaskNodeViewModel root)
    {
        var idMap = new Dictionary<string, TaskNodeViewModel>();
        foreach (var vm in DependencyValidator.EnumerateTasks(root))
        {
            idMap[vm.Id] = vm;
        }
        // Predecessors.Add は OnPredecessorsCollectionChanged 経由で
        // _model.PredecessorIds を Clear+再構築するので、iterate 対象が変異する → コピーを取ってから舐める。
        foreach (var vm in DependencyValidator.EnumerateTasks(root))
        {
            foreach (var id in vm.Model.PredecessorIds.ToList())
            {
                if (idMap.TryGetValue(id, out var pred))
                {
                    vm.Predecessors.Add(pred);
                }
            }
        }
    }

    internal void SetCurrentFilePath(string? path)
    {
        _currentFilePath = path;
        HasFilePath.Value = path is not null;
        WindowTitle.Value = path is null
            ? Strings.Main_Title_Untitled
            : string.Format(Strings.Main_Title_Format, Path.GetFileName(path));
    }

    private string SuggestedMarkdownFileName()
    {
        if (_currentFilePath is null) return "untitled.md";
        var name = Path.GetFileName(_currentFilePath);
        if (name.EndsWith(".wbs.yaml", StringComparison.OrdinalIgnoreCase))
            return name[..^".wbs.yaml".Length] + ".md";
        return Path.ChangeExtension(name, ".md");
    }

    // ----- ファイル I/O コマンド (ダイアログは IFileDialogService 経由) -----

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!HasCurrentFilePath)
        {
            await SaveAsAsync().ConfigureAwait(true);
            return;
        }
        await SaveToPathInternalAsync(_currentFilePath!).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var defaultName = _currentFilePath is null
            ? "untitled.wbs.yaml"
            : Path.GetFileName(_currentFilePath);
        var path = await _dialogs.ShowSaveAsAsync(
            YamlStorageService.SaveFilter, Strings.Dialog_SaveAs_Title, defaultName, YamlStorageService.PrimaryExtension);
        if (path is null) return;
        await SaveToPathInternalAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        var path = await _dialogs.ShowSaveAsAsync(
            MarkdownExportService.SaveFilter, Strings.Dialog_MarkdownExport_Title,
            SuggestedMarkdownFileName(), MarkdownExportService.PrimaryExtension);
        if (path is null) return;
        try
        {
            await _markdown.ExportAsync(_document, path).ConfigureAwait(true);
            StatusMessage.Value = string.Format(Strings.Status_MarkdownExported_Format, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage.Value = string.Format(Strings.Status_MarkdownExportFailed_Format, ex.Message);
        }
    }

    // ドラッグ&ドロップやスタートアップ引数からの直接ロード。
    // FileDropBehavior が Command として呼び出す (string 引数)。
    // 起動 progress を進める用に MainViewModel.ReportLoading を段階的に叩く。
    [RelayCommand]
    internal async Task LoadFromPathAsync(string path)
    {
        try
        {
            MainViewModel.Current?.ReportLoading(80, string.Format(Strings.Status_Loading_Format, Path.GetFileName(path)));
            var doc = await _storage.LoadAsync(path).ConfigureAwait(true);
            MainViewModel.Current?.ReportLoading(95, Strings.Status_Building);
            LoadDocument(doc);
            SetCurrentFilePath(path);
            StatusMessage.Value = string.Format(Strings.Status_Loaded_Format, Path.GetFileName(path));
            MainViewModel.Current?.ReportLoading(100, Strings.Status_Complete);
        }
        catch (Exception ex)
        {
            StatusMessage.Value = string.Format(Strings.Status_LoadFailed_Format, ex.Message);
            MainViewModel.Current?.ReportLoading(100);
        }
    }

    private async Task SaveToPathInternalAsync(string path)
    {
        try
        {
            await _storage.SaveAsync(_document, path).ConfigureAwait(true);
            SetCurrentFilePath(path);
            IsDirty.Value = false;
            StatusMessage.Value = string.Format(Strings.Status_Saved_Format, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage.Value = string.Format(Strings.Status_SaveFailed_Format, ex.Message);
        }
    }

    // ----- ツリー操作コマンド -----

    [RelayCommand]
    private void AddSiblingToSelected()
    {
        var current = SelectedTask.Value;
        if (current is null)
        {
            var vm = new TaskNodeViewModel(new TaskNode(), _rootVm);
            _rootVm.Children.Add(vm);
            SelectedTask.Value = vm;
            vm.IsSelected.Value = true;
            return;
        }
        current.AddSiblingAfterCommand.Execute(null);
    }

    [RelayCommand]
    private void AddChildToSelected()
    {
        var current = SelectedTask.Value;
        if (current is null)
        {
            AddSiblingToSelected();
            return;
        }
        current.AddChildCommand.Execute(null);
    }

    [RelayCommand]
    private void DeleteSelected() => SelectedTask.Value?.DeleteCommand.Execute(null);

    [RelayCommand]
    private void IndentSelected() => SelectedTask.Value?.IndentCommand.Execute(null);

    [RelayCommand]
    private void OutdentSelected() => SelectedTask.Value?.OutdentCommand.Execute(null);

    [RelayCommand]
    private void MoveSelectedUp() => SelectedTask.Value?.MoveUpCommand.Execute(null);

    [RelayCommand]
    private void MoveSelectedDown() => SelectedTask.Value?.MoveDownCommand.Execute(null);

    // ----- キーボードナビゲーション -----

    [RelayCommand]
    private void SelectNextTask()
    {
        var next = SelectedTask.Value?.NextVisibleTask();
        if (next != null) next.IsSelected.Value = true;
    }

    [RelayCommand]
    private void SelectPreviousTask()
    {
        var prev = SelectedTask.Value?.PreviousVisibleTask();
        if (prev != null) prev.IsSelected.Value = true;
    }

    [RelayCommand]
    private void SelectFirstTask()
    {
        if (RootTasks.Count > 0) RootTasks[0].IsSelected.Value = true;
    }

    [RelayCommand]
    private void SelectLastTask()
    {
        if (RootTasks.Count == 0) return;
        var cur = RootTasks[RootTasks.Count - 1];
        while (cur.IsExpanded.Value && cur.Children.Count > 0)
        {
            cur = cur.Children[cur.Children.Count - 1];
        }
        cur.IsSelected.Value = true;
    }

    // ----- 依存関係編集 -----

    [RelayCommand]
    private void AddPredecessorToSelected(TaskNodeViewModel? candidate)
    {
        var target = SelectedTask.Value;
        if (target is null || candidate is null) return;
        var result = DependencyValidator.CanAddPredecessor(target, candidate);
        if (!result.IsValid)
        {
            StatusMessage.Value = result.ErrorMessage;
            return;
        }
        target.Predecessors.Add(candidate);
        StatusMessage.Value = string.Format(Strings.Status_PredecessorAdded_Format, candidate.Title);
        RecomputeSelectablePredecessors();
    }

    [RelayCommand]
    private void RemovePredecessorFromSelected(TaskNodeViewModel? predecessor)
    {
        var target = SelectedTask.Value;
        if (target is null || predecessor is null) return;
        if (target.Predecessors.Remove(predecessor))
        {
            StatusMessage.Value = string.Format(Strings.Status_PredecessorRemoved_Format, predecessor.Title);
            RecomputeSelectablePredecessors();
        }
    }

    public IEnumerable<TaskNodeViewModel> GetSelectablePredecessorsForSelected()
    {
        var target = SelectedTask.Value;
        if (target is null) yield break;
        foreach (var vm in DependencyValidator.EnumerateTasks(_rootVm))
        {
            if (DependencyValidator.CanAddPredecessor(target, vm).IsValid)
            {
                yield return vm;
            }
        }
    }

    // ----- AI への依頼 (v0.6 タスクの分解依頼) -----

    // ツリー行の右クリック →「AI にタスクの分解を依頼」。
    // 依頼は AgentRequests に積まれ、待機中の AI があれば即配送される。
    [RelayCommand]
    private void RequestBreakdown(TaskNodeViewModel? task)
    {
        task ??= SelectedTask.Value;
        if (task is null) return;

        // 未保存 doc は DocumentRegistry.ListDocuments から除外されるので AI から解決できない。
        // メニュー側でも無効化しているが、コマンド側でも黙って弾かず理由を出す。
        if (_currentFilePath is null)
        {
            StatusMessage.Value = Strings.Status_AgentRequest_NeedsSave;
            return;
        }

        AgentRequests.Enqueue(new Services.Mcp.AgentRequest
        {
            Kind = Services.Mcp.AgentRequestKind.Breakdown,
            DocumentId = System.IO.Path.GetFullPath(_currentFilePath),
            TaskId = task.Id,
            TaskTitle = task.Title,
            AncestorTitles = CollectAncestorTitles(task),
            // 子に配分してもらうために現在値を渡す。これが無いと着手済みタスクを
            // 砕いた瞬間に進捗が 0% に戻る (親の値は子があると無視されるため)。
            EstimateHours = task.EstimateHours,
            RemainingHours = task.RemainingHours,
            Notes = task.Notes,
        });

        StatusMessage.Value = IsAgentWaiting.Value
            ? string.Format(Strings.Status_AgentRequest_Delivered_Format, task.Title)
            : string.Format(Strings.Status_AgentRequest_Queued_Format, task.Title);
    }

    // まだ配送されていない依頼を全部取り消す。
    [RelayCommand]
    private void CancelPendingAgentRequests()
    {
        var count = AgentRequests.CancelAllQueued();
        if (count > 0)
        {
            StatusMessage.Value = string.Format(Strings.Status_AgentRequest_Cancelled_Format, count);
        }
    }

    // ルート直下から当該タスクの親までのタイトル列 (AI に文脈を伝えるため)。
    private static IReadOnlyList<string> CollectAncestorTitles(TaskNodeViewModel task)
    {
        var titles = new List<string>();
        // Parent.Parent == null は仮想ルートなので祖先として数えない。
        for (var p = task.Parent; p is not null && p.Parent is not null; p = p.Parent)
        {
            titles.Add(p.Title);
        }
        titles.Reverse();
        return titles;
    }

    // ----- 実行順序 (v0.6) -----

    // 未完了の葉タスクを「依存を満たす順」に並べて返す。
    // MCP の get_next_tasks が読む、AI 向けの実行順序。
    //
    // 順序の出どころは 2 つだけで、どちらも既存の真実:
    //   1. 先行タスク依存 (PredecessorIds) — 満たされていないものは後ろに回る
    //   2. ツリーの並び順 — 依存で決まらない部分の tie-break
    // 「順序番号」フィールドは作らない。3 つ目の真実を増やすと食い違ったとき直せなくなる
    // (Fable レビュー 2026-09-03)。ユーザーは並べ替えと依存編集で順序を動かす。
    public IReadOnlyList<TaskNodeViewModel> GetExecutionOrder()
    {
        // ツリー順の葉タスクのうち、まだ残時間があるものだけが対象。
        var pending = DependencyValidator.EnumerateTasks(_rootVm)
            .Where(t => t.IsLeaf && t.RolledUpRemainingHours > 0.0)
            .ToList();

        var index = new Dictionary<TaskNodeViewModel, int>();
        for (var i = 0; i < pending.Count; i++) index[pending[i]] = i;

        // Kahn 法。同時に着手可能なものはツリー順で若い方を先に出す。
        var ordered = new List<TaskNodeViewModel>(pending.Count);
        var emitted = new HashSet<TaskNodeViewModel>();
        var remaining = new List<TaskNodeViewModel>(pending);

        while (remaining.Count > 0)
        {
            // 未完了の先行タスクが残っていないものが着手可能。
            // 完了済み・対象外の先行タスクは既に条件を満たしているので無視してよい。
            var ready = remaining
                .Where(t => t.Predecessors.All(p => !index.ContainsKey(p) || emitted.Contains(p)))
                .OrderBy(t => index[t])
                .ToList();

            if (ready.Count == 0)
            {
                // 循環が残っている場合の保険。DependencyValidator が防いでいるはずだが、
                // ここで無限ループにするより、残りをツリー順で吐いて打ち切る方が安全。
                ordered.AddRange(remaining.OrderBy(t => index[t]));
                break;
            }

            foreach (var t in ready)
            {
                ordered.Add(t);
                emitted.Add(t);
            }
            remaining.RemoveAll(emitted.Contains);
        }

        return ordered;
    }


    // Ctrl+1〜9: 選択タスクを兄弟内の N 番目 (1-indexed) に移動する。
    // MoveUp/MoveDown の random access 版。9 個までしか届かないのは仕様。
    [RelayCommand]
    private void MoveSelectedToPosition(string? position)
    {
        var task = SelectedTask.Value;
        if (task is null || task.Parent is null) return;

        // タイトル編集中は数字がテキスト入力なので何もしない (VM 側で完結させる)。
        if (task.IsEditing.Value) return;

        if (!int.TryParse(position, out var oneBased)) return;
        var siblings = task.Parent.Children;
        var from = siblings.IndexOf(task);
        if (from < 0) return;

        var to = Math.Clamp(oneBased - 1, 0, siblings.Count - 1);
        if (from == to) return;
        siblings.Move(from, to);
    }

    // 親の右クリック →「子タスクを並び順で直列化」。
    // 今の兄弟の並びをそのまま FS 依存チェーン (子 i-1 → 子 i) に変換する。
    // 番号を振るより速く、依存関係が唯一の真実のまま残る (Fable レビュー)。
    [RelayCommand]
    private void SerializeChildren(TaskNodeViewModel? parent)
    {
        parent ??= SelectedTask.Value;
        if (parent is null || parent.IsLeaf) return;

        var children = parent.Children;
        var added = 0;
        var skipped = 0;
        for (var i = 1; i < children.Count; i++)
        {
            var target = children[i];
            var predecessor = children[i - 1];
            if (target.Predecessors.Contains(predecessor)) continue;

            // 循環や自己参照は DependencyValidator に判断させる。
            // 既に別の依存が張られていて矛盾する場合はそのタスクだけ飛ばす。
            var result = DependencyValidator.CanAddPredecessor(target, predecessor);
            if (!result.IsValid)
            {
                skipped++;
                continue;
            }
            target.Predecessors.Add(predecessor);
            added++;
        }

        StatusMessage.Value = skipped > 0
            ? string.Format(Strings.Status_Serialized_PartialFormat, added, skipped)
            : string.Format(Strings.Status_Serialized_Format, added);
        RecomputeSelectablePredecessors();
    }

    [RelayCommand]
    private void ShowArrowDiagram(TaskNodeViewModel? parent)
    {
        parent ??= SelectedTask.Value;
        if (parent is null || parent.IsLeaf) return;
        _arrowDiagramService?.Show(parent);
    }
}
