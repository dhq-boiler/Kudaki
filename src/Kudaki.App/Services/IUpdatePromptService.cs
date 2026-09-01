using System.Threading.Tasks;

namespace Kudaki.App.Services;

// VM が更新プロンプトの Window を直接生成しないための境界。
// View 側で WpfUpdatePromptService を実装、DI で VM に渡す。
public interface IUpdatePromptService
{
    // 更新のダウンロード + インストーラー起動までを担当。
    // 戻り値: true なら成功して起動した (呼び出し側は自身を終了すべき)、
    //         false ならユーザーキャンセル / 失敗 (継続してよい)。
    Task<bool> PromptAndInstallAsync(UpdateInfo update);
}
