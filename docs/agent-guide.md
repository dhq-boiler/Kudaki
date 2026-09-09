# Kudaki for AI agents

This is a short operating guide for agents driving Kudaki through its MCP server. It complements the tool listing in the main README with the shape of the workflows that come up in practice.

## The three-step loop

Every write goes through the same three steps. Skipping any of them means you either send stale data or clobber concurrent edits.

1. `list_documents` — pick the target and capture its `revision`.
2. `get_document` (or `find_task` / `list_tasks` for a slice) — read what you are about to change.
3. `propose_changes` (or `update_tasks`) — send the new state, passing the `revision` you captured as `expectedRevision`.

If someone else edited the document between steps 2 and 3, step 3 returns `revision_mismatch` and touches nothing. Re-read and rebuild the proposal.

## Which write tool to use

- `update_tasks` — bumping `remainingHours` or appending to `notes` on existing tasks. Does not require sending the whole document. This is the auto-apply path for the two most common edits.
- `propose_changes` — everything else: adding or deleting tasks, changing titles / estimates / dependencies, restructuring the tree, rewriting notes wholesale.

If a change touches even one field outside the `update_tasks` scope, use `propose_changes` for the whole batch — Kudaki will fall back to the approval UI anyway, and mixing two calls just splits the review into two dialogs.

## Read tools compared

- `get_document` — the whole thing. Use when you need to reason across many tasks, or when you are about to propose changes and want a fresh baseline.
- `find_task` — one task by id. Use when you already know the id and only need its own fields; pair with `includeChildren=false` to skip the subtree.
- `list_tasks` — enumerate with `ancestorId` / `status` / `leafOnly` filters. Use for exploration ("what is under this parent?", "what is still open?"). Not sorted for execution — for that use `get_next_tasks`.
- `get_next_tasks` — the user-controlled work order. Predecessors respected, tree order breaks ties. Re-read it before each task rather than caching a plan; the user reshapes it by dragging tasks and editing dependencies.

## Standing by for user requests

Kudaki's MCP transport is stateless, so it cannot push anything to you. Instead you park in `wait_for_request` and Kudaki hands over whatever the user asks for while you are waiting.

Right-clicking a task in Kudaki offers **Ask AI to break down this task**. The lifecycle:

- User right-clicks and picks the action → Kudaki looks for a waiting agent for that document.
- If `wait_for_request` is in flight (visible as `agentWaiting: true` in `list_documents`) the request is returned immediately.
- If not, the request is queued (`pendingRequests` in `list_documents` reflects the depth). The next `wait_for_request` call drains it in order — requests are never lost.

A typical standing-by session:

```
1. list_documents                      → pick target, note pendingRequests
2. (drain any queued requests first)
   wait_for_request(documentId, ...)   → returns {kind: "breakdown", taskId, ...}
3. get_document / find_task            → read the task and its context
4. propose_changes with expectedRevision → deliver child tasks
5. Loop back to step 2 to keep waiting.
```

The block in step 2 is exclusive: while a `wait_for_request` call is in flight the session cannot do anything else. Only enter it when the user has explicitly asked you to stand by.

When splitting a task, distribute the parent's `estimateHours` and `remainingHours` across the new children. Kudaki ignores a parent's own hours once it has children, so skipping the split resets the task's progress to zero.

## State summary of the two "who has the ball" fields

| `agentWaiting` | `pendingRequests` | Meaning |
| --- | --- | --- |
| false | 0 | Idle. No agent parked, nothing queued. |
| true | 0 | Agent parked in `wait_for_request`, nothing to hand over yet. |
| false | > 0 | Requests are waiting for someone to pick them up. Call `wait_for_request` to drain. |
| true | > 0 | Transient — the parked call is about to return the next queued request. |

## Response shapes to expect

`propose_changes` and `update_tasks` return one of:

- `auto_applied` — user has auto-apply enabled and all diffs qualified as light. Applied without UI.
- `approved` / `rejected` — user answered the diff overlay.
- `timeout` — no answer within `timeoutSeconds`.
- `no_changes` — your proposal was identical to the current state.
- `revision_mismatch` — `expectedRevision` did not match; nothing was touched. Re-read and retry.
- `unknown_document` / `unknown_task` — bad id.
- `error` — with `kind: "yaml_parse"`, `line`, `column`, and `message` when the YAML failed to parse; a plain `message` otherwise. Fix and retry when `kind` is `yaml_parse`; consult the user for anything else.

## Auto-starting Kudaki with a Claude Code hook

Kudaki has to be running for its MCP server to be reachable. Rather than adding a headless mode to Kudaki (which would break the approval UI it exists for), start it from a Claude Code `SessionStart` hook — the agent gets Kudaki up as part of session setup, without having to prompt the user for it.

Kudaki is single-instance, so a second launch just brings the existing window to the foreground; running the hook every session is safe.

Add to `~/.claude/settings.json` (or a per-project `.claude/settings.json`), pointing `command` at your Kudaki install:

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell -NoProfile -WindowStyle Hidden -Command \"Start-Process 'C:\\Program Files\\Kudaki\\Kudaki.exe'\""
          }
        ]
      }
    ]
  }
}
```

The hook fires once per Claude Code session. `Start-Process` returns immediately, so it does not block the agent from starting; if Kudaki is already up, the new process exits after handing focus to the existing instance.

If the MCP endpoint is still not reachable when the agent's first `list_documents` call runs (the window is opening but not yet listening), the call fails with a connection error — retry after a couple of seconds. Once the window is up the endpoint stays available for the rest of the session.
