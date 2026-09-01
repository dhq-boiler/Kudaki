# Kudaki

**Work Breakdown Structure エディター**。Excel より速く/楽にタスクを砕くことに全振りした WPF アプリ。

![screenshot](docs/screenshot.png)

## コンセプト

Kudaki (砕き) はタスクを細かく砕いて計画するためのエディターです。既存の WBS ツール (Excel が事実上のデファクト) に対して、**キーボードだけで完結する編集体験**と**残時間ベースの日次更新**で差別化しています。

主なゴール:

- ✅ **キーボード優先**: Enter で兄弟追加、Tab でインデント、Shift+Tab でアウトデント。マウスを触らずに階層を組める
- ✅ **残時間モデル**: 毎日「残り何時間か」だけ更新すれば、実績と進捗率が自動で派生計算される
- ✅ **砕き警告**: 見積が過大な葉タスク (デフォルト 40h 超) に ⚠ を付けて「まだ砕けます」と提示。名前の由来そのもの
- ✅ **YAML 保存 / Markdown エクスポート**: GitHub 上でそのままレビューできる、差分も取れる、AI エージェントに書かせやすい
- ✅ **ダーク UI 固定**

## インストール

[Releases](https://github.com/dhq-boiler/Kudaki/releases/latest) から `KudakiSetup.exe` をダウンロードして実行してください (Windows 10/11 x64, ユーザー領域インストール、管理者権限不要)。

.NET ランタイムは同梱されているので個別インストール不要です。

## 使い方

### 起動

- スタートメニューから **Kudaki**
- または `Kudaki.exe path/to/plan.wbs.yaml` でファイル直接オープン
- `.wbs.yaml` ファイルをウィンドウにドラッグ&ドロップでも開けます

### キーバインド (ツリー編集)

| キー | 動作 |
|---|---|
| `Enter` | 選択タスクの直後に兄弟を追加 (空なら top-level に追加) |
| `Alt+Enter` | 選択タスクの子を追加 |
| `Tab` | 選択タスクをインデント (前の兄弟の子にする) |
| `Shift+Tab` | 選択タスクをアウトデント (親の兄弟にする) |
| `Ctrl+↑` / `Ctrl+↓` | 選択タスクを並び替え |
| `Delete` | 選択タスクを削除 (子ごと) |

### キーバインド (ファイル)

| キー | 動作 |
|---|---|
| `Ctrl+N` | 新規 |
| `Ctrl+O` | 開く |
| `Ctrl+S` | 上書き保存 |
| `Ctrl+Shift+S` | 名前を付けて保存 |
| `Ctrl+E` | Markdown エクスポート |

### 残時間モデル

Kudaki では「実績」と「進捗率」は**入力欄がありません**。代わりに毎日「残時間」を更新するだけで、以下が派生計算されます:

- **消化** (実績) = `max(0, 見積 - 残)`
- **進捗** = `(見積 - 残) / 見積` を 0〜100 に clamp

「残 = null (未入力)」は未着手扱い、「残 = 0」は完了扱い。

## 保存形式

`.wbs.yaml` 拡張子の [YAML](https://yaml.org/) ファイル。人間可読で diff も取れる、AI エージェントが直接書くのも簡単です。実サンプルは [docs/v02-plan.wbs.yaml](docs/v02-plan.wbs.yaml) を参照。

Markdown エクスポートは GitHub 貼り付け用の task-list 形式です ([docs/v02-plan.md](docs/v02-plan.md))。

## 技術スタック

- C# / WPF / **.NET 10**
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (RelayCommand 生成) + [**Cysharp/R3**](https://github.com/Cysharp/R3) (`BindableReactiveProperty` で観測可能状態)
- YAML I/O: [YamlDotNet](https://github.com/aaubry/YamlDotNet)
- MVVM 純度: コードビハインドは View 固有配線のみ (ダイアログは `IFileDialogService`、drag&drop は Attached Behavior、キーバインドは XAML)

## ロードマップ

- **v0.2**: [MCP サーバー + diff 承認 UI](docs/v02-plan.wbs.yaml) — AI エージェントに WBS を書かせてレビューだけで済ませる
- **v0.3+**: 個別承認、日次残時間履歴による burndown、ガントチャート派生など

## ライセンス

MIT — [LICENSE](./LICENSE) を参照。

## Author

[@dhq_boiler](https://github.com/dhq-boiler)
