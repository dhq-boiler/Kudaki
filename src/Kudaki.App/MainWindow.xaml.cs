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
