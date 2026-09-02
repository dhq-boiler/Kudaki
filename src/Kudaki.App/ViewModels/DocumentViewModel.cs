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

    public DocumentViewModel(IFileDialogService dialogs)
    {
        _dialogs = dialogs;
        LoadDocument(new WbsDocument());
        SelectedTask.Subscribe(_ => RecomputeSelectablePredecessors());
        WireOwnPendingQueue();
    }

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
            CurrentPendingSet.Value = PendingService.Pending.Count > 0 ? PendingService.Pending[0] : null;
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

        OnPropertyChanged(nameof(RootTasks));
        SelectedTask.Value = null;
        IsDirty.Value = false;
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

    [RelayCommand]
    private void ShowArrowDiagram(TaskNodeViewModel? parent)
    {
        parent ??= SelectedTask.Value;
        if (parent is null || parent.IsLeaf) return;
        _arrowDiagramService?.Show(parent);
    }
}
