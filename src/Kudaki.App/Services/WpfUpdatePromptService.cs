using System.Threading.Tasks;
using System.Windows;
using Kudaki.App.ViewModels;
using Kudaki.App.Views;

namespace Kudaki.App.Services;

public sealed class WpfUpdatePromptService : IUpdatePromptService
{
    private readonly Window _owner;

    public WpfUpdatePromptService(Window owner) => _owner = owner;

    public Task<bool> PromptAndInstallAsync(UpdateInfo update)
    {
        var vm = new UpdatePromptViewModel(update);
        var window = new UpdatePromptWindow(vm) { Owner = _owner };
        var result = window.ShowDialog();
        return Task.FromResult(result == true);
    }
}
