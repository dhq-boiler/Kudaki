using System.Windows;
using Kudaki.App.Services;

namespace Kudaki.App.Views;

// タブ close 時の「未保存の変更」3 択ダイアログ。
// OS ネイティブ MessageBox がダークテーマに追従しないため、Kudaki のパレットで再実装。
// state を持たないので ViewModel は無し、code-behind で結果を受け取る。
public partial class ConfirmDialog : Window
{
    public ConfirmResult Result { get; private set; } = ConfirmResult.Cancel;

    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;              // Window.Title (使わないがタスクバー等で参照される)
        DialogTitleText.Text = title;
        MessageText.Text = message;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Save;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Discard;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Cancel;
        DialogResult = false;
    }
}
