# Changelog

All notable changes to Kudaki are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-09-02

### Added

- **Multi-document tabs.** Kudaki now runs as a single-instance process (Mutex + Named Pipe) and opens multiple `.wbs.yaml` files in tabs. Double-clicking a file or launching the executable a second time forwards the path to the running instance and opens a new tab there. Each tab shows a dirty marker (`*`) and a close button, and closing a dirty tab opens a Save / Discard / Cancel confirm dialog. The open tabs and the active selection are persisted to `settings.json` and restored on the next launch.
- **Per-document MCP schema.** The `get_document` and `propose_changes` tools now require an explicit `documentId` (absolute file path). A new `list_documents` tool returns every open document as `{documentId, filePath, title, isActive, isDirty, revision}`. Multiple Claude sessions can now target different tabs cleanly instead of racing on a single active document. `propose_changes` accepts an optional `expectedRevision` (obtained from `list_documents`) for optimistic concurrency; a stale snapshot is rejected with `revision_mismatch` before the review UI opens.
- **Auto-apply for lightweight AI changes.** A new **MCP** category in Preferences lets you opt in to skipping the review UI for proposals that only update `RemainingHours` or append to `Notes`. Task Add / Delete, hierarchy changes, `EstimateHours`, `Title`, `Assignee`, `DueDate`, and `Notes` rewrites always go through review. Mixed proposals (at least one heavy change) fall back to review as a whole. AI callers can force review on any single proposal with `requireApproval=true`; they cannot loosen the policy from their side.
- **Auto-save on approval.** Approving an MCP proposal writes the changed document to disk immediately, so restarting Kudaki no longer loses accepted AI edits.
- **Custom `ConfirmDialog` window.** The unsaved-changes prompt is now a Kudaki-palette dialog with `WindowChrome`, replacing the OS-native `MessageBox` that did not follow the dark theme.
- **Kata-style scrollbars.** Slim 10 px dark scrollbars with a rounded thumb (blue on drag) replace the previous minimal `ScrollBar` template, matching the Kata code-smell settings look across TreeView, detail pane, and dialogs.
- **UI language switcher.** Preferences → General exposes Japanese / English / Follow OS. Windows opened after the change use the new culture (WPF binds strings once at construction).

### Changed

- The `DiffOverlay` visibility now follows the active tab's `CurrentPendingSet` through XAML binding, replacing a code-behind subscription that had been pinned to a single document instance.
- `IsDirty` now flips true on any manual edit (task title, estimate, notes, structure), not only on Save / Load / AI proposal, so the close-confirm dialog and tab dirty marker behave correctly.

### Fixed

- **Startup crash from a duplicate `ScrollBar` style** introduced during the scrollbar refactor. The old minimal style has been removed; the new Kata-derived one is the single source of truth.
- **Lost tabs after saving preferences.** `System.Text.Json`'s `PropertyNamingPolicy = CamelCase` applies only to writes; reads defaulted to case-sensitive matching, so `openDocuments` in the settings file failed to bind back to the C# `OpenDocuments` property and was overwritten with `[]` on the next save. `PropertyNameCaseInsensitive = true` fixes both directions.
- **Splash hang when a second instance failed to start** because `Kestrel.StartAsync` swallowed the port-in-use exception. Kudaki now pre-checks the port with a `TcpListener`, fails fast, and reports the error through the Landing overlay.
- Landing overlay could stay visible if MCP started before `MainViewModel.Current` was assigned; `ReportLoadingSafelyAsync` now marshals to the UI thread with a short retry.
- Tab persistence is written on every `Documents` change and `ActiveDocument` switch, so Force-kill or unexpected termination no longer strands `openDocuments` at `[]`.

### Breaking

- **MCP `get_document` / `propose_changes` now require `documentId`.** v0.2 callers that omitted it must be updated. Use `list_documents` to obtain the id (an absolute file path).

## [0.2.0]

Initial MCP server, Diff Overlay, logo / Landing, AI-agent external task store. See the [`v0.2.0` release](https://github.com/dhq-boiler/Kudaki/releases/tag/v0.2.0) for the packaged installer.
