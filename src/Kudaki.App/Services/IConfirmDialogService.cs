namespace Kudaki.App.Services;

// VM が MessageBox を直接触らないための境界。
// View 側で WpfConfirmDialogService を実装、DI で VM に渡す。
// t-tab-close の「保存 / 破棄 / キャンセル」3 択で使う (feedback_r3_and_mvvm_purity)。
public interface IConfirmDialogService
{
    ConfirmResult ShowSaveDiscardCancel(string message, string title);
}

public enum ConfirmResult
{
    Save,
    Discard,
    Cancel,
}
