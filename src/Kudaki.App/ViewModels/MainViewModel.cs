using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Services;
using R3;

namespace Kudaki.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly YamlStorageService _storage = new();
    private readonly MarkdownExportService _markdown = new();
    private readonly UpdateCheckService _updateCheck = new();
    private readonly IFileDialogService _dialogs;
    private readonly IUpdatePromptService _updatePrompt;
    private WbsDocument _document = null!;
    private TaskNodeViewModel _rootVm = null!;
    private string? _currentFilePath;

    // R3 の BindableReactiveProperty を採用。XAML は {Binding X.Value} でアクセス。
    public BindableReactiveProperty<TaskNodeViewModel?> SelectedTask { get; } = new(null);
    public BindableReactiveProperty<string> WindowTitle { get; } = new("Kudaki - 無題");
    public BindableReactiveProperty<bool> IsDirty { get; } = new(false);
    public BindableReactiveProperty<string?> StatusMessage { get; } = new(null);
    public BindableReactiveProperty<UpdateInfo?> AvailableUpdate { get; } = new(null);

    // 先行タスク追加候補 (SelectedTask 変更・依存編集後に再計算)
    public BindableReactiveProperty<System.Collections.Generic.IReadOnlyList<TaskNodeViewModel>>
        SelectablePredecessors { get; } = new(System.Array.Empty<TaskNodeViewModel>());

    public MainViewModel(IFileDialogService dialogs, IUpdatePromptService updatePrompt)
    {
        _dialogs = dialogs;
        _updatePrompt = updatePrompt;
        LoadDocument(new WbsDocument());
        // SelectedTask が切り替わったら候補一覧を再計算
        SelectedTask.Subscribe(_ => RecomputeSelectablePredecessors());
    }

    private void RecomputeSelectablePredecessors()
    {
        var t = SelectedTask.Value;
        SelectablePredecessors.Value = t is null
            ? System.Array.Empty<TaskNodeViewModel>()
            : DependencyValidator.EnumerateTasks(_rootVm)
                .Where(vm => DependencyValidator.CanAddPredecessor(t, vm).IsValid)
                .ToList();
    }

    // TreeView.ItemsSource がこれをバインドする。仮想ルート方式で top-level も VM 化。
    public ObservableCollection<TaskNodeViewModel> RootTasks => _rootVm.Children;

    internal WbsDocument Document => _document;
    internal string? CurrentFilePath => _currentFilePath;
    internal bool HasCurrentFilePath => _currentFilePath is not null;

    // MCP サーバー (Kudaki.App プロセス内) が現在の VM を掴むための単純ブリッジ。
    // MVVM 純粋派の静的シングルトンは避けたいが、DI コンテナを持たない Kudaki では
    // 「Kudaki プロセスに MainViewModel は常に1個」を活用してこれで足りる。
    // MainWindow の ctor から一度だけセットする。
    public static MainViewModel? Current { get; internal set; }

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
        // id → VM の flat map
        var idMap = new System.Collections.Generic.Dictionary<string, TaskNodeViewModel>();
        foreach (var vm in DependencyValidator.EnumerateTasks(root))
        {
            idMap[vm.Id] = vm;
        }
        // 各 VM の Model.PredecessorIds を Predecessors コレクションへ流し込む。
        // Predecessors.Add は OnPredecessorsCollectionChanged 経由で
        // _model.PredecessorIds を Clear+再構築するので、iterate 対象が変異する。
        // → コピーを取ってから舐める。
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
            ? "Kudaki - 無題"
            : $"Kudaki - {System.IO.Path.GetFileName(path)}";
    }

    // 現在ファイルパスから Markdown 出力の推奨ファイル名を生成。
    private string SuggestedMarkdownFileName()
    {
        if (_currentFilePath is null) return "untitled.md";
        var name = System.IO.Path.GetFileName(_currentFilePath);
        if (name.EndsWith(".wbs.yaml", StringComparison.OrdinalIgnoreCase))
            return name[..^".wbs.yaml".Length] + ".md";
        return System.IO.Path.ChangeExtension(name, ".md");
    }

    // ----- ファイル I/O コマンド (ダイアログは IFileDialogService 経由) -----

    [RelayCommand]
    private void NewDocument()
    {
        LoadDocument(new WbsDocument());
        SetCurrentFilePath(null);
        StatusMessage.Value = null;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await _dialogs.ShowOpenAsync(YamlStorageService.OpenFilter, "WBS ファイルを開く");
        if (path is null) return;
        await LoadFromPathAsync(path).ConfigureAwait(true);
    }

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
            : System.IO.Path.GetFileName(_currentFilePath);
        var path = await _dialogs.ShowSaveAsAsync(
            YamlStorageService.SaveFilter, "名前を付けて保存", defaultName, YamlStorageService.PrimaryExtension);
        if (path is null) return;
        await SaveToPathInternalAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        var path = await _dialogs.ShowSaveAsAsync(
            MarkdownExportService.SaveFilter, "Markdown エクスポート",
            SuggestedMarkdownFileName(), MarkdownExportService.PrimaryExtension);
        if (path is null) return;
        try
        {
            await _markdown.ExportAsync(_document, path).ConfigureAwait(true);
            StatusMessage.Value = $"Markdown エクスポートしました: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Markdown エクスポート失敗: {ex.Message}";
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
        // 更新プロンプトを開く。インストーラー起動に成功した (=result true) 場合は
        // 自身を終了して installer に上書きさせる。
        var launched = await _updatePrompt.PromptAndInstallAsync(update).ConfigureAwait(true);
        if (launched)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    // ドラッグ&ドロップやスタートアップ引数からの直接ロード。
    // FileDropBehavior が Command として呼び出す (string 引数)。
    [RelayCommand]
    internal async Task LoadFromPathAsync(string path)
    {
        try
        {
            var doc = await _storage.LoadAsync(path).ConfigureAwait(true);
            LoadDocument(doc);
            SetCurrentFilePath(path);
            StatusMessage.Value = $"読み込みました: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"読み込み失敗: {ex.Message}";
        }
    }

    private async Task SaveToPathInternalAsync(string path)
    {
        try
        {
            await _storage.SaveAsync(_document, path).ConfigureAwait(true);
            SetCurrentFilePath(path);
            IsDirty.Value = false;
            StatusMessage.Value = $"保存しました: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"保存失敗: {ex.Message}";
        }
    }

    // ----- ツリー操作コマンド (Enter / Alt+Enter / Delete などから叩かれる) -----

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

    // ----- キーボードナビゲーション (行内 TextBox にフォーカスがあると Up/Down/Home/End が
    //       キャレット移動として消費されるので、View 側の PreviewKeyDown からここへ流す) -----

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

    // SelectedTask に predecessor 追加。妥当性チェックを通ったものだけ追加。
    // 弾かれた理由は StatusMessage に流す。
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
        StatusMessage.Value = $"先行タスクを追加: {candidate.Title}";
        RecomputeSelectablePredecessors();
    }

    [RelayCommand]
    private void RemovePredecessorFromSelected(TaskNodeViewModel? predecessor)
    {
        var target = SelectedTask.Value;
        if (target is null || predecessor is null) return;
        if (target.Predecessors.Remove(predecessor))
        {
            StatusMessage.Value = $"先行タスクを解除: {predecessor.Title}";
            RecomputeSelectablePredecessors();
        }
    }

    // 現在の SelectedTask に対して predecessor 候補になれるタスク一覧を返す。
    // 呼び出し側 (プロパティパネルの追加 ComboBox) が使用。
    public System.Collections.Generic.IEnumerable<TaskNodeViewModel> GetSelectablePredecessorsForSelected()
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

    // アローダイアグラム表示。親タスク context menu から呼ばれる。
    // Views/ArrowDiagramWindow を開く実装は次コミット。
    [RelayCommand]
    private void ShowArrowDiagram(TaskNodeViewModel? parent)
    {
        parent ??= SelectedTask.Value;
        if (parent is null || parent.IsLeaf) return;
        _arrowDiagramService?.Show(parent);
    }

    private IArrowDiagramService? _arrowDiagramService;
    internal void SetArrowDiagramService(IArrowDiagramService s) => _arrowDiagramService = s;
}
