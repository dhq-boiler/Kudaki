<p align="center">
  <img src="docs/logo/lockup-horizontal.png" alt="Kudaki" width="620"/>
</p>

# Kudaki

A keyboard-driven Work Breakdown Structure editor for Windows.

![screenshot](docs/screenshot.png)

## Overview

Kudaki (from the Japanese verb "to break down") is a small WPF application for building and maintaining hierarchical task plans. It is designed to be faster and less friction-prone than editing a WBS in a spreadsheet: every operation has a keyboard shortcut, progress is derived from the remaining-hours you update daily, and the tool warns you when a leaf task looks too coarse to plan reliably.

Kudaki is Windows-only, single-user, free, and open source.

## Installation

Download `KudakiSetup.exe` from the [latest release](https://github.com/dhq-boiler/Kudaki/releases/latest) and run it.

- Windows 10 or 11, x64
- The .NET 10 runtime is bundled in the installer; no separate download is required
- Installs into the current user profile (`%LOCALAPPDATA%\Programs\Kudaki`); no administrator rights required
- Uninstall from Windows "Apps & features"

## Usage

Kudaki opens in an empty document. Press `Enter` and start typing to add the first task. Every subsequent operation is one keystroke.

### Opening a file

- Launch from the Start Menu
- Or run `Kudaki.exe path\to\plan.wbs.yaml` from a shell to open a file directly
- Or drag a `.wbs.yaml` (or `.yaml` / `.yml`) file onto the window

### Keyboard shortcuts

Tree editing:

| Key                | Action                                                             |
| ------------------ | ------------------------------------------------------------------ |
| `Enter`            | Add a sibling after the selected task (or a top-level task if none is selected) |
| `Alt`+`Enter`      | Add a child under the selected task                                |
| `Tab`              | Indent the selected task (make it a child of the previous sibling) |
| `Shift`+`Tab`      | Outdent the selected task (promote it to a sibling of its parent)  |
| `Ctrl`+`Up`        | Move the selected task up among its siblings                       |
| `Ctrl`+`Down`      | Move the selected task down among its siblings                     |
| `Delete`           | Delete the selected task (including its children)                  |

File:

| Key                    | Action              |
| ---------------------- | ------------------- |
| `Ctrl`+`N`             | New document        |
| `Ctrl`+`O`             | Open                |
| `Ctrl`+`S`             | Save                |
| `Ctrl`+`Shift`+`S`     | Save as             |
| `Ctrl`+`E`             | Export to Markdown  |

### Remaining-hours model

Kudaki does not expose "actual hours" or "percent complete" as editable fields. Instead, you edit a single "remaining hours" value per leaf task each day, and the following are derived:

- **Spent** = `max(0, estimate - remaining)`
- **Progress** = `(estimate - remaining) / estimate`, clamped to 0..100

The rules for a leaf task are:

- Remaining is unset: the task is not started (0% complete)
- Remaining equals the estimate: the task is not started
- Remaining is between 0 and the estimate: in progress
- Remaining is 0: complete
- Remaining exceeds the estimate: the task grew past its original estimate; progress stays at 0 until the estimate is revised

Internal (parent) nodes aggregate the estimates and remainings of their leaves. Their spent and progress are derived from the aggregates.

### Breakdown warning

If a leaf task has an estimate greater than 40 hours, Kudaki marks it with a warning glyph and a note saying it is still too large to plan reliably. This threshold is fixed in v0.1 and will be configurable later.

### Task dependencies

Any task can declare predecessor tasks — tasks that must complete before this one starts. Semantics are Finish-to-Start only: `X -> Y` means every leaf under `X` must complete before any leaf under `Y` can start. Predecessors may sit at any level of the tree, including internal (parent) nodes.

To add a predecessor, select a task and pick one from the "Predecessor tasks" dropdown in the detail pane. Current predecessors are shown as pill chips with an `x` to remove them. Cycles and ancestor/descendant edges are rejected. Indenting or outdenting a task auto-removes any predecessor that would become illegal after the move.

### Arrow diagram

Right-click a parent task in the tree and pick "Show arrow diagram" to open a per-parent Activity-on-Node view. Nodes are laid out with a Kahn topological sort; only edges internal to the current parent scope are drawn. A lightning bolt marker on a child indicates that child has an inbound predecessor from outside the current parent.

A small demo file exercising cross-phase dependencies is in [`docs/deps-demo.wbs.yaml`](docs/deps-demo.wbs.yaml).

## MCP server (AI agent integration)

Kudaki exposes a [Model Context Protocol](https://modelcontextprotocol.io/) server on `http://localhost:27650/mcp` while running. AI agents (Claude Code, Claude Desktop, and any other MCP-aware client) can read the current document and propose changes; the user reviews the proposed diff in an in-app overlay and accepts or rejects the whole set with one click.

Transport is Streamable HTTP in stateless mode (MCP 2025 spec). No session tokens or authentication are used; the server binds to `localhost` only.

### Tools exposed

- `get_document` — read-only. Returns the currently open WBS as YAML text.
- `propose_changes` — submit a full replacement WBS as YAML text. Kudaki diffs it against the current document, shows the diff to the user, and waits (default 5 minutes, configurable per call) for approval or rejection. Returns one of: `approved` / `rejected` / `timeout` / `no_changes` / `error`.

### Connecting from Claude Code

Add to `~/.claude/mcp.json` (or a per-project `.claude/mcp.json`):

```json
{
  "mcpServers": {
    "kudaki": {
      "type": "http",
      "url": "http://localhost:27650/mcp"
    }
  }
}
```

Kudaki must be running for the MCP endpoint to be reachable.

### Approval UI

When a `propose_changes` call arrives, Kudaki shows a modal diff overlay listing each proposed change:

- Additions are outlined in green with a `+` marker.
- Deletions are outlined in red with a `-` marker.
- Updates are outlined in orange with a `~` marker, followed by per-field Before → After lines.
- Document-level changes (e.g. the document title) appear once under a synthetic "(ドキュメント全体)" entry.

Two buttons at the bottom — "承認 (全部反映)" and "却下 (全部)" — commit or discard the entire proposed set. If the user does not respond within the tool's `timeoutSeconds`, the call returns `timeout` and the overlay closes without changing the document. Fine-grained (per-change) approval is planned for a later release.

## Using Kudaki as an AI agent's task tracker

Most AI coding agents (Claude Code, Copilot Chat, and so on) keep their task list inside the session. It evaporates when the session ends and it does not cross project boundaries. That is fine for tiny one-off tasks, but it makes long-running work invisible to the human and unshareable between sessions.

Kudaki is built to be the external, persistent task store that fixes this. Point the agent at a `.wbs.yaml` file — for example `docs/tasks.wbs.yaml` in a git-managed repository, or `~/.claude/projects/<slug>/tasks.wbs.yaml` for per-user tracking outside the repo — and tell it to manage its own work there. Two things you get right away:

- **Token savings**: the agent stops carrying its task list around inside the conversation context. Task state lives in the `.wbs.yaml` file and is fetched via `get_document` only when the agent actually needs it, so every subsequent prompt stays leaner and cheaper.
- **Visual overview in a real GUI window**: you see the agent's work in Kudaki's WBS view — hierarchy, aggregated hours, breakdown warnings, dependency arrows — instead of scrolling through terminal task listings. You can watch the agent's plan take shape and spot problems (a leaf that's still 60 hours, an unplanned dependency) at a glance.

Everything else follows from those:

- Tasks persist across sessions and diff cleanly in git.
- Every write the agent makes goes through the MCP `propose_changes` flow, so you approve or reject the diff before it lands in the file.
- A fresh session picks the state back up by calling `get_document` — no re-briefing required.

In `~/.claude/CLAUDE.md` (global) or a project's `CLAUDE.md`, an instruction of this shape is enough to route the agent's task management through Kudaki from that session on:

> Task management is done in a Kudaki `.wbs.yaml` file, not with the built-in task tools. Default location: `docs/tasks.wbs.yaml` in git-managed repositories, otherwise `~/.claude/projects/<slug>/tasks.wbs.yaml`. Existing in-flight session tasks can finish through the built-in list; new tasks go to the `.wbs.yaml` from that point on.

## File format

Documents are saved as YAML with the extension `.wbs.yaml`. The format is versioned (`version: 3` in the current release) and is designed to be human-readable, diff-friendly, and easy for tools (including AI agents) to produce or consume. A worked example is in [`docs/v02-plan.wbs.yaml`](docs/v02-plan.wbs.yaml).

Version 3 adds `predecessorIds` on each task. Files saved by v0.1.2 or older (`version: 2`) load unchanged; files saved by v0.1.3 or newer will not load in older releases.

A Markdown export exists for sharing on GitHub or in documentation. It uses task-list syntax (`- [ ]` / `- [x]`) and preserves rolled-up totals and the breakdown warning. See [`docs/v02-plan.md`](docs/v02-plan.md) for a rendered example.

## Building from source

Requires the .NET 10 SDK.

```
git clone https://github.com/dhq-boiler/Kudaki.git
cd Kudaki
dotnet build
dotnet run --project src/Kudaki.App
```

To produce a self-contained release build:

```
dotnet publish src/Kudaki.App -c Release -r win-x64 --self-contained true -o publish/Kudaki.App
```

To rebuild the installer, first zip the published app into `src/Kudaki.Installer/Payload/Kudaki-payload.zip`, then publish the installer as a single-file self-contained executable:

```
dotnet publish src/Kudaki.Installer -c Release -r win-x64 --self-contained true -o publish/Kudaki.Installer
```

## Technology

- C#, WPF, .NET 10
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) for command generation, [R3](https://github.com/Cysharp/R3) for observable properties
- YAML I/O: [YamlDotNet](https://github.com/aaubry/YamlDotNet)

Code-behind is kept intentionally small. Dialogs go through an `IFileDialogService`, drag-and-drop is an attached behavior, key bindings live in XAML and bind directly to view-model commands.

## Roadmap

Shipped in v0.2: the MCP server described above with `get_document` and `propose_changes`, the diff review overlay, and document-level diff detection.

Planned for v0.3 and later: per-change (not just all-or-nothing) approval in the diff overlay, subtree add/delete folding in the diff, and configurability of the MCP listen port from the UI. See [`docs/v02-plan.wbs.yaml`](docs/v02-plan.wbs.yaml) for the running punch list.

## License

MIT. See [LICENSE](./LICENSE).

## Author

[dhq_boiler](https://github.com/dhq-boiler)
