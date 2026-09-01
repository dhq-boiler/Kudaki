using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;
using Microsoft.Win32;

namespace Kudaki.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        OpenButton.Click += async (_, _) => await OpenFileAsync();
        SaveButton.Click += async (_, _) => await SaveAsync();
        SaveAsButton.Click += async (_, _) => await SaveAsAsync();
        ExportMarkdownButton.Click += async (_, _) => await ExportMarkdownAsync();

        // ファイル系のショートカット (Tree にフォーカスがあっても撃てるように Window スコープ)
        InputBindings.Add(new KeyBinding(
            new RelayFileCommand(async () => await OpenFileAsync()),
            new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayFileCommand(async () => await SaveAsync()),
            new KeyGesture(Key.S, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayFileCommand(async () => await SaveAsAsync()),
            new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(
            new RelayFileCommand(async () => await ExportMarkdownAsync()),
            new KeyGesture(Key.E, ModifierKeys.Control)));
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Vm.SelectedTask = e.NewValue as TaskNodeViewModel;
    }

    private async System.Threading.Tasks.Task OpenFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = YamlStorageService.OpenFilter,
            Title = "WBS ファイルを開く"
        };
        if (dlg.ShowDialog(this) != true) return;
        await Vm.LoadFromPathAsync(dlg.FileName);
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (!Vm.HasCurrentFilePath)
        {
            await SaveAsAsync();
            return;
        }
        await Vm.SaveToPathAsync(Vm.CurrentFilePath!);
    }

    private async System.Threading.Tasks.Task SaveAsAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = YamlStorageService.SaveFilter,
            Title = "名前を付けて保存",
            DefaultExt = YamlStorageService.PrimaryExtension,
            AddExtension = true,
            FileName = Vm.CurrentFilePath is null
                ? "untitled.wbs.yaml"
                : System.IO.Path.GetFileName(Vm.CurrentFilePath)
        };
        if (dlg.ShowDialog(this) != true) return;
        await Vm.SaveToPathAsync(dlg.FileName);
    }

    // .wbs.yaml / .yaml / .yml をウィンドウにドロップで直接開く。
    // ドロップ中はカーソルを Copy に、それ以外なら None にする。
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!HasSupportedFile(e)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var path = files.FirstOrDefault(IsSupportedPath);
        if (path is null) return;
        await Vm.LoadFromPathAsync(path);
    }

    private static bool HasSupportedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files?.Any(IsSupportedPath) == true;
    }

    private static bool IsSupportedPath(string path)
    {
        return path.EndsWith(".wbs.yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task ExportMarkdownAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = MarkdownExportService.SaveFilter,
            Title = "Markdown エクスポート",
            DefaultExt = MarkdownExportService.PrimaryExtension,
            AddExtension = true,
            FileName = Vm.SuggestedMarkdownFileName()
        };
        if (dlg.ShowDialog(this) != true) return;
        await Vm.ExportMarkdownAsync(dlg.FileName);
    }
}

// ICommand の最小実装。InputBindings に async 動作を紐付けるためだけの薄いラッパ。
internal sealed class RelayFileCommand : ICommand
{
    private readonly System.Func<System.Threading.Tasks.Task> _execute;

    public RelayFileCommand(System.Func<System.Threading.Tasks.Task> execute) => _execute = execute;

    public event System.EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await _execute();
}
