using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var result = 0;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--focus")) await FocusRegression.VerifyAsync();
                else await VerifyAsync();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { dispatcher.InvokeShutdown(); }
        }));
        Dispatcher.Run();
        return result;
    }

    private static async Task VerifyAsync()
    {
        var directory = Directory.CreateTempSubdirectory("Kudaki-save-");
        try
        {
            var firstPath = Path.Combine(directory.FullName, "first.yaml");
            var secondPath = Path.Combine(directory.FullName, "second.yaml");
            const string original = "version: 2\ntasks:\n- id: task\n  title: Original\n";
            await File.WriteAllTextAsync(firstPath, original);
            await File.WriteAllTextAsync(secondPath, original);
            var vm = new MainViewModel(null!, null!, null!, null!, null!, new SilentNotifications(), null!);
            await vm.OpenInNewTabAsync(firstPath);
            var first = vm.ActiveDocument.Value!;

            // Read the production XAML so reverting a binding reproduces the regression.
            var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.source"));
            var bindings = xaml.Descendants()
                .Where(e => e.Name.LocalName is "KeyBinding" or "MenuItem")
                .Select(e => (string?)e.Attribute("Command"))
                .Where(s => s is not null && (s.EndsWith("SaveCommand}") || s.EndsWith("SaveAsCommand}")))
                .Select(s =>
                {
                    var target = new FrameworkElement();
                    target.SetBinding(FrameworkElement.TagProperty, new Binding(s![9..^1]) { Source = vm });
                    return target;
                }).ToArray();
            Require(bindings.Length == 4, "All four save entry points must be covered.");
            var save = bindings[0];
            first.RootTasks[0].Title = "First edited";
            await vm.OpenInNewTabAsync(secondPath);
            var second = vm.ActiveDocument.Value!;
            second.RootTasks[0].Title = "Second edited";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            foreach (var binding in bindings)
                Require(ReferenceEquals(binding.Tag, second.SaveCommand) || ReferenceEquals(binding.Tag, second.SaveAsCommand),
                    "BUG-SAVE-1: Save bindings must follow the selected tab.");
            await ((IAsyncRelayCommand)save.Tag).ExecuteAsync(null);
            Require((await File.ReadAllTextAsync(secondPath)).Contains("Second edited"), "BUG-SAVE-1: Selected file was not saved.");
            Require(await File.ReadAllTextAsync(firstPath) == original, "BUG-SAVE-2: Inactive file must not be overwritten.");
            Require(!second.IsDirty.Value && !second.TabHeaderText.Value.EndsWith(" *"), "BUG-SAVE-2: Saved tab must lose its dirty mark.");
            Require(first.IsDirty.Value && first.TabHeaderText.Value.EndsWith(" *"), "BUG-SAVE-2: Inactive edits must remain dirty.");

            vm.ActiveDocument.Value = first;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await ((IAsyncRelayCommand)save.Tag).ExecuteAsync(null);
            Require((await File.ReadAllTextAsync(firstPath)).Contains("First edited") && !first.IsDirty.Value,
                "BUG-SAVE-3: Switching back must save the original tab.");
            Console.WriteLine("PASS: BUG-SAVE-1, BUG-SAVE-2, BUG-SAVE-3 (WPF bindings, file contents, dirty marks)");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SilentNotifications : IApprovalNotificationService
    {
        public void NotifyPendingArrived() { }
        public void Clear() { }
    }
}
