# Kudaki

Work Breakdown Structure エディター。タスクをキーボードだけで速く砕くための WPF アプリ。

## コンセプト

**Excel より速く/楽に WBS を書く**ことに全振り。豪華な機能より入力速度を優先します。

- キーボード優先: Enter で同階層追加 / Tab でインデント / Shift+Tab でアウトデント
- 「まだ砕けます」警告: 見積工数が過大な葉タスクを検出（Kudaki = 砕き）
- ダークテーマ固定
- 保存形式は YAML（`.wbs.yaml`）、共有用に Markdown エクスポート

## 技術スタック

- C# / WPF / .NET 10
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- YAML I/O: [YamlDotNet](https://github.com/aaubry/YamlDotNet)

## ステータス

現在 v0.1 (MVP) 開発中。

## ライセンス

MIT — [LICENSE](./LICENSE) を参照。
