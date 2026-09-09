# YAML style for agents proposing to Kudaki

Kudaki accepts any well-formed YAML that round-trips through YamlDotNet, but a small style discipline avoids the two footguns that come up most often: block scalars that silently fold whitespace, and Markdown that renders poorly in the notes pane.

## `notes` field

Kudaki renders `notes` as Markdown in the detail pane. Pick the block form by the shape of the text.

**Short, single-line note** — use double-quoted flow scalar.

```yaml
- id: t-example
  title: Example
  notes: "Waiting on confirmation from the reviewer."
```

**Multi-line note with paragraphs or code** — use `|` (literal block scalar). Every line break in the source is preserved.

```yaml
- id: t-example
  title: Example
  notes: |
    Completed on 2026-09-09 in commit abc1234.

    Follow-up: the retry policy needs a shorter backoff for the mobile client.
```

**Do not use `>` (folded block scalar).** Folded scalars collapse consecutive non-empty lines into a single line with a space separator, and preserve blank lines only in specific configurations. This routinely surprises agents that expect their line breaks to survive round-trip. `|` is unambiguous — use it.

## Headings inside notes

Kudaki's detail pane renders Markdown, so use Markdown bold for headings inside a note:

```yaml
notes: |
  **Decision**

  Went with option B (in-memory cache) after benchmarking option A at 40ms per lookup.

  **Follow-up**

  - Add a metric for cache hit ratio.
  - Revisit if memory usage crosses 200MB.
```

Do not use `#`, `##`, etc. inside a note — they render as headings in the standalone Markdown export, but they read as noise in the inline detail pane.

## `remainingHours` and other numeric fields

Emit numbers as plain YAML scalars, not quoted strings. `remainingHours: 0` is correct; `remainingHours: "0"` is a string and will fail deserialization.

## `predecessorIds`

A list of sibling task ids. Empty list, missing key, and `[]` all mean "no predecessors" and are equivalent. Use whichever your serializer produces naturally.

```yaml
- id: t-child-c
  title: Wire up UI
  predecessorIds:
  - t-child-a
  - t-child-b
```

Only siblings under the same parent are legal — Kudaki rejects a predecessor that points outside the sibling set. Cycles are rejected too.

## Round-trip discipline

If you fetched the document with `get_document`, mutated it in memory, and are about to send it back with `propose_changes`, keep the original scalar style unless you have a reason to change it. Switching `|` to `"` for the same content produces a diff Kudaki will show to the user as a Notes change — noise that costs a review round.

`update_tasks` avoids this problem entirely for the `remainingHours` + notes-append case; prefer it when applicable.
