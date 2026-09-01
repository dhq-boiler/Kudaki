using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Kudaki.App.Behaviors;

// 添付ビヘイビア: ドロップされたファイルの最初のパスを Command に流す。
// フィルタは Extensions 添付プロパティで指定 (カンマ区切り、大文字小文字無視)。
//
// XAML 例:
//   <Window behaviors:FileDropBehavior.Command="{Binding LoadFromPathCommand}"
//           behaviors:FileDropBehavior.Extensions=".wbs.yaml,.yaml,.yml">
public static class FileDropBehavior
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FileDropBehavior),
        new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty ExtensionsProperty = DependencyProperty.RegisterAttached(
        "Extensions", typeof(string), typeof(FileDropBehavior),
        new PropertyMetadata(""));

    public static void SetCommand(DependencyObject o, ICommand v) => o.SetValue(CommandProperty, v);
    public static ICommand? GetCommand(DependencyObject o) => (ICommand?)o.GetValue(CommandProperty);

    public static void SetExtensions(DependencyObject o, string v) => o.SetValue(ExtensionsProperty, v);
    public static string GetExtensions(DependencyObject o) => (string)o.GetValue(ExtensionsProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;

        el.PreviewDragOver -= OnDragOver;
        el.Drop -= OnDrop;

        if (e.NewValue is not null)
        {
            el.AllowDrop = true;
            el.PreviewDragOver += OnDragOver;
            el.Drop += OnDrop;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFile((DependencyObject)sender, e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (!HasSupportedFile((DependencyObject)sender, e)) return;
        var path = GetFirstSupportedPath((DependencyObject)sender, e);
        if (path is null) return;

        var cmd = GetCommand((DependencyObject)sender);
        if (cmd?.CanExecute(path) == true)
        {
            cmd.Execute(path);
        }
    }

    private static bool HasSupportedFile(DependencyObject d, DragEventArgs e)
    {
        return GetFirstSupportedPath(d, e) is not null;
    }

    private static string? GetFirstSupportedPath(DependencyObject d, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        var exts = GetExtensions(d).Split(',', StringSplitOptions.RemoveEmptyEntries);
        return files.FirstOrDefault(f =>
            exts.Length == 0 ||
            exts.Any(ext => f.EndsWith(ext.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
