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
        "{documentId, filePath, title, isActive, isDirty}. `documentId` is the absolute file path " +
        "and is the required key for get_document and propose_changes. Unsaved (untitled) documents " +
        "are NOT listed — they cannot be addressed by MCP tools until saved. " +
        "Call this FIRST before get_document / propose_changes to pick the correct target.")]
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
            sb.Append("\"isDirty\":").Append(d.IsDirty ? "true" : "false");
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
        "Kudaki diffs it against that document, shows the diff in that document's tab review UI, and waits " +
        "for approval or rejection (default timeout: 5 minutes). Returns a JSON string with a `result` field: " +
        "`approved` / `rejected` / `timeout` / `no_changes` / `unknown_document` / `error`.")]
    public static async Task<string> ProposeChanges(
        [Description("Absolute file path of the target document (from list_documents). Required.")]
        string documentId,
        [Description("Full replacement WBS document as YAML text (same format get_document returns). Required.")]
        string yaml,
        [Description("Optional caller identification, shown to the user in the review UI (e.g. 'Claude Code')")]
        string source = "AI agent",
        [Description("Optional approval timeout in seconds (default 300 = 5 minutes)")]
        int timeoutSeconds = 300)
    {
        var doc = DocumentRegistry.Instance.Resolve(documentId);
        if (doc is null)
        {
            return Json("unknown_document", $"documentId not found: {documentId}. Call list_documents first.");
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

        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : (TimeSpan?)null;
        var result = await doc.PendingService.SubmitAsync(set, timeout).ConfigureAwait(false);

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
