using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kudaki.App;
using Kudaki.App.ViewModels;

internal static class FocusRegression
{
    public static async Task VerifyAsync()
    {
        var app = new HarnessApp();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Kudaki;component/Themes/Palette.Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Kudaki;component/Themes/ControlStyles.xaml", UriKind.Relative) });
        var window = new MainWindow();
        // This harness does not run App.OnStartup, restore documents, or start MCP.
        app.MainWindow = null;
        window.Show();
        window.Activate();
        app.MainWindow = null;
        ((FrameworkElement)window.FindName("LandingOverlay")).Visibility = Visibility.Collapsed;
        try
        {
            var vm = (MainViewModel)window.DataContext;
            var doc = vm.ActiveDocument.Value!;
            doc.AddSiblingToSelectedCommand.Execute(null);
            await Idle();
            var tree = Descendants<TreeView>(window).Single();
            var first = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            Console.WriteLine($"Window active={window.IsActive}, visible={window.IsVisible}; row visible={first.IsVisible}, enabled={first.IsEnabled}, focusable={first.Focusable}; keyboard={Keyboard.FocusedElement}");
            Require(first.Focus(), "Initial row must receive keyboard focus.");

            for (var i = 1; i <= 2; i++)
            {
                if (i == 2)
                {
                    var detailTitle = Descendants<TextBox>(window).First(b => b.IsVisible && ReferenceEquals(b.DataContext, doc.SelectedTask.Value));
                    Require(detailTitle.Focus(), "Details field must receive focus before adding.");
                }
                ExecuteKey(i == 2 ? window : tree, Key.Enter);
                await Idle();
                var added = doc.RootTasks[i];
                Require(ReferenceEquals(tree.SelectedItem, added), "ENTER-1: Added row must be selected.");
                var container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(added);
                Console.WriteLine($"After Enter: newRowFocused={container.IsKeyboardFocusWithin}");
                ExecuteKey(tree.IsKeyboardFocusWithin ? tree : window, Key.F2);
                await Idle();
                Require(Keyboard.FocusedElement is TextBox box && ReferenceEquals(box.DataContext, added)
                    && added.IsEditing.Value && ReferenceEquals(doc.SelectedTask.Value, added),
                    "ENTER-2: F2 must focus the added row's title and keep editing active.");
                var editor = (TextBox)Keyboard.FocusedElement;
                editor.Text = $"Added {i}";
                Require(added.Title == $"Added {i}", "ENTER-3: Title input must update the new task.");
                doc.EndEditTitle(false);
                container.Focus();
                await Idle();
            }
            vm.NewDocumentCommand.Execute(null);
            await Idle();
            var secondDoc = vm.ActiveDocument.Value!;
            ExecuteKey(window, Key.Enter);
            await Idle();
            Require(secondDoc.RootTasks.Count == 1 && doc.RootTasks.Count == 3,
                "ENTER-4: Window-level Enter must add to the active tab only.");
            ExecuteKey(window, Key.F2);
            await Idle();
            Require(Keyboard.FocusedElement is TextBox newTabEditor
                && ReferenceEquals(newTabEditor.DataContext, secondDoc.RootTasks[0])
                && secondDoc.RootTasks[0].IsEditing.Value,
                "ENTER-4: Window-level F2 must focus the active tab's new title.");
            Console.WriteLine("PASS: ENTER-1, ENTER-2, ENTER-3, ENTER-4 (production window, selection, title focus, repeated additions, active tab)");
        }
        finally { window.Hide(); }
    }

    private static void ExecuteKey(UIElement tree, Key key)
    {
        var binding = tree.InputBindings.OfType<KeyBinding>().Single(b => b.Key == key && b.Modifiers == ModifierKeys.None);
        binding.Command.Execute(binding.CommandParameter);
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HarnessApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
}
