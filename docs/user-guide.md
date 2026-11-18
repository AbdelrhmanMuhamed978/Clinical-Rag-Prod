# Clinical RAG Platform — User Guide

This guide is for clinicians, reviewers, guideline editors and workspace owners using the web application. It covers the normal workflow from sign-in to a verified answer, plus the controls for chunking, testing, publishing and administration.

> **Safety:** The application is decision support only. It is not a substitute for clinical judgement, local policy or primary-source review. Only statements that pass deterministic quote, number, wording and scope checks appear as answers. Use synthetic or de-identified cases; do not enter patient identifiers.

---

## 1. What the application does

The platform turns uploaded guideline PDFs into searchable evidence, then answers questions or full cases using only that evidence.

The main workflow is:

```text
Workspace → Upload PDF → Parse → Select pages → Chunk → Review → Embed
          → Ask a question / full case → Review verified answer
          → Flag a problem / save the analysis
```

A separate governance workflow controls how answers are produced:

```text
Workflow draft → Run tests → Publish configuration
Rules → Submit for clinician review → Approve → Enable
```

---

## 2. Sign in and choose a workspace

1. Open the frontend URL (local default: `http://localhost:3000`).
2. Sign in with your verified account. Unverified accounts cannot use protected workspace routes.
3. On **Workspaces**, create a workspace with **New workspace name → Create**, or open an existing workspace card.
4. Use the sidebar to move between **Documents**, **Workflow**, **Rules**, **Tests**, **Playground** and **Settings**.

A workspace is the security and data boundary. Documents, chunks, conversations, rules, tests and configuration in one workspace are not visible from another.

### Roles

| Role | Typical use |
| --- | --- |
| **Viewer** | Read workspace resources and saved analyses. No editing, review, analysis runs or administration. |
| **Editor** | Upload/parse/chunk/review/embed documents, edit workflow drafts, create/edit rules and test cases. Cannot approve rules or administer members. |
| **Reviewer** | Review, approve, retire and toggle clinical rules. Does not edit definitions or administer the workspace. Reviewer is a separate capability, not a higher rank. |
| **Owner** | Full workspace administration, publication, editing and review. At least one owner must remain. |

Running an analysis requires **editor or owner** access.

### Invitations

An owner can invite an email address with a role. The app saves **pending access**; it does not send an invitation email. Access starts when that same verified email next signs in. Owners can cancel a pending invitation from **Settings**.

---

## 3. Navigation overview

| Page | Purpose |
| --- | --- |
| **Documents** | Upload, parse and open guidelines; see page/chunk counts and processing status. |
| **Chunk Review** | Select pages, run background chunk jobs, review chunks, reset pages/embeddings and embed accepted evidence. |
| **Playground** | Ask a question or submit a full case; inspect verified answers, citations, DRAFT warnings and Not covered groups. |
| **Tests** | Maintain in-app test cases and run them against the application. |
| **Workflow** | View/edit the six pipeline stages, save drafts, run tests and publish versions. |
| **Rules** | Create and edit clinical rules; submit, approve, enable or retire them. |
| **Settings** | Colab connection (platform administrators), members, invitations, rename, leave and delete. |

---

## 4. Documents: upload and parse a guideline

### 4.1 Upload

1. Open **Documents**.
2. Select **Upload guideline**.
3. Choose one PDF (up to 50 MB).
4. Complete the required metadata:

| Field | Required | Notes |
| --- | --- | --- |
| PDF | Yes | One guideline PDF. |
| Publishing society / organization | Yes | Used for provenance and display. |
| Title | Yes | Human-readable guideline title. |
| Guideline year | Yes | 1900–2100. |
| Guideline status | Yes | **DRAFT**, **FINAL** or **SUPERSEDED**. Unknown status is treated as DRAFT. |
| Version | No | e.g. `v2`, `2024 update`. |
| Effective date | No | ISO date shown on the document card. |
| Review-by date | No | Local review date. |
| Source URL | No | Original publisher/reference URL. |
| Permission / licence note | No | Record why the PDF may be used. |

5. Select **Upload and parse**. Parsing starts automatically on the server; you do not need to stay on the page.

### 4.2 Parse states

| State | Meaning |
| --- | --- |
| `uploaded` | File saved; queued for automatic parsing. |
| `parsing` | Server is extracting text. Progress refreshes automatically. |
| `parsed` | Text is available; the document can be opened in Chunk Review. |
| `failed` | Parsing stopped. The card shows a safe reason and a **Retry Parse** button when retryable. |
| `chunking` / `embedding` | A document operation is active. Editing/reset controls are temporarily disabled. |
| `review` / `indexed` | Chunking/review or embedding has advanced. |

Use **Retry Parse** after a transient Colab/network failure. Use **Delete** only when you want to remove the document and its stored file. A failed upload can be deleted and uploaded again.

### 4.3 OCR warning

The parser does **not** perform OCR.

- A page is reported as `needs OCR` only when it has very little extractable text **and** at least 50% of the page is covered by a displayed image.
- A short chapter divider with a little text is no longer labelled as scanned.
- A genuinely image-only/scanned page is flagged, omitted from indexed text, and shown as a warning.
- To use a scanned page, run OCR in a separate trusted tool first, then upload a text-layer PDF.

Treat OCR warnings as a data-quality signal, not as proof that the page contains no useful clinical content. Review the source PDF before relying on surrounding chunks.

---

## 5. Chunk Review

Open a parsed document with **Choose pages / Review / Embed**.

The page has three review tabs and a background-job panel:

- **Document Text** — parsed pages and page checkboxes.
- **Pending Chunks** — chunks awaiting clinical review.
- **Reviewed** — accepted, rejected and embedded chunks.
- **Background chunk job** — durable page selection and job controls.
- **Chunking Progress** — saved page checkpoint and total pages.
- **Document reset controls** — reset pages or embeddings.

### 5.1 Select pages

1. Open **Document Text**.
2. Use **Select unchunked** to restore the default, or **Clear** to start empty.
3. Check individual **Page N** boxes. Selecting non-contiguous pages is supported.
4. Processed pages are disabled and marked **Processed**.
5. Choose at most 1000 pages per request.
6. Edit **Chunker guidance** if needed, for example: “keep each recommendation with its strength grade; never split tables.”
7. Choose a chunking path:
   - **Preferred:** **Start job: all remaining** or **Start job: selected** in the Background chunk job panel.
   - **Compatibility only:** **Chunk All Portions** or **Chunk Selected** above the tabs. These use the legacy synchronous request and can be interrupted if the page or network closes.

Guidance from a valid request is saved and prefilled next time. An untouched guidance field follows the saved value; your local edit is not overwritten by refreshes.

### 5.2 Background chunk job settings

| Setting | Range | Default | Meaning |
| --- | --- | --- | --- |
| Batch size | 1–10 pages | 2 | Pages sent to the model per batch. Smaller batches checkpoint more often; larger batches are faster. |
| Max tokens | 100–2000 | 400 | Model output limit per batch. |
| Batch timeout | 60–600 s | 300 s | Maximum time for one batch, including its retries. |
| Max retries | 0–3 | 1 | Retries for a failed/time-out batch. |
| On batch failure | Continue / Stop | Continue | `Continue` records the failed pages and proceeds; `Stop` ends the job after the failed batch. |

Start with the defaults. Use smaller batches and a longer timeout for dense tables or slow model responses.

### 5.3 Start and monitor a job

1. Select the pages.
2. Review the settings.
3. Click **Start job: selected (N)** (or **Start job: all remaining (N)**).
4. The job returns immediately with status **Queued**; the server worker owns it from then on.
5. Use the shared **Live updates** controls:

| Control | Effect |
| --- | --- |
| **Auto-poll all page reads** | Periodically refresh document status, chunk list, chunking progress and job status. |
| **Interval** | Choose **2 s**, **5 s**, **10 s** or **30 s**. The default is 2 s. |
| **Poll now** | Refresh everything once, even when auto-poll is off. |
| **Stop polling** | Stop **all** periodic page reads. It does **not** cancel the job. |
| **Last update / Next poll** | Show when the page last refreshed and when the next automatic refresh is due. **Manual** means no timer is running. |

The preference is saved **per document** in this browser. Leaving auto-poll off and reloading keeps the page manual-only until you use **Poll now** or **Refresh**.

While auto-poll is on, the page uses the lightweight processing-state endpoint. The full document, chunk list and progress evidence are refreshed only when the state actually changes, when you use **Poll now/Refresh**, or when the job reaches a terminal state. This avoids re-downloading the full chunk list every 2 seconds. A hidden tab pauses automatic polling.

Closing the page, changing tabs, losing focus or stopping polling does not cancel the worker. Only **Pause** and **Cancel** affect the job.

### 5.4 Job statuses

| Status | Meaning |
| --- | --- |
| `queued` | Waiting for the worker. |
| `running` | A batch is being processed. |
| `pausing` | Pause requested; the current batch is allowed to finish. |
| `paused` | Stopped at a batch boundary. Resume or cancel. |
| `stopping` | Cancel requested; the current batch is allowed to finish. |
| `cancelled` | Stopped by request. Completed pages are kept. |
| `completed` | All selected pages finished. |
| `partial` | Some pages completed and some failed, or not all document pages were selected. |
| `failed` | No pages completed, or the job ended with failures. Check the safe error code. |
| `blocked` | Colab/model unavailable or a batch timed out. Completed pages are kept; resume when the service is ready. |
| `interrupted` | The server process or lease stopped unexpectedly. Resume or retry. |

The job view shows page arrays (`completed`, `failed`, `skipped`, `remaining`), batch counts and a current batch range. It never contains chunk text.

### 5.5 Pause, resume, cancel and retry

- **Pause after current batch** — the in-flight batch commits, then the job pauses. A queued job pauses immediately.
- **Resume job** — returns a paused, blocked or interrupted job to the queue.
- **Cancel job** — the in-flight batch commits, then the job stops. Completed pages are kept.
- **Retry failed (N)** — creates a new job for failed pages only.
- **Retry remaining (N)** — creates a new job for pages not completed, failed or skipped.
- Retry is disabled while the document is busy or when the selected pages overlap accepted/embedded chunks.
- A retry is a **new linked job**. The original job and its page history remain available.

If the browser reloads or you leave and return, the page reattaches to the active job automatically. If the server restarts mid-job, completed batches remain; an interrupted job must be resumed, retried or cancelled explicitly.

### 5.6 Read job errors safely

The panel shows fixed categories rather than upstream response text:

| Code | What to do |
| --- | --- |
| `colab_unavailable` | Wait for Colab readiness, then **Resume job**. |
| `batch_timeout` | Resume, or retry failed pages with a larger timeout/smaller batch. |
| `invalid_batch_result` | Retry failed pages; if it repeats, inspect the source page and model logs. |
| `empty_batch_result` | Retry failed pages; the model returned neither a chunk nor a skip for a page. |
| `worker_error` | Resume or retry remaining pages; check server logs. |
| `batch_failed` | Retry failed pages. |

Completed pages are never deleted by a later failed batch.

### 5.7 Review chunks

Open **Pending Chunks**.

- **Accept** keeps a chunk as evidence for embedding.
- **Reject** removes it from the evidence path.
- **Accept all ≥ 0.85 (N)** bulk-accepts pending chunks at or above the confidence threshold. Adjust **Min confidence:** to change the threshold.
- **Reject Front Matter (N)** rejects chunks classified as front matter.
- Expand a chunk to inspect its full exact text, page, section, confidence, type and verbatim status.
- A **Needs re-review** badge means the source page text changed after a re-parse. The chunk is shown for review, is excluded from accepted/embedded counts and cannot be served until it is reviewed again.
- Review actions record the authenticated reviewer and time. Edited reviewed text/sections return to accepted and invalidate vectors.

Important checks:

- **Verbatim ✓** means the deterministic verifier found the quote in the source. “Verbatim check unknown” is not a pass.
- A high confidence score is not clinical validation.
- Never accept a chunk you have not read, especially tables, dosing text and negations.

### 5.8 Embed accepted evidence

1. Review pending chunks.
2. Confirm the accepted count.
3. Select **Embed (N)**.
4. Wait for completion. The document becomes `indexed` only when accepted chunks are embedded and no pending review remains.
5. Embedding a reviewed subset does **not** mean every page was selected or indexed. Check the page/chunk counts.

If the embedding model or dimensions change, the workspace’s existing vectors may need re-embedding. Use **Reset embeddings** only after understanding that all embedded chunks return to accepted and must be re-embedded.

### 5.9 Reset, re-parse and embeddings

Under **Document reset controls**:

- **Reset selected pages** and **Reset all pages** remove chunks/vectors whose source ranges overlap the selected pages, clear their processed-page flags, and require confirmation.
- Reset never automatically re-chunks. After resetting, explicitly select pages and start a new job.
- Resetting an overlapping range can clear more than the exact selected page.
- **Re-parse source PDF** re-reads the stored PDF and replaces the parsed text after confirmation. Chunks on pages whose normalized text changed are kept but marked **Needs re-review**, returned to pending and stripped of their stale vectors. Chunks on unchanged pages keep their review state and vectors. If re-parse fails, existing text, chunks and reviews are kept.
- A **Needs re-review** chunk is excluded from the accepted/embedded counts until a reviewer reviews it again. Check the changed-page and marked-chunk counts in the confirmation result.
- **Reset embeddings** keeps source text and review attribution, clears vectors and embedding progress, and returns embedded chunks to accepted.
- Reset is irreversible for the deleted chunks/vectors. Regenerate from the source PDF if needed.

### 5.10 Recommended chunking sequence

1. Parse the PDF.
2. Skim **Document Text** and the OCR warning.
3. Start a background job for a small page set first.
4. Watch one batch complete; pause and inspect the chunks.
5. Resume or start the rest with appropriate batch size/timeout.
6. Review pending chunks; reject front matter and unsafe/malformed content.
7. Accept only verified evidence.
8. Embed accepted chunks.
9. Confirm the document status and counts before using it in Playground.

---

## 6. Playground: ask a question or a full case

Open **Playground**. Editor or owner access is required to run an analysis.

### 6.1 Question mode

Use question mode for a focused clinical question, for example:

> In an adult with suspected septic shock, when should vasopressors be started?

The pipeline retrieves relevant guideline chunks, drafts, then verifies quotes, numbers, wording, scope and topic. Only verified recommendations appear as answers.

### 6.2 Full case mode

Use case mode for a de-identified case description. The pipeline extracts values and problems, plans questions, retrieves evidence, drafts and verifies.

- Do not paste names, MRNs, dates of birth, addresses, contact details or other identifiers.
- Case mode is intended for decision support and evidence review, not diagnosis or prescribing.
- Facts extracted from the case are transient and are not treated as guideline evidence.

### 6.3 Reading a verified answer

| Element | Meaning |
| --- | --- |
| Recommendation | A statement that passed deterministic verification. |
| Citation chip (`S1`, `S2`, …) | Source chunk identity used for the statement. |
| Page / section | Source location from the guideline metadata, not model invention. Section paths use `Treatment > Induction therapy`. |
| Strength / certainty | Taken only from an explicit grade in the same paragraph or table row, such as `(Strong recommendation, high-certainty evidence)`. Otherwise shown as not stated. |
| **FINAL** | Source guideline status is final. |
| **DRAFT** | Source is draft; the warning must remain visible. |
| Exact quote | Source text used for verification. Table evidence is shown as a row plus its column headers. |
| Related, may not answer your question | Verified statements that were not close enough to the question to be the lead answer. Do not treat them as the answer. |
| Question coverage | Lists each stated part of the question as answered, related only or not covered. |
| **Not covered** | The loaded guidelines did not contain a verifiable statement for that problem. It does **not** mean the topic is safe or unimportant. |
| Excluded drafts | Collapsed section containing rejected/unverified material with reasons only. Never treat it as an answer. |
| Clinical disclaimer | Decision-support warning; remains part of the result. |

Formatting differences that do not change meaning—Unicode superscripts such as `mm³` versus `mm3`, HTML entities, dash spacing and parser-recorded footnote letters—are ignored when locating the quote. Altered numbers, negations, strength, scope or added words are still rejected. A table quote must be an exact row label plus one exact cell; a paraphrased table sentence is not accepted.

A question is classified by its wording or by the absence of extracted vitals, labs, drugs, age and sex—not by word count. Verified statements that are only loosely related are moved to **Related, may not answer your question** and never become the lead answer. Use **Question coverage** to see which stated parts were answered, related only or not covered.

If the pipeline cannot retrieve or verify evidence, the answer is withheld and a safe reason category is shown. No free-text model answer is substituted.

### 6.4 Conversation memory

- The browser keeps at most six verified question/answer turns in local state.
- Saved summaries include only verified recommendations; drafts, patient checks and source excerpts are excluded.
- History can resolve references such as “what about the second option?” but fresh guideline evidence must still pass verification.
- **Clear history** cancels in-flight work and starts a new conversation identity.
- Changing workspace or user clears the conversation.

### 6.5 Flag an answer

Open **Flag this answer for governance review**:

1. Choose a reason: Incorrect statement, Potentially unsafe, Outdated guideline, Missing important context, Citation mismatch or Other.
2. Add an optional note (maximum 1000 characters). **Do not include patient or case text.**
3. Select **Record flag**.

The flag stores the run ID, configuration version, reason category and note. It does not copy the case into the flag. The owner reviews flags through the governance/operations process.

### 6.6 Saved analyses

**My analyses** lists runs saved under your account and workspace. Saved results retain privacy-safe evidence metadata; audit-only runs may open without a renderable answer. Deleting a saved analysis removes that saved record, not the underlying guideline evidence.

---

## 7. Tests

Open **Tests** to maintain the in-app test cases used as governance evidence.

### 7.1 Create a test case

1. Enter a **name**.
2. Enter the case/question input text.
3. Optionally enter expected output JSON. The page shows the expected schema.
4. Save the case.

Test cases are synthetic or de-identified; do not store real patient data.

### 7.2 Run tests

- **Run all N** runs every case.
- **Run selected (N)** runs the checked cases.
- **Run** on a card runs one case.
- Results show pass/fail, duration and a bounded diff. “Last run” is retained on the case card.
- Running uses the verified pipeline; tests do not prove clinical validity.

Use the **Tests** page for focused subsets. The **Workflow** page runs the current suite as part of the publish gate.

### 7.3 Delete cases

Use **Delete** on a card or **Delete selected**. Deletion removes the test case and its history; confirm the selection first.

---

## 8. Workflow and publishing

Open **Workflow** to manage the configuration that produces answers.

### 8.1 The six stages

| Step | What it controls |
| --- | --- |
| **Read case** | Clinical value/medication extraction and canonical units. |
| **Plan** | How clinical problems and focused guideline questions are identified. |
| **Retrieve** | Retrieval/reranking limits and query behavior. |
| **Draft** | Generation instructions and output limits. |
| **Verify** | Deterministic quote/number/wording/scope checks. |
| **Reconcile** | Linking patient problems to existing rules. |

The panels are read-only. Edits are made through the workflow fields; **Reconcile** uses the separate rule/patient-flag flow.

### 8.2 Drafts and versions

1. Edit the workflow fields. The app preserves untouched JSON keys.
2. Select **Save draft**. A new draft version is created.
3. Use **Reload published** to discard unsaved edits and return to the published configuration.
4. **Discard** removes the selected draft but keeps version history.
5. **Restore as draft** creates a new draft from a historical version; published stays unchanged and fresh tests are required.

Drafts never serve live analyses. Only the published version is used by Playground and tests unless the workflow explicitly runs against a selected draft.

### 8.3 Test and publish

1. Select the draft.
2. Select **Run tests** (or use **Tests** for subsets).
3. Review progress and failures. A failed or incomplete run blocks publication.
4. Select **Publish** only when the server gate reports that the current suite passed for that exact draft.
5. Publishing is **owner-only**. Editors may edit, save and test drafts but cannot publish.

The server, not the browser’s cached result, decides whether publication is allowed. If someone else publishes or changes a version, reload before saving or publishing.

### 8.4 Compare versions

Use the version list/diff to inspect configuration changes. Diffs summarize changed configuration entries without storing clinical case text. Publishing a new version archives the previous one; it does not erase history.

---

## 9. Rules

Open **Rules** to manage clinical rules used by the pipeline.

### 9.1 Create or edit

Editors can create and edit rule definitions, recognition terms and configuration drafts. A rule includes its clinical logic, applicability and evidence links. Save changes as a new revision; unchanged fields are preserved.

### 9.2 Review and approval

1. Submit the rule for review.
2. A **reviewer or owner** approves the exact revision and records reviewer identity/time plus catalog binding.
3. Approval does not automatically enable the rule.
4. An owner or reviewer can **enable**, **disable** or **retire** the rule.
5. Definition changes clear approval. Exact no-ops preserve it; partial edits preserve omitted fields.

Editors cannot self-approve. Only current, approved and enabled rules enter the pipeline. Non-case-level rules must also match the current recognition catalog revision. Review is audited; the rule’s review state and history remain visible.

### 9.3 Rule states

| State | Meaning |
| --- | --- |
| `draft` | Editable; not approved. |
| `in_review` | Submitted; awaiting reviewer/owner decision. |
| `approved` | Approved at the recorded revision; may still be disabled. |
| `retired` | No longer used; retained for history. |

---

## 10. Settings and administration

Open **Settings**.

### 10.1 Colab operations (platform administrators)

The **Colab operations** card shows:

- Live model readiness and model/embedding identity.
- GPU device, total/used memory.
- Connection revision, last heartbeat and stale status.
- Manual override state.

Administrators can set the **Colab base URL** and a **replacement API key**. The key is protected in the backend and never returned to the browser. Changes affect **every workspace**.

**Manual override** blocks notebook registration and heartbeats. After releasing it, rerun the notebook tunnel/registration cell; an old heartbeat cannot restore the connection.

### 10.2 Members and invitations (owners)

- **Invite member** — enter an email, choose viewer/editor/reviewer/owner, confirm. Access begins when that verified email signs in. No email is sent by the app.
- **Pending invitations** — cancel an unused invitation. If it was already accepted, refresh the member list instead.
- **Change role** — select the role and save. Reviewer is a separate review capability, not an administrative role.
- **Remove member** — removes workspace access only; the account and audit history remain.
- A workspace must retain at least one owner. Assign another owner before demoting, removing or leaving as the last owner.

### 10.3 Rename, leave and delete

- **Rename workspace** requires the expected current name; conflicts require refresh.
- **Leave workspace** removes your access. The last owner must assign another owner first.
- **Delete workspace** soft-deletes the workspace and permanently deletes its documents, chunks, chunk jobs, guidance and uploaded PDFs. Configuration/rule/catalog/audit history remains. Access ends immediately. There is no restore API.

Deletion is blocked while a document is being processed. Resolve or cancel the operation first. Deletion is not a full account-erasure or retention-policy substitute.

### 10.4 API base

The API base is read-only and comes from the frontend deployment configuration. Contact the operator if it is wrong.

---

## 11. Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| `/health` returns 200 but `/ready` returns 503 | A dependency is not ready. Colab, database or pipeline can fail independently. | Read the `checks` object. Chunking needs Colab; analysis needs Colab and pipeline. |
| Colab status is offline / model is `null` | No fresh heartbeat, tunnel down, wrong secret, or manual override. | Check the Colab tab, heartbeat, registration secret and manual override. Rerun the notebook registration cell. |
| Colab registration returns 401 | `COLAB_REGISTRATION_SECRET` does not match the backend. | Set the same value on both sides, restart the API if needed, rerun cell 6. |
| Pipeline is unavailable | Python service, service key, backend URL or readiness check failed. | Chunking/review still work; restore the pipeline before running analyses/tests. |
| Upload/parse fails | Colab/parse dependency or malformed PDF. | Read the safe reason; use **Retry Parse**. If it persists, inspect the PDF and operator logs. |
| OCR warning on a page | Page has little text and a large displayed image. | Run OCR externally, then re-upload a text-layer PDF. Short dividers should not be flagged. |
| Chunk job stays queued | Worker disabled/not running, or API down. | Check `Documents:ChunkJobs`, API logs and `/health`. The job persists and resumes when a worker runs. |
| Chunk job is blocked/interrupted | Colab outage, timeout, or server restart. | **Resume job** when ready, or **Retry failed/remaining**. Completed batches remain. |
| Cancelled job shows completed pages | Cancel takes effect at the next batch boundary. | Expected. Retry remaining for unprocessed pages. |
| Job status not updating | Auto-poll off, polling stopped, tab hidden, or transient read failure. | Use **Poll now**, re-enable auto-poll, or reload. Stopping polling does not cancel the job. |
| Controls are disabled | Document is busy, active job, read failure, or insufficient role. | Wait for processing, resolve the job, refresh, or ask an owner/editor. |
| “Some selected pages overlap reviewed chunks” | Accepted/embedded chunks exist on those pages. | Reset those pages first, or select different pages. |
| “This document is being processed or changed” | Another operation or a stale processing claim. | Wait for it to finish; refresh. Do not start a second operation. |
| Embed is disabled | No accepted chunks, active processing, or read failure. | Review/accept chunks, finish processing and refresh. |
| Publish is blocked | Draft tests failed, no current passing run, stale published version, or not owner. | Run the current suite, reload, fix failures, and ask an owner to publish. |
| Sign-in succeeds but workspace is empty | No membership/invitation accepted. | Ask an owner to invite the same verified email; sign in again. |
| Uploaded file is a failed duplicate | Earlier parse failed. | Delete the failed row or retry it; do not upload multiple copies blindly. |

### Diagnostic endpoints

- `GET /health` — process liveness only.
- `GET /ready` — database schema, Colab model/GPU and pipeline readiness, with sanitized detail.
- Browser network/console errors may contain a request ID; use it to correlate server logs without sharing case text.

---

## 12. Privacy, safety and retention

- **Decision support only.** Verified quotes reduce, but do not eliminate, clinical risk. Review the source guideline and the patient context.
- **Use de-identified inputs.** Do not enter names, MRNs, dates of birth, addresses, contact details or other identifiers in questions, cases, test cases, flags or notes.
- **Flags** store the run ID, configuration version, reason and optional note. Do not put case text in a flag note.
- **Result storage** is controlled by the `Privacy:StoreResults` setting. When unset, short questions may be stored while cases and follow-ups are not. Explicit `false` disables storage; `true` opts in to a privacy-projected evidence-only case record. Ask the operator if you are unsure.
- **Audit-only runs** can be listed/opened without a renderable answer; legacy free text is not turned into an answer.
- **Reset** of pages/embeddings is destructive for the affected chunks/vectors. There is no undo.
- **Workspace deletion** is a soft-delete of the workspace but a hard delete of its documents, chunks, jobs, guidance and uploads. Audit/config/rule history remains. Backups and external retention are separate operator responsibilities.
- **No clinical correctness or patient-safety certification** is implied by a passing test or a published configuration.

---

## 13. Quick reference

### Chunk/review statuses

| Status | Meaning |
| --- | --- |
| `pending` | Awaiting review. |
| `accepted` | Approved evidence; eligible for embedding. |
| `rejected` | Excluded from evidence. |
| `embedded` | Has a current vector and participates in retrieval. |
| `failed` | Chunking/processing failure; inspect the safe reason. |

### Role matrix

| Action | Viewer | Editor | Reviewer | Owner |
| --- | --- | --- | --- | --- |
| Read documents/results | Yes | Yes | Yes | Yes |
| Upload/parse/chunk/review/embed | No | Yes | No | Yes |
| Ask/run analysis | No | Yes | No | Yes |
| Edit workflow draft / test cases | No | Yes | No | Yes |
| Run draft tests | No | Yes | No | Yes |
| Publish configuration | No | No | No | Yes |
| Create/edit rules | No | Yes | No | Yes |
| Approve/enable/retire rules | No | No | Yes | Yes |
| Invite/remove members, delete workspace | No | No | No | Yes |

### End-to-end checklist

1. Sign in and open the correct workspace.
2. Upload a PDF with complete provenance/status metadata.
3. Wait for parsing; retry or delete a failed parse.
4. Check OCR warnings.
5. Select pages; save guidance; start a background job.
6. Pause and inspect the first batch if the document is dense.
7. Resume/complete; confirm completed pages and counts.
8. Review pending chunks; reject front matter and unsafe text.
9. Accept verified evidence and embed.
10. Ask a question/full case in Playground.
11. Read only verified recommendations; inspect citations and DRAFT warnings.
12. Flag any problem without case text.
13. Run tests; fix draft configuration; publish as owner only after a passing gate.
14. Review/approve/enable rules through the separate clinician-review flow.

---

*For deployment, secrets, migrations, readiness and operator recovery, see [operations.md](operations.md) and [architecture.md](architecture.md).*
