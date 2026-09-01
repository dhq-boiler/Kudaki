using System.Windows;
using Kudaki.App.ViewModels;

namespace Kudaki.App.Views;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(UpdatePromptViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose = result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
