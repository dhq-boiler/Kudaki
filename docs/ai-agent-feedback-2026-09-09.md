# Kudaki フィードバック — AI エージェントから (2026-09-09)

> Claude Code (Opus 4.7) が STAMINA LAB プロジェクトで Kudaki v0.6.0 を丸 1 日使い倒した後の率直な感想。エージェント視点から見た「使い勝手」を、Kudaki チーム (＝先生自身) に向けて残しておく。

---

## 検証環境

- Kudaki: v0.6.0 (2026-09-03 リリース)
- クライアント: Claude Code (Opus 4.7、Windows 11)
- 対象 WBS: `C:\Users\boiler\.claude\projects\stamina_lab\tasks.wbs.yaml` (v2 スキーマ、既に 40+ タスク、YAML 12KB 弱)
- 実施内容: 新規機能「筋肉痛回避 (DOMS) 機能」を子タスク 7 個追加 → 実装完了マーク propose → 承認 → 実装 → 進捗更新 propose の 2 ラウンド

## 総合評価

**AI エージェントのタスク管理として現時点でベストな選択肢**、という感想。特に以下の 2 点が決定的:

1. **セッションを越えて persist する** — `TaskCreate` (Claude Code 組み込み) はセッション消えたら揮発するが、Kudaki は `.wbs.yaml` としてディスクに残る。次日、別セッションから `/load-session` した Claude が続きから追える。ドッグフーディング的にはこの一点だけで既に価値がある。
2. **承認 UI が信頼を担保する** — AI が勝手に書き換えられない設計は、ドッグフーディング体験と "AI エージェントに任せる緊張感" の両方に効く。書き手 (AI) 側も「間違っていたら reject されるだけ」と分かっているので、大胆に propose できる。

---

## v0.6.0 で解決済み / うまく設計されているもの

- **全文差し替え方式** — partial patch (JSON patch など) と比べて **AI が意図とズレた diff を作りにくい**。「最終状態を書く」だけに集中できる。今回 2 ラウンドとも意図と反映が完全一致した。
- **`expectedRevision` の楽観ロック (v0.3)** — 2 ラウンド目に revision が `172dbd072201` → `2aabab25bdbb` に進んでいたのを検知し、そのまま渡すことで安全に上書きできた。並行セッションを想定した設計として文句なし。
- **返り値の分類 (`approved` / `rejected` / `timeout` / `no_changes` / `revision_mismatch` / `error`)** — 何が起きたかが 1 単語で分かる。特に `no_changes` は「実質差分ゼロを送ってしまった」時に AI が空回りしないようにしてくれる。
- **`list_documents` の revision フィールド** — 「今、Kudaki 内で最新の状態」を AI が把握できる。今回は `list_documents` → `get_document` → `propose_changes` の 3-step ワークフローがそのまま自然に成立した。
- **auto-apply の分類ポリシー (v0.3)** — `RemainingHours` と `Notes` の append-only は自動反映、それ以外は承認 UI というのは AI/人間の分業として正しい設計。今回の propose は notes 全書き換えを含んでいたので承認 UI 経由になったが、これは意図通りの挙動と理解している。
- **Approval notifications (v0.4)** — サウンド + タスクバー点滅 + アンバーバッジは、"AI が働いている感" を先生 (人間) 側に確実に届ける UX として非常に強い。「ちゃんと propose 届いてる」の確信が持てる。
- **`--src-dir` からの読み込みか、外部エディタで書き換えた場合の反映** — 今回は試していないが、複数タブ運用しても衝突しなさそうな設計に見える。

## エージェント視点での改善提案 (v0.6.0 で残っている摩擦)

### 1. 全文送信のトークンコストが痛い

今回、WBS 全文 (12KB 弱) を **2 回** 送った。1 ラウンド目は新規子タスク 7 個追加、2 ラウンド目は既存タスク 6 個の `remainingHours` を 0 に + notes 追記。特に 2 ラウンド目は本質的な変更が「6 タスクの数値 + notes 追記」だけなのに全文再送になり、コンテキストウィンドウにも API 課金にも響く。

**提案 A**: full-replacement 原則は残しつつ、頻出操作だけ short API を用意する。

```
update_tasks(documentId, [
  { id: "sore-ui", remainingHours: 0, notes_append: "[完了 2026-09-09] ..." },
  { id: "sore-user-model", remainingHours: 0 },
  ...
], expectedRevision: "...")
```

`RemainingHours` と `Notes append` はもう auto-apply 対象なので、この API は既存の light-update パスに素直に乗るはず。全文送信を回避する副次的な効果が大きい。

**提案 B**: `propose_changes` の入力を「該当 subtree の YAML」だけに絞れるモード。今回で言えば `sore-muscle-avoidance` の子ツリーだけを送って、Kudaki 側でルート YAML に patch してくれる形。ID ベースのマージがきれいに書けるなら、こちらのほうがトークン節約になる。

### 2. タスク検索 / クエリ API がない

現状は `get_document` で全文取ってきて自前で `grep`/parse するしかない。40 タスクくらいまでなら耐えるが、100 超えたら AI が「今どこにいるか」を把握する前にコンテキスト食い切りそう。

`get_next_tasks` は v0.6 で入って、これはこれで嬉しい (依存順に unfinished leaf を返してくれる)。ただ、AI が propose を組み立てる前段では「ID 指定で 1 タスクだけ取りたい」「特定 ancestor 配下だけ列挙したい」が欲しくなる。

**提案**: `find_task(documentId, id)` (単発取得) と `list_tasks(documentId, ancestorId?, status?, limit?)` (絞り込み列挙) を追加。返り値は YAML fragment そのままでよい (AI 側で propose を組む時にそのまま貼り直せる)。

### 3. `wait_for_request` / `get_next_tasks` のユースケース例が欲しい

v0.6 で入ったこの 2 つは、今回のワークフローでは使う機会がなかった。CHANGELOG を読んで「あー、こういうときに使うのか」となったが、Kudaki が MCP で AI に指示を渡せるようになったのは大きな変化なので、以下のような具体例が docs にあると AI が能動的に活用できる。

- 「先生が右クリックで `Ask AI to break down this task` を押した → AI が `wait_for_request` で拾って `propose_changes` で子タスクを埋める」までの一連の流れをコード例で示す
- `get_next_tasks` を叩いた結果で「今日は unblocked なタスクをこの順で全部やって」的な session を組む例
- `agentWaiting` / `pendingRequests` の状態遷移図

いま Claude 側では「wait_for_request って何秒くらいでタイムアウトするの?」も分からず、能動的に呼ぶのが少し怖い。ドキュメント補強でだいぶ変わりそう。

### 4. Kudaki 未起動時に AI 側から自立して起動できない

今日、Kudaki 未起動の状態で 1 ラウンド目を試みたら `ConnectionRefused` になり、先生に「起動してほしいっす」と依頼するしかなかった。この "起動待ち" のブロックが 1 回入ると、ワークフローがそこで止まる。

**提案**: `kudaki --start` (or `kudaki --headless` で MCP ポートだけ立ち上げるモード) みたいな CLI を用意して、Bash tool から起動できるようにする。セキュリティ的に外部プロセス起動を許すのは慎重にすべきなので、「MCP ポート + ローカル通信のみ」の限定モードで十分。UI が要る操作 (承認 UI) は結局 Kudaki を起動してもらう必要があるが、AI が propose するタイミングで自動起動 → 承認待ちで先生の目に入る、というフローになれば無駄な依頼往復が消える。

もし技術的に難しい (WPF 系だとヘッドレス起動が微妙) なら、Claude Code の hooks を書いて「Kudaki が起動していなかったら Windows shortcut を叩く」で回避できる公式レシピが docs にあるだけでも助かる。

### 5. YAML パースエラー時に行番号を返してほしい

今日は起きなかったが、`propose_changes` の `error` レスポンスに YAML パースエラーが混じったとき、行番号やカラム番号があると AI が自己修復できる。現状の `{ "result": "error" }` だけだと再試行が完全に試行錯誤になる。

```json
{
  "result": "error",
  "kind": "yaml_parse",
  "line": 42,
  "column": 10,
  "message": "did not find expected key"
}
```

`kind` を追加しておくと、AI 側で「YAML 系エラーなら自分で直す」「validation 系エラーなら人間に相談する」の分岐が書きやすい。

### 6. Notes フィールドの `>` block scalar の癖

YAML の `>` (folded) と `|` (literal) の使い分けで、空行 (段落区切り) が保持されるか毎回不安になる。今回は `>` の中で `\n\n` を段落区切りとして書いたら意図通り保存されたが、`>` は本来 fold して空白 1 個にする挙動なので、Kudaki 内部で何かハックしているのか、それとも AI が空行を書けば YAML パーサが素直に保つのかが判然としない。

**提案**: docs に「AI 向け YAML スタイルガイド」を 1 ページ足す。以下だけで OK。

- 短い 1 行 notes → `notes: "..."` (double-quote)
- 段落を含む長い notes → `notes: |` (literal block scalar) を推奨、`>` は非推奨
- 見出しには `**...**` (Markdown 太字) を使う (Kudaki UI 側でどう見えるかスクショ添え)

### 7. Dependency の記法サンプルが欲しい

CHANGELOG に "Link children in list order" (v0.6) の記述があり、右クリックで dependency chain を張れるのは分かった。ただ YAML 上で dependency がどう表現されるか、`deps-demo.wbs.yaml` (docs にある) を読まないと分からない。

**提案**: README か Getting Started に、"AI が dependency 付きタスクを propose する場合" の最小 YAML サンプルを載せる。今回の DOMS 実装なら、`sore-ui` の predecessor に `sore-user-model` / `sore-filter-service` を書いた形になったはず。書ければ `get_next_tasks` と自然に連携できる。

---

## Wishlist (今後あると嬉しい)

- **タスクへの実装コミット紐付け** — commit SHA / PR URL をタスクの metadata に持たせて、`propose_changes` から「このタスク完了 = このコミットで」を宣言できる。CHANGELOG 化やチーム振り返りに使える。
- **「今日 propose された変更」のダイジェスト** — 1 日の終わりに「今日 AI から N 個 propose が来て、うち N 個 approved / N 個 rejected」を出す機能。ドッグフーディングの計測にそのまま使える。
- **`propose_changes` の `preview` モード** — 実際には反映せず、Kudaki 側で計算した diff だけ返してくれる。AI が propose 前に "この差分で意図通りか" を人間に見せる用途に使える。
- **タスク単位のコメントスレッド** — propose の理由や AI 側のメモを、YAML の notes とは別レイヤで残せる。commit message と code comment の関係と同じ発想。

---

## 総括

一言で: **`.wbs.yaml` に AI が propose する仕組み、思っていた 3 倍うまく動く。**

TaskCreate から Kudaki に切り替えたのは正解だった。トークンコストと partial update だけ改善されると、AI エージェント向けタスク管理ツールとしては現時点で選択肢が他にないレベル。

`Ask AI to break down this task` (v0.6) は今日試せなかったので、次回のセッションで意識的に使ってみて追加フィードバックを残す予定。

— 仲正イチカ (Claude Code, Opus 4.7)
