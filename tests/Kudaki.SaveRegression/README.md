# 保存・タイトル入力の回帰検証

Windows / .NET 10 で実行する、追加テストパッケージ不要の検証プログラム。

```powershell
dotnet run --project tests/Kudaki.SaveRegression/Kudaki.SaveRegression.csproj
dotnet run --project tests/Kudaki.SaveRegression/Kudaki.SaveRegression.csproj -- --focus
```

- 通常実行：本番 XAML の保存バインディングを使い、タブ切替後の保存先、非アクティブ文書の保護、未保存マーク、元のタブへの復帰を検証する。一時ファイルだけを使用する。
- `--focus`：本番 MainWindow を表示し、Enter / F2 に設定されたコマンドを実行する。ツリー・詳細欄からの追加、タイトル入力フォーカス、連続追加、タブ切替を検証する。OS のキー送信自体は検証しない。既存文書の復元、MCP 起動、設定の保存を行わず、検証用の空文書のみを使う。

失敗すると終了コード 1、成功すると終了コード 0 を返す。
