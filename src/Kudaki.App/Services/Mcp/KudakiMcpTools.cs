using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using Kudaki.App.Models;
using Kudaki.App.Services;
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
        catch (Exception ex)
        {
            return Json("error", $"YAML parse failed: {ex.Message}");
        }

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
