# Accessibility audit — 16 November 2026

Scope: Playground, Documents, Chunk Review, Workflow, Rules and Tests.

Result: **passed** for the requested gate.

- axe-core 4.14.0 found **0 critical, 0 serious, 0 moderate and 0 minor** issues on every page.
- No horizontal overflow was detected at 320, 375, 768 or 1440 px.
- Keyboard-only tests cover the primary task on all six pages.
- Evidence: [`accessibility-audit-2026-11-16-run5.json`](accessibility-audit-2026-11-16-run5.json)
  (generated 2026-11-16T10:00:20Z from the final tree after the production build and full test run).
- Base revision: `a92f196`; the audited changes were uncommitted worktree changes.

## Method

### Automated axe audit in a real layout engine

The component tests render each page with synthetic fixtures and write a styled HTML snapshot.
`scripts/a11y_audit.py` loads each snapshot in Chromium with the production CSS and runs
axe-core against the full document at four viewport widths:

| Width | Height | Target |
| ---: | ---: | --- |
| 320 | 640 | Small mobile |
| 375 | 812 | Mobile |
| 768 | 1024 | Tablet |
| 1440 | 900 | Desktop |

The audit keeps the `color-contrast` rule enabled, reports serious and critical violations, and
also measures `documentElement.scrollWidth` against the viewport to detect horizontal overflow.
The structural Vitest checks disable color contrast because jsdom has no layout engine; the
Chromium run is the contrast gate.

Reproduction (PowerShell, from `frontend/` after `npm run build`):

```powershell
$css = Get-ChildItem .next/static/css -File | Sort-Object Length -Descending | Select-Object -First 1
$env:A11Y_CSS_HREF = "file:///" + ($css.FullName -replace '\\','/')
$env:A11Y_SNAPSHOT_DIR = Join-Path $env:TEMP 'clinical-rag-a11y'
npx vitest run tests/playground.test.tsx tests/documents.test.tsx tests/chunk-review.test.tsx `
  tests/workflow-lifecycle.test.tsx tests/rules-editor.test.tsx tests/tests-page.test.tsx
Remove-Item Env:A11Y_CSS_HREF, Env:A11Y_SNAPSHOT_DIR
python scripts/a11y_audit.py --snapshots "$env:TEMP\clinical-rag-a11y" `
  --output docs/accessibility-audit-2026-11-16-run6.json
```

The script refuses to overwrite an existing report so each run is retained.

### Keyboard-only walkthrough

Each test starts from the page's natural focus order and uses only the keyboard (`Tab`, arrow
keys, `Enter`, `Space` and typing), never clicks. The tests live beside the existing page tests and
share `frontend/tests/keyboard.ts`.

| Page | Keyboard path exercised | Test |
| --- | --- | --- |
| Playground | Tab to the clinical question, type it, press Enter to send, then Tab to the result export controls. | `playground.test.tsx` — “completes the question flow from the keyboard alone, including result exports” |
| Documents | Tab to Upload guideline, press Enter to open the dialog, Tab to Cancel, press Enter to dismiss. | `documents.test.tsx` — “opens and dismisses the upload dialog from the keyboard alone” |
| Chunk Review | Tab to the tab list, use ArrowRight/Enter to select Pending Chunks, ArrowLeft/Enter to return, Tab to an unprocessed page checkbox and toggle it twice with Space. | `chunk-review.test.tsx` — “switches review tabs and selects pages from the keyboard alone” |
| Workflow | Tab to the Verify step, press Enter, and confirm focus moves to the “Verify settings” heading and `aria-current="step"` follows. | `workflow-lifecycle.test.tsx` — “opens workflow step settings with the keyboard and moves focus to the settings heading” |
| Rules | Tab to New rule, press Enter, Tab to Rule title and type a title. | `rules-editor.test.tsx` — “starts a new rule and reaches its required title from the keyboard alone” |
| Tests | Tab to a case checkbox, select it with Space, Tab to Run selected and press Enter; confirm the selected case is posted and results render. | `tests-page.test.tsx` — “selects a case and starts a run from the keyboard alone” |

Existing tests also cover focus movement to validation summaries on Rules and Workflow and native
labels on Chunk Review page inputs. The test-only dialog shim exercises open/close callbacks but
does not reproduce native Escape or top-layer behaviour.

## Issues found and fixed

### Run 1 — failed

Report: [`accessibility-audit-2026-11-16.json`](accessibility-audit-2026-11-16.json)

- **All six pages, all widths:** `document-title` (serious). The generated snapshots omitted a
  document title. The snapshot harness now writes the application title
  (“Clinical RAG Platform”), matching the real root layout.
- **Chunk Review, all widths:** `color-contrast` (serious). White text on `emerald-600` accept
  buttons measured 3.65:1. The accept and bulk-accept buttons now use `emerald-700` with
  `emerald-800` hover.
- **Workflow, all widths:** `color-contrast` (serious). Muted text on the selected step’s accent
  background measured 3.84:1. The light-mode `--muted-foreground` token changed from
  `oklch(0.55 0.03 260)` to `oklch(0.50 0.03 260)`, which is also used by other secondary text.

### Run 2 — failed

Report: [`accessibility-audit-2026-11-16-run2.json`](accessibility-audit-2026-11-16-run2.json)

The document-title findings were resolved. Chunk Review and Workflow contrast findings remained
because the contrast fixes had not yet been applied. The title fix was included in run 2; the
contrast token and accept-button changes were applied after run 2 and verified in run 3.

### Runs 3–5 — passed

Reports: [`accessibility-audit-2026-11-16-run3.json`](accessibility-audit-2026-11-16-run3.json)
(first pass, 2026-11-16T09:38:16Z),
[`accessibility-audit-2026-11-16-run4.json`](accessibility-audit-2026-11-16-run4.json)
(repeat after the build and keyboard tests, 2026-11-16T09:54:00Z), and
[`accessibility-audit-2026-11-16-run5.json`](accessibility-audit-2026-11-16-run5.json)
(final pass from the final tree, 2026-11-16T10:00:20Z).

| Page | 320 px | 375 px | 768 px | 1440 px |
| --- | --- | --- | --- | --- |
| Playground | pass | pass | pass | pass |
| Documents | pass | pass | pass | pass |
| Chunk Review | pass | pass | pass | pass |
| Workflow | pass | pass | pass | pass |
| Rules | pass | pass | pass | pass |
| Tests | pass | pass | pass | pass |

At every page/width combination: critical 0, serious 0, moderate 0, minor 0, horizontal overflow
false. The relevant mobile layout fixes included single-column Workflow step cards,
single-column Chunk Review tabs below `sm`, wrapping Tests headers/rows, and removing fixed-width
truncating labels that pushed narrow layouts sideways.

## Limitations

- The audit uses synthetic fixtures and styled jsdom snapshots, not an authenticated session against
  the live backend. It covers rendered structure, contrast and responsive width, but not
  server-provided content outside those fixtures.
- jsdom cannot reproduce native dialog top-layer behaviour, focus trapping or Escape handling.
  The Documents keyboard test therefore proves keyboard opening and dismissal, while native
  `<dialog>` semantics remain the production focus-containment mechanism.
- Screen-reader announcements, zoom/reflow beyond the tested widths, and browser-specific
  keyboard differences were not manually tested. A real authenticated browser walkthrough remains
  the recommended pre-release check when test credentials and a seeded workspace are available.
