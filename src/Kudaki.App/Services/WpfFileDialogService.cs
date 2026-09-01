using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Kudaki.App.Services;

// Microsoft.Win32 の OpenFileDialog / SaveFileDialog を IFileDialogService として提供。
// この実装だけが View レイヤに触れる。VM は IFileDialogService だけを知る。
public sealed class WpfFileDialogService : IFileDialogService
{
    private readonly Window _owner;

    public WpfFileDialogService(Window owner) => _owner = owner;

    public Task<string?> ShowOpenAsync(string filter, string title)
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title };
        var result = dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
        return Task.FromResult<string?>(result);
    }

    public Task<string?> ShowSaveAsAsync(string filter, string title, string defaultFileName, string defaultExt)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            Title = title,
            FileName = defaultFileName,
            DefaultExt = defaultExt,
            AddExtension = true
        };
        var result = dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
        return Task.FromResult<string?>(result);
    }
}
