namespace Kudaki.App.Services;

// MCP の承認待ちが発生したことを気づかせるための境界。
// 実装 (WpfApprovalNotificationService) は SystemSounds と user32 P/Invoke を扱う View 層なので、
// VM 側はこの interface しか触らない (feedback_r3_and_mvvm_purity)。
//
// 背景: propose_changes を投げても承認 UI に気づかず default 5 分で timeout する事象が
// dogfood で頻発した。方針は「段階的エスカレーション」= 音・タスクバー点滅・タブバッジで
// 呼びかけるが、フォアグラウンド奪取はしない (他アプリでのタイピングを奪う事故を避ける)。
public interface IApprovalNotificationService
{
    // 新しい承認待ちが到着した。音・点滅・タスクバー表示・再催促タイマーを起動する。
    void NotifyPendingArrived();

    // 承認待ちが全ドキュメントで片付いた。鳴り物を全部止める。
    void Clear();
}
