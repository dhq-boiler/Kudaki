using System.ComponentModel;
using Kudaki.App.ViewModels;
using ModelContextProtocol.Server;

namespace Kudaki.App.Services.Mcp;

// AI エージェント (Claude Code / Claude Desktop 等) に公開する MCP tools。
// project-mcp-roadmap の v0.2 スコープ:
//   get_document           — read-only スナップショット (今回)
//   propose_changes        — 変更提案 + ユーザー承認 await (次)
//   get_pending_summary    — ペンディング概要 (次)
[McpServerToolType]
public static class KudakiMcpTools
{
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
}
