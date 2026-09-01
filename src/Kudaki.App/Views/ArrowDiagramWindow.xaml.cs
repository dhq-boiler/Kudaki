using System.Windows;
using Kudaki.App.ViewModels;

namespace Kudaki.App.Views;

public partial class ArrowDiagramWindow : Window
{
    public ArrowDiagramWindow(ArrowDiagramViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
