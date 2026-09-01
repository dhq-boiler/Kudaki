using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Services;

namespace Kudaki.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly YamlStorageService _storage = new();
    private readonly MarkdownExportService _markdown = new();
    private WbsDocument _document = null!;
    private TaskNodeViewModel _rootVm = null!;
    private string? _currentFilePath;

    [ObservableProperty]
    private TaskNodeViewModel? _selectedTask;

    [ObservableProperty]
    private string _windowTitle = "Kudaki - 無題";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _statusMessage;

    public MainViewModel()
    {
        LoadDocument(new WbsDocument());
    }

    // TreeView.ItemsSource がこれをバインドする。
    // 仮想ルート (_rootVm) の Children を露出することで、top-level タスクも
    // Parent を持つ通常の VM として扱える (Indent/Outdent のロジック一本化)。
    public ObservableCollection<TaskNodeViewModel> RootTasks => _rootVm.Children;

    internal WbsDocument Document => _document;

    internal string? CurrentFilePath => _currentFilePath;

    // 新規/読込時にドキュメントを差し替える。仮想ルートの Model.Children を
    // Document.Tasks と同じ参照にすることで、VM の木構造の変更が Document 側に
    // 自動で反映される。
    internal void LoadDocument(WbsDocument document)
    {
        _document = document;
        var stubModel = new TaskNode
        {
            Id = "__root__",
            Children = document.Tasks
        };
        _rootVm = new TaskNodeViewModel(stubModel, parent: null);
        OnPropertyChanged(nameof(RootTasks));
        SelectedTask = null;
        IsDirty = false;
    }

    internal void SetCurrentFilePath(string? path)
    {
        _currentFilePath = path;
        WindowTitle = path is null
            ? "Kudaki - 無題"
            : $"Kudaki - {System.IO.Path.GetFileName(path)}";
    }

    // View 側から呼ぶ。ファイルダイアログは View の責務、VM は path を受け取るだけ。
    internal async Task<bool> LoadFromPathAsync(string path)
    {
        try
        {
            var doc = await _storage.LoadAsync(path).ConfigureAwait(true);
            LoadDocument(doc);
            SetCurrentFilePath(path);
            StatusMessage = $"読み込みました: {System.IO.Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"読み込み失敗: {ex.Message}";
            return false;
        }
    }

    internal async Task<bool> SaveToPathAsync(string path)
    {
        try
        {
            await _storage.SaveAsync(_document, path).ConfigureAwait(true);
            SetCurrentFilePath(path);
            IsDirty = false;
            StatusMessage = $"保存しました: {System.IO.Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失敗: {ex.Message}";
            return false;
        }
    }

    internal bool HasCurrentFilePath => _currentFilePath is not null;

    internal async Task<bool> ExportMarkdownAsync(string path)
    {
        try
        {
            await _markdown.ExportAsync(_document, path).ConfigureAwait(true);
            StatusMessage = $"Markdown エクスポートしました: {System.IO.Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Markdown エクスポート失敗: {ex.Message}";
            return false;
        }
    }

    // 現在ファイルパスから Markdown 出力の推奨ファイル名を生成
    internal string SuggestedMarkdownFileName()
    {
        if (_currentFilePath is null) return "untitled.md";
        var name = System.IO.Path.GetFileName(_currentFilePath);
        // .wbs.yaml → .md、.yaml → .md、それ以外は拡張子差し替え
        if (name.EndsWith(".wbs.yaml", StringComparison.OrdinalIgnoreCase))
            return name[..^".wbs.yaml".Length] + ".md";
        return System.IO.Path.ChangeExtension(name, ".md");
    }

    [RelayCommand]
    private void NewDocument()
    {
        LoadDocument(new WbsDocument());
        SetCurrentFilePath(null);
        StatusMessage = null;
    }

    // 選択なしなら top-level に追加、選択ありならその直後に兄弟追加。
    // Enter キーの主動作。
    [RelayCommand]
    private void AddSiblingToSelected()
    {
        if (SelectedTask is null)
        {
            var vm = new TaskNodeViewModel(new TaskNode(), _rootVm);
            _rootVm.Children.Add(vm);
            SelectedTask = vm;
            vm.IsSelected = true;
            return;
        }
        SelectedTask.AddSiblingAfterCommand.Execute(null);
    }

    // 選択タスクに子追加。選択なしなら top-level に追加 (兄弟追加と同じ扱い)。
    [RelayCommand]
    private void AddChildToSelected()
    {
        if (SelectedTask is null)
        {
            AddSiblingToSelected();
            return;
        }
        SelectedTask.AddChildCommand.Execute(null);
    }

    [RelayCommand]
    private void DeleteSelected() => SelectedTask?.DeleteCommand.Execute(null);

    [RelayCommand]
    private void IndentSelected() => SelectedTask?.IndentCommand.Execute(null);

    [RelayCommand]
    private void OutdentSelected() => SelectedTask?.OutdentCommand.Execute(null);

    [RelayCommand]
    private void MoveSelectedUp() => SelectedTask?.MoveUpCommand.Execute(null);

    [RelayCommand]
    private void MoveSelectedDown() => SelectedTask?.MoveDownCommand.Execute(null);
}
