using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Kudaki.App.Models;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;
using ModelContextProtocol.Server;

namespace Kudaki.App.Services.Mcp;

// AI エージェント (Claude Code / Claude Desktop 等) に公開する MCP tools。
//
// v0.3 スキーマ変更 (sec-mcp-schema):
//   list_documents         — 現在開いてる全 doc の一覧 (最初に AI が呼ぶ)
//   get_document           — 指定 documentId の read-only スナップショット
//   propose_changes        — 指定 documentId への変更提案 + ユーザー承認 await
//
// documentId は絶対パス。AI は list_documents で得た documentId を get_document /
// propose_changes に渡す。未保存 doc は list_documents に出ないので触れない。
// 「アクティブ doc へのフォールバック」は事故の元なので採用しない (Fable レビュー指摘)。
[McpServerToolType]
public static class KudakiMcpTools
{
    // 使い回しのシリアライザ (静的で 1 個持てば十分)。
    private static readonly YamlStorageService _yaml = new();

    [McpServerTool(Name = "list_documents")]
    [Description(
        "List all WBS documents currently open in Kudaki. Returns a JSON array of " +
        "{documentId, filePath, title, isActive, isDirty, revision, agentWaiting, pendingRequests}. " +
        "`documentId` is the absolute file path " +
        "and is the required key for get_document and propose_changes. `revision` is a short hash of the " +
        "current document state; pass it to propose_changes as `expectedRevision` to reject stale proposals " +
        "that would overwrite concurrent user or AI edits. Unsaved (untitled) documents are NOT listed — " +
        "they cannot be addressed by MCP tools until saved. Call this FIRST before get_document / " +
        "propose_changes to pick the correct target and revision. `pendingRequests` is the number of " +
        "user requests waiting for an agent on that document — call wait_for_request to pick them up.")]
    public static string ListDocuments()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var docs = dispatcher is null || dispatcher.CheckAccess()
            ? DocumentRegistry.Instance.ListDocuments()
            : dispatcher.Invoke(() => DocumentRegistry.Instance.ListDocuments());

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < docs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var d = docs[i];
            sb.Append('{');
            sb.Append("\"documentId\":").Append(JsonString(d.DocumentId)).Append(',');
            sb.Append("\"filePath\":").Append(JsonString(d.FilePath)).Append(',');
            sb.Append("\"title\":").Append(JsonString(d.Title)).Append(',');
            sb.Append("\"isActive\":").Append(d.IsActive ? "true" : "false").Append(',');
            sb.Append("\"isDirty\":").Append(d.IsDirty ? "true" : "false").Append(',');
            sb.Append("\"revision\":").Append(JsonString(d.Revision)).Append(',');
            sb.Append("\"agentWaiting\":").Append(d.AgentWaiting ? "true" : "false").Append(',');
            sb.Append("\"pendingRequests\":").Append(d.PendingRequests);
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    [McpServerTool(Name = "get_document")]
    [Description(
        "Return the specified WBS document as YAML text. `documentId` (absolute file path) is required; " +
        "obtain it from list_documents. Returns a JSON error object {\"result\":\"unknown_document\", ...} " +
        "if the documentId does not match any currently open document. Read-only snapshot.")]
    public static string GetDocument(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
        }
        return doc.GetDocumentYamlSnapshot();
    }

    [McpServerTool(Name = "propose_changes")]
    [Description(
        "Propose a full replacement of a specific WBS document. Both `documentId` (absolute file path from " +
        "list_documents) and `yaml` (full replacement content, same format get_document returns) are required. " +
        "Kudaki diffs it against that document. Behavior depends on user's auto-apply policy AND whether the " +
        "change qualifies as \"light\" (RemainingHours updates or Notes append-only): " +
        "if enabled and all diffs qualify, applies immediately without approval UI (returns `auto_applied`); " +
        "otherwise shows the diff in that document's tab review UI and waits for approval or rejection " +
        "(default timeout: 5 minutes). Use `requireApproval=true` to force manual approval regardless of " +
        "the auto-apply policy (recommended for changes you want the user to explicitly review). " +
        "You CANNOT loosen the policy from the AI side; only tighten (this is by design). " +
        "Returns a JSON string with a `result` field: `auto_applied` / `approved` / `rejected` / `timeout` / " +
        "`no_changes` / `unknown_document` / `revision_mismatch` / `error`.")]
    public static async Task<string> ProposeChanges(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Full replacement WBS document as YAML text (same format get_document returns). Required.")]
        string yaml,
        [Description("Optional caller identification, shown to the user in the review UI (e.g. 'Claude Code')")]
        string source = "AI agent",
        [Description("Optional approval timeout in seconds (default 300 = 5 minutes)")]
        int timeoutSeconds = 300,
        [Description("If true, force manual approval UI even when the change qualifies for auto-apply. " +
                     "AI can only tighten the policy (not loosen it), so setting this to false has no effect " +
                     "when the user has auto-apply disabled.")]
        bool requireApproval = false,
        [Description("Optional. Expected current revision (from list_documents). If specified and does not " +
                     "match the current server-side revision, the propose is rejected with " +
                     "`result:revision_mismatch` WITHOUT showing UI, to prevent overwriting concurrent edits " +
                     "made after your last snapshot. Recommended workflow: list_documents (get revision) → " +
                     "get_document → build proposal → propose_changes with expectedRevision.")]
        string? expectedRevision = null,
        // SDK がツールメソッドに注入する。クライアント切断 (Claude Code の Ctrl+C 等) で
        // 承認待ちを解放するために必要。渡さないと待機が残り続ける。
        System.Threading.CancellationToken ct = default)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
        }

        // v03-mcp-auto-apply t-revision-check: 呼び出し側が「取得時点」の revision を渡した場合、
        // その後にユーザーや別 AI が編集して revision が変わっていれば reject。
        // AI に「最新を再取得してからやり直せ」を明示的に返す (承認 UI に流さない、上書き事故防止)。
        if (!string.IsNullOrEmpty(expectedRevision))
        {
            var currentRevision = doc.GetRevision();
            if (!string.Equals(currentRevision, expectedRevision, StringComparison.OrdinalIgnoreCase))
            {
                return $"{{\"result\":\"revision_mismatch\",\"expected\":{JsonString(expectedRevision)},\"current\":{JsonString(currentRevision)}}}";
            }
        }

        WbsDocument proposed;
        try
        {
            proposed = _yaml.DeserializeFromString(yaml);
        }
        catch (WbsLoadException wex) when (wex.InnerException is YamlDotNet.Core.YamlException yex)
        {
            // #5: YAML パースエラーは line / column を含めて AI が自己修復できる形で返す。
            return YamlParseErrorJson(yex);
        }
        catch (YamlDotNet.Core.YamlException yex)
        {
            return YamlParseErrorJson(yex);
        }
        catch (Exception ex)
        {
            return Json("error", $"YAML parse failed: {ex.Message}");
        }

        return await ApplyProposedAsync(doc, proposed, source, timeoutSeconds, requireApproval, ct).ConfigureAwait(false);
    }

    // v0.7 t-update-tasks-api: propose_changes の short-form。
    // RemainingHours 更新 と Notes 追記だけを id ベースで受ける。全文送信を回避してトークン節約。
    // 内部で current YAML を clone して updates を model 上に apply、後は propose_changes と同じ
    // DiffCalculator + auto-apply / 承認 UI パイプに合流させる。
    [McpServerTool(Name = "update_tasks")]
    [Description(
        "Short-form API for the common case of updating RemainingHours and appending to Notes on existing " +
        "tasks. You pass a list of updates keyed by taskId — Kudaki clones the current document, applies the " +
        "updates internally, and runs the same diff + auto-apply + approval pipeline as propose_changes. " +
        "When the user has auto-apply enabled and all your updates qualify as light (RemainingHours and/or " +
        "Notes append), the change is applied without the approval UI (returns `auto_applied`). " +
        "Adding or removing tasks, changing titles, estimates, dependencies, or restructuring the tree is " +
        "NOT supported here — use propose_changes for those. " +
        "Returns the same JSON shape as propose_changes: `auto_applied` / `approved` / `rejected` / `timeout` / " +
        "`no_changes` / `unknown_document` / `unknown_task` / `revision_mismatch` / `error`.")]
    public static async Task<string> UpdateTasks(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Array of update entries. Each entry: {\"id\":\"task-id\",\"remainingHours\":<number>,\"notesAppend\":\"text\"}. " +
                     "`id` is required and must match an existing taskId. At least one of `remainingHours` or " +
                     "`notesAppend` must be present per entry. `notesAppend` is appended to the existing Notes " +
                     "with a blank line separator when Notes is non-empty (append-only, so it stays on the " +
                     "auto-apply path). To rewrite Notes wholesale, add tasks, change other fields, or move " +
                     "things around, call propose_changes instead.")]
        TaskUpdate[] updates,
        [Description("Optional caller identification, shown to the user in the review UI (e.g. 'Claude Code')")]
        string source = "AI agent",
        [Description("Optional approval timeout in seconds (default 300 = 5 minutes). Only used when the change " +
                     "falls off the auto-apply path (e.g. user disabled auto-apply, or requireApproval=true).")]
        int timeoutSeconds = 300,
        [Description("If true, force manual approval UI even when the change qualifies for auto-apply. " +
                     "AI can only tighten the policy (not loosen it).")]
        bool requireApproval = false,
        [Description("Optional. Expected current revision (from list_documents). If specified and does not " +
                     "match the current server-side revision, the propose is rejected with " +
                     "`result:revision_mismatch` WITHOUT touching the document.")]
        string? expectedRevision = null,
        System.Threading.CancellationToken ct = default)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
        }

        if (updates is null || updates.Length == 0)
        {
            return Json("error", "updates array is empty; specify at least one update entry.");
        }

        // revision check (propose_changes と同じ挙動)
        if (!string.IsNullOrEmpty(expectedRevision))
        {
            var currentRevision = doc.GetRevision();
            if (!string.Equals(currentRevision, expectedRevision, StringComparison.OrdinalIgnoreCase))
            {
                return $"{{\"result\":\"revision_mismatch\",\"expected\":{JsonString(expectedRevision)},\"current\":{JsonString(currentRevision)}}}";
            }
        }

        // 現在の Document を YAML round-trip で clone してから update を適用する。
        // 直接 doc.Document を触ると承認前に UI に見える状態を書き換えることになるので絶対 NG。
        WbsDocument proposed;
        try
        {
            proposed = _yaml.DeserializeFromString(doc.GetDocumentYamlSnapshot());
        }
        catch (Exception ex)
        {
            return Json("error", $"Failed to clone current document: {ex.Message}");
        }

        var map = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        foreach (var t in proposed.Tasks) FlattenTasksInto(t, map);

        var unknownIds = new List<string>();
        var invalidEntries = new List<string>();
        foreach (var u in updates)
        {
            if (u is null || string.IsNullOrEmpty(u.Id))
            {
                invalidEntries.Add("<missing id>");
                continue;
            }
            if (u.RemainingHours is null && u.NotesAppend is null)
            {
                invalidEntries.Add(u.Id);
                continue;
            }
            if (!map.TryGetValue(u.Id, out var node))
            {
                unknownIds.Add(u.Id);
                continue;
            }

            if (u.RemainingHours.HasValue) node.RemainingHours = u.RemainingHours;
            if (u.NotesAppend is not null)
            {
                if (string.IsNullOrEmpty(node.Notes))
                {
                    node.Notes = u.NotesAppend;
                }
                else
                {
                    // 段落区切りの空行を挟む (Kudaki UI では Markdown として描画される)。
                    // append-only 判定は「After が Before で始まる」なので prefix 一致は維持される。
                    node.Notes = node.Notes + "\n\n" + u.NotesAppend;
                }
            }
        }

        if (unknownIds.Count > 0)
        {
            return Json("unknown_task", $"Task ids not found: {string.Join(", ", unknownIds)}");
        }
        if (invalidEntries.Count > 0)
        {
            return Json("error", $"Update entries missing id or both remainingHours and notesAppend: {string.Join(", ", invalidEntries)}");
        }

        return await ApplyProposedAsync(doc, proposed, source, timeoutSeconds, requireApproval, ct).ConfigureAwait(false);
    }

    // propose_changes / update_tasks の共通末尾処理。
    // proposed WbsDocument に対して DiffCalculator を回し、auto-apply / 承認 UI / apply の分岐をする。
    private static async Task<string> ApplyProposedAsync(
        DocumentViewModel doc,
        WbsDocument proposed,
        string source,
        int timeoutSeconds,
        bool requireApproval,
        System.Threading.CancellationToken ct)
    {
        var current = doc.Document;
        var changes = DiffCalculator.Compare(current, proposed);
        if (changes.Count == 0)
        {
            return "{\"result\":\"no_changes\"}";
        }

        var set = new PendingChangeSet
        {
            Changes = changes,
            Source = source,
            Proposed = proposed,
        };

        // v03-mcp-auto-apply: ユーザー設定 + AI 側 requireApproval + 分類器の全条件が揃ったら
        // 承認 UI をスキップして即適用。AI 側は緩められないので requireApproval は tighten 専用。
        var app = System.Windows.Application.Current as App;
        var autoApplyEnabled = app?.SettingsStore.Load().AutoApply.Enabled ?? false;
        if (autoApplyEnabled && !requireApproval && set.IsAllAutoApplicable)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                await doc.ApplyProposedDocumentAsync(proposed).ConfigureAwait(false);
            }
            else
            {
                var inner = await dispatcher.InvokeAsync(
                    () => doc.ApplyProposedDocumentAsync(proposed)).Task.ConfigureAwait(false);
                await inner.ConfigureAwait(false);
            }
            return $"{{\"result\":\"auto_applied\",\"changesCount\":{changes.Count}}}";
        }

        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : (TimeSpan?)null;
        var result = await doc.PendingService.SubmitAsync(set, timeout, ct).ConfigureAwait(false);

        if (result == ApprovalResult.Approved)
        {
            // 承認された proposed を Kudaki の Document に反映 + auto save。
            // UI thread で LoadDocument + File I/O が走るので Dispatcher で切り替える。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                await doc.ApplyProposedDocumentAsync(proposed).ConfigureAwait(false);
            }
            else
            {
                // DispatcherOperation<Task> の完了 (=UI thread の Task 生成) を待ってから、
                // 内部 Task (LoadDocument + Save) の完了を待つ。
                var inner = await dispatcher.InvokeAsync(
                    () => doc.ApplyProposedDocumentAsync(proposed)).Task.ConfigureAwait(false);
                await inner.ConfigureAwait(false);
            }
        }

        return result switch
        {
            ApprovalResult.Approved => $"{{\"result\":\"approved\",\"changesCount\":{changes.Count}}}",
            ApprovalResult.Rejected => "{\"result\":\"rejected\"}",
            ApprovalResult.TimedOut => "{\"result\":\"timeout\"}",
            _ => "{\"result\":\"unknown\"}",
        };
    }

    private static void FlattenTasksInto(TaskNode node, Dictionary<string, TaskNode> map)
    {
        map[node.Id] = node;
        foreach (var child in node.Children)
        {
            FlattenTasksInto(child, map);
        }
    }

    // v0.7 t-find-task: 1 タスクだけを YAML fragment で返す (全文取らずに済ませる)。
    [McpServerTool(Name = "find_task")]
    [Description(
        "Return a single task from the document as a YAML fragment. Use this when you already know the " +
        "taskId and only need that one task's fields — avoids pulling the whole document. " +
        "Set `includeChildren` to false to get just the task's own fields (children are elided). " +
        "Returns the YAML text on success, or JSON error {\"result\":\"unknown_document\"|\"unknown_task\", ...}.")]
    public static string FindTask(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Task id (matches the `id` field in the YAML). Required.")]
        string taskId,
        [Description("If true (default), include the task's children subtree. Set false to get only this task's own fields.")]
        bool includeChildren = true)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var snapshot = dispatcher is null || dispatcher.CheckAccess()
            ? doc.Document
            : dispatcher.Invoke(() => doc.Document);

        var map = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        foreach (var t in snapshot.Tasks) FlattenTasksInto(t, map);

        if (!map.TryGetValue(taskId, out var node))
        {
            return Json("unknown_task", $"taskId not found: {taskId}");
        }

        return _yaml.SerializeTaskToString(node, includeChildren);
    }

    // v0.7 t-list-tasks: subtree / status で絞り込んだ列挙。get_next_tasks は「次にやる順」だが
    // これは「何がある?」の探索用。
    [McpServerTool(Name = "list_tasks")]
    [Description(
        "List tasks in the document with optional subtree and status filters. Use this for exploration " +
        "(\"what's in this document?\", \"what's under this ancestor?\", \"what's still open?\") — for " +
        "\"what should I do next?\" call get_next_tasks instead. " +
        "`ancestorId` restricts to that task's subtree (the ancestor itself is not included in the result). " +
        "`status` filters by state: `open` = rolled-up remainingHours > 0, `done` = remainingHours reached 0, " +
        "`all` (default) = every task. `leafOnly` restricts to leaf tasks (no children). " +
        "Returns a JSON array of {taskId, title, ancestorTitles, estimateHours, remainingHours, " +
        "rolledUpRemainingHours, isLeaf}.")]
    public static string ListTasks(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Optional. If set, only tasks under this ancestor's subtree are returned (the ancestor itself is excluded).")]
        string? ancestorId = null,
        [Description("Optional filter: `open` | `done` | `all` (default). `open` = rolledUpRemainingHours > 0.")]
        string? status = null,
        [Description("If true, only leaf tasks (no children) are returned. Default false = internal nodes included.")]
        bool leafOnly = false,
        [Description("Maximum number of tasks to return. Default 100.")]
        int limit = 100)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var snapshot = dispatcher is null || dispatcher.CheckAccess()
            ? doc.Document
            : dispatcher.Invoke(() => doc.Document);

        // ancestor 指定なら subtree の起点をそこの Children に固定する。
        IReadOnlyList<TaskNode> roots;
        if (!string.IsNullOrEmpty(ancestorId))
        {
            var map = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
            foreach (var t in snapshot.Tasks) FlattenTasksInto(t, map);
            if (!map.TryGetValue(ancestorId, out var anc))
            {
                return Json("unknown_task", $"ancestorId not found: {ancestorId}");
            }
            roots = anc.Children;
        }
        else
        {
            roots = snapshot.Tasks;
        }

        var filterOpen = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
        var filterDone = string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);
        // 未指定 or "all" は素通し。それ以外の未知の値も素通し (誤指定で 0 件になるより素通しが親切)。

        if (limit < 1) limit = 1;

        var sb = new StringBuilder();
        sb.Append('[');
        var count = 0;
        var ancestorPath = new List<string>();
        WalkList(roots, ancestorPath, sb, ref count, limit, filterOpen, filterDone, leafOnly);
        sb.Append(']');
        return sb.ToString();
    }

    private static void WalkList(
        IReadOnlyList<TaskNode> nodes,
        List<string> ancestorTitles,
        StringBuilder sb,
        ref int count,
        int limit,
        bool filterOpen,
        bool filterDone,
        bool leafOnly)
    {
        foreach (var node in nodes)
        {
            if (count >= limit) return;

            var isLeaf = node.Children.Count == 0;
            var rolled = node.GetRolledUpRemainingHours();

            var include = true;
            if (leafOnly && !isLeaf) include = false;
            if (filterOpen && rolled <= 0.0) include = false;
            if (filterDone && rolled > 0.0) include = false;

            if (include)
            {
                if (count > 0) sb.Append(',');
                count++;
                sb.Append('{');
                sb.Append("\"taskId\":").Append(JsonString(node.Id)).Append(',');
                sb.Append("\"title\":").Append(JsonString(node.Title)).Append(',');
                sb.Append("\"ancestorTitles\":[");
                for (var i = 0; i < ancestorTitles.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(ancestorTitles[i]));
                }
                sb.Append("],");
                sb.Append("\"estimateHours\":").Append(NullableNumber(node.EstimateHours)).Append(',');
                sb.Append("\"remainingHours\":").Append(NullableNumber(node.RemainingHours)).Append(',');
                sb.Append("\"rolledUpRemainingHours\":")
                    .Append(rolled.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"isLeaf\":").Append(isLeaf ? "true" : "false");
                sb.Append('}');
            }

            if (node.Children.Count > 0)
            {
                ancestorTitles.Add(node.Title);
                WalkList(node.Children, ancestorTitles, sb, ref count, limit, filterOpen, filterDone, leafOnly);
                ancestorTitles.RemoveAt(ancestorTitles.Count - 1);
            }
        }
    }

    private static string YamlParseErrorJson(YamlDotNet.Core.YamlException yex)
    {
        // YamlException.Start は Mark 構造体で Line/Column を持つ (1-based)。
        var line = yex.Start.Line;
        var column = yex.Start.Column;
        var sb = new StringBuilder();
        sb.Append("{\"result\":\"error\",\"kind\":\"yaml_parse\",\"line\":")
          .Append(line)
          .Append(",\"column\":")
          .Append(column)
          .Append(",\"message\":")
          .Append(JsonString(yex.Message))
          .Append('}');
        return sb.ToString();
    }

    [McpServerTool(Name = "get_next_tasks")]
    [Description(
        "Return the document's unfinished leaf tasks in the order they should be worked on. " +
        "Use this to decide what to do next instead of picking tasks yourself — the user controls this " +
        "order from Kudaki by reordering the tree and editing dependencies, so it reflects what they " +
        "actually want done first, and it changes as they rearrange things. Re-read it before starting " +
        "each task rather than caching a plan. " +
        "The order comes from the predecessor dependencies (a task never appears before a predecessor that " +
        "is still unfinished) with the tree order breaking ties. Tasks whose remaining hours have reached " +
        "zero are treated as done and left out. Returns a JSON array of " +
        "{taskId, title, ancestorTitles, estimateHours, remainingHours, blockedBy}, where `blockedBy` lists " +
        "the unfinished predecessors that still gate the task.")]
    public static string GetNextTasks(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Maximum number of tasks to return (default 20).")]
        int limit = 20)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return $"{{\"result\":\"unknown_document\",\"message\":{JsonString($"No open document matches '{documentId}'. Call list_documents first.")}}}";
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var ordered = dispatcher is null || dispatcher.CheckAccess()
            ? doc.GetExecutionOrder()
            : dispatcher.Invoke(() => doc.GetExecutionOrder());

        if (limit < 1) limit = 1;
        var sb = new StringBuilder();
        sb.Append('[');
        var count = 0;
        foreach (var task in ordered)
        {
            if (count >= limit) break;
            if (count > 0) sb.Append(',');
            count++;

            sb.Append('{');
            sb.Append("\"taskId\":").Append(JsonString(task.Id)).Append(',');
            sb.Append("\"title\":").Append(JsonString(task.Title)).Append(',');
            sb.Append("\"ancestorTitles\":[");
            var ancestors = new System.Collections.Generic.List<string>();
            for (var p = task.Parent; p is not null && p.Parent is not null; p = p.Parent) ancestors.Add(p.Title);
            ancestors.Reverse();
            for (var i = 0; i < ancestors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(ancestors[i]));
            }
            sb.Append("],");
            sb.Append("\"estimateHours\":").Append(NullableNumber(task.EstimateHours)).Append(',');
            sb.Append("\"remainingHours\":").Append(NullableNumber(task.RemainingHours)).Append(',');
            sb.Append("\"blockedBy\":[");
            var first = true;
            foreach (var pred in task.Predecessors)
            {
                if (pred.RolledUpRemainingHours <= 0.0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonString(pred.Title));
            }
            sb.Append(']');
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    [McpServerTool(Name = "wait_for_request")]
    [Description(
        "Wait for the user to send you a request from Kudaki's UI, then return it. This is how Kudaki asks " +
        "YOU to do something: the user right-clicks a task and picks an action, and this call returns it. " +
        "Kudaki's MCP transport is stateless, so it cannot push to you — you have to be waiting here. " +
        "The call BLOCKS until a request arrives or `timeoutSeconds` elapses, so only call it when the user " +
        "has asked you to stand by for Kudaki requests; your session cannot do anything else while it waits. " +
        "Returns {result:'request', id, kind, documentId, taskId, taskTitle, ancestorTitles, estimateHours, " +
        "remainingHours, notes} or {result:'timeout'}. Requests issued while nobody is waiting are queued, " +
        "so a request is never lost — check `pendingRequests` in list_documents and call this to drain them. " +
        "kind 'breakdown' means: split that task into concrete child tasks and send them with propose_changes. " +
        "When splitting, distribute the parent's estimateHours and remainingHours across the new children — " +
        "Kudaki ignores a parent's own hours once it has children, so skipping this resets the task's progress. " +
        "To keep standing by, call this again after handling each request.")]
    public static async Task<string> WaitForRequest(
        [Description("Absolute file path of the document to watch (from list_documents). Required.")]
        string documentId,
        [Description("How long to block before giving up, in seconds (default 300 = 5 minutes). " +
                     "On timeout call again to keep waiting.")]
        int timeoutSeconds = 300,
        System.Threading.CancellationToken ct = default)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return $"{{\"result\":\"unknown_document\",\"message\":{JsonString($"No open document matches '{documentId}'. Call list_documents first.")}}}";
        }

        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromMinutes(5);
        var request = await doc.AgentRequests.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (request is null) return "{\"result\":\"timeout\"}";

        var sb = new StringBuilder();
        sb.Append("{\"result\":\"request\",");
        sb.Append("\"id\":").Append(JsonString(request.Id.ToString())).Append(',');
        sb.Append("\"kind\":").Append(JsonString(request.Kind.ToString().ToLowerInvariant())).Append(',');
        sb.Append("\"documentId\":").Append(JsonString(request.DocumentId)).Append(',');
        sb.Append("\"taskId\":").Append(JsonString(request.TaskId)).Append(',');
        sb.Append("\"taskTitle\":").Append(JsonString(request.TaskTitle)).Append(',');
        sb.Append("\"ancestorTitles\":[");
        for (var i = 0; i < request.AncestorTitles.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonString(request.AncestorTitles[i]));
        }
        sb.Append("],");
        sb.Append("\"estimateHours\":").Append(NullableNumber(request.EstimateHours)).Append(',');
        sb.Append("\"remainingHours\":").Append(NullableNumber(request.RemainingHours)).Append(',');
        sb.Append("\"notes\":").Append(request.Notes is null ? "null" : JsonString(request.Notes));
        sb.Append('}');
        return sb.ToString();
    }

    private static string NullableNumber(double? value) =>
        value is null ? "null" : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Json(string result, string message)
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"result\":\"{result}\",\"message\":\"{escaped}\"}}";
    }

    // 単純な JSON 文字列エスケープ (list_documents 内で使用)。
    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

// v0.7 update_tasks の 1 エントリ。MCP SDK は System.Text.Json でパラメータを deserialize するので
// JsonPropertyName で AI が書きやすい camelCase key に固定する。
public sealed class TaskUpdate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("remainingHours")]
    public double? RemainingHours { get; set; }

    [JsonPropertyName("notesAppend")]
    public string? NotesAppend { get; set; }
}
