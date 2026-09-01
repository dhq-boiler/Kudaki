# Kudaki v0.2 — MCP + diff承認

*更新: 2026-09-01 09:00*

- [ ] **設計** — 見積合計 12h / 残合計 12h / 進捗 0%
  - [ ] **アーキテクチャ確定 (in-process HTTP vs stdio 別プロセス)** — 見積 4h / 進捗 0%
    > project-mcp-roadmap の方針: HTTP/SSE を推奨、Kestrel in-process ホスト。
    > stdio 別プロセス案は配線が倍になるので却下したが、
    > Claude Desktop 側の対応状況で再検討の余地はある。
    > 
  - [ ] **MCP ツール定義 (get_document / propose_changes / get_pending_summary)** — 見積 3h / 進捗 0%
    > propose_changes は同期 await 方式で、AI から見ると1回呼んで結果を待つだけ。
    > get_pending_summary はフォールバック用。
    > 
  - [ ] **Diff データモデル設計 (PendingChange, FieldDiffs)** — 見積 3h / 進捗 0%
  - [ ] **承認フロー設計 (all-or-nothing 一括承認)** — 見積 2h / 進捗 0%
    > 個別承認は v0.3 で検討。
- [ ] **MCP サーバー基盤** — 見積合計 10h / 残合計 10h / 進捗 0%
  - [ ] **ModelContextProtocol NuGet 導入 と依存追加** — 見積 1h / 進捗 0%
  - [ ] **Kestrel in-process ホスト起動 (localhost 任意ポート)** — 見積 4h / 進捗 0%
    > WPF プロセス内で ASP.NET Core を起こす。UI スレッドと干渉しない構成に。
  - [ ] **サーバー起動/停止のライフサイクル管理 (App.OnStartup / OnExit)** — 見積 2h / 進捗 0%
  - [ ] **MCP tool 登録の骨組み** — 見積 3h / 進捗 0%
- [ ] **MCP ツール実装** — 見積合計 13h / 残合計 13h / 進捗 0%
  - [ ] **get_document ツール (読取専用、現在の WbsDocument を返す)** — 見積 2h / 進捗 0%
  - [ ] **propose_changes ツール (提案受付 + 承認/却下を await して返す)** — 見積 6h / 進捗 0%
    > TaskCompletionSource で承認/却下シグナルを待つ。
    > タイムアウト (デフォルト 5分) 到達で却下扱い。
    > 
  - [ ] **get_pending_summary ツール (現在ペンディング中の変更概要)** — 見積 2h / 進捗 0%
  - [ ] **エラー / タイムアウト処理 + ロギング** — 見積 3h / 進捗 0%
- [ ] **Diff エンジン** — 見積合計 18h / 残合計 18h / 進捗 0%
  - [ ] **WbsDocument 比較 (id ベース照合、Guid が安定していることを前提)** — 見積 4h / 進捗 0%
  - [ ] **PendingChange 生成 (Op=Add/Update/Delete + FieldDiffs)** — 見積 6h / 進捗 0%
  - [ ] **ネスト subtree の Add / Delete をまとめて1つの PendingChange に** — 見積 4h / 進捗 0%
  - [ ] **Unit test (round-trip / 順序入れ替え / subtree の挙動)** — 見積 4h / 進捗 0%
- [ ] **Diff 承認 UI** — 見積合計 17h / 残合計 17h / 進捗 0%
  - [ ] **Diff Panel の XAML レイアウト (右サイドバーに追加)** — 見積 4h / 進捗 0%
  - [ ] **追加/削除/変更の色分け (緑/赤/オレンジ、既存パレット準拠)** — 見積 3h / 進捗 0%
  - [ ] **承認 / 却下ボタンと確認フロー** — 見積 3h / 進捗 0%
  - [ ] **承認時の PendingChanges → Document 反映処理** — 見積 4h / 進捗 0%
  - [ ] **サーバー側 await 中コールへの結果通知 (TaskCompletionSource.SetResult)** — 見積 3h / 進捗 0%
- [ ] **設定 UI + ドキュメント** — 見積合計 8h / 残合計 8h / 進捗 0%
  - [ ] **MCP サーバー ON/OFF トグル (プロパティパネル外の設定エリア)** — 見積 2h / 進捗 0%
  - [ ] **リッスンポート表示 (Claude Code 用に URL コピー可)** — 見積 2h / 進捗 0%
  - [ ] **README に MCP セクション追記** — 見積 2h / 進捗 0%
  - [ ] **Claude Code 側の登録手順 (mcp.json 例) を docs/ に配置** — 見積 2h / 進捗 0%
