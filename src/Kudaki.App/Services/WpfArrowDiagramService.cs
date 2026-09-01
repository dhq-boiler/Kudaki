using System.Windows;
using Kudaki.App.ViewModels;
using Kudaki.App.Views;

namespace Kudaki.App.Services;

public sealed class WpfArrowDiagramService : IArrowDiagramService
{
    private readonly Window _owner;

    public WpfArrowDiagramService(Window owner) => _owner = owner;

    public void Show(TaskNodeViewModel parent)
    {
        var vm = new ArrowDiagramViewModel(parent);
        var window = new ArrowDiagramWindow(vm) { Owner = _owner };
        window.Show(); // Modal ではなく普通のトップレベル (複数開けたほうが比較しやすい)
    }
}
