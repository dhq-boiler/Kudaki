using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Kudaki.App.Models;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;
using ModelContextProtocol.Server;

namespace Kudaki.App.Services.Mcp;

// AI エージェント (Claude Code / Claude Desktop 等) に公開する MCP tools。
// project-mcp-roadmap の v0.2 スコープ:
//   get_document           — read-only スナップショット
//   propose_changes        — 変更提案 + ユーザー承認 await
//   get_pending_summary    — ペンディング概要 (次)
[McpServerToolType]
public static class KudakiMcpTools
{
    // 使い回しのシリアライザ (静的で 1 個持てば十分)。
    private static readonly YamlStorageService _yaml = new();

    [McpServerTool(Name = "get_document")]
    [Description("Return the currently open WBS document as YAML text. Read-only snapshot.")]
    public static string GetDocument()
    {
        var vm = MainViewModel.Current;
        if (vm is null)
        {
            return "# Kudaki: no document loaded\n";
        }
        return vm.GetDocumentYamlSnapshot();
    }

    [McpServerTool(Name = "propose_changes")]
    [Description(
        "Propose a full replacement of the WBS document. Submit the entire new document as YAML text " +
        "(same format as get_document returns). Kudaki diffs it against the current document, shows the " +
        "diff in the review UI to the user, and waits for approval or rejection (default timeout: 5 minutes). " +
        "Returns a JSON string with a `result` field: `approved` / `rejected` / `timeout` / `no_changes` / `error`.")]
    public static async Task<string> ProposeChanges(
        [Description("Full replacement WBS document as YAML text (same format get_document returns)")]
        string yaml,
        [Description("Optional caller identification, shown to the user in the review UI (e.g. 'Claude Code')")]
        string source = "AI agent",
        [Description("Optional approval timeout in seconds (default 300 = 5 minutes)")]
        int timeoutSeconds = 300)
    {
        var vm = MainViewModel.Current;
        if (vm is null)
        {
            return "{\"result\":\"error\",\"message\":\"No document loaded in Kudaki\"}";
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

        var current = vm.Document;
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
        var result = await PendingChangesService.Instance.SubmitAsync(set, timeout).ConfigureAwait(false);

        if (result == ApprovalResult.Approved)
        {
            // 承認された時点で proposed を Kudaki の実 Document に反映。
            // UI スレッド上で LoadDocument する。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                vm.ApplyProposedDocument(proposed);
            }
            else
            {
                await dispatcher.InvokeAsync(() => vm.ApplyProposedDocument(proposed)).Task.ConfigureAwait(false);
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
        // 単純な手書き。message はダブルクォート / バックスラッシュだけエスケープすれば OK。
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"result\":\"{result}\",\"message\":\"{escaped}\"}}";
    }
}
