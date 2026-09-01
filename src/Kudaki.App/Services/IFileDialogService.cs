using System.Threading.Tasks;

namespace Kudaki.App.Services;

// VM がファイルダイアログを直接触らないための境界。
// View 側で WpfFileDialogService を実装、DI で VM に渡す。
public interface IFileDialogService
{
    // 選ばれたパス、キャンセルなら null。
    Task<string?> ShowOpenAsync(string filter, string title);

    Task<string?> ShowSaveAsAsync(string filter, string title, string defaultFileName, string defaultExt);
}
