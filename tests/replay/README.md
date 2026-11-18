# Synthetic answer replay

Run `python -m pytest tests/replay -q` or `python tests/replay/harness.py`.

These are new live captures from the real Qwen3-4B/PubMedBERT audit stack on
2026-11-15, **not original historical recordings**. Both FINAL and DRAFT local
synthetic integration-test documents were loaded. Source text, source order and
generation response/finish metadata are preserved; source scores and generation
prompts containing the fictional case are deliberately omitted. Request IDs link
the captures to text-free logs. Nothing here is clinical evidence.

`contexts.json` stores each exact source once. `a.json`, `b.json`, `c.json` map
recorded source tags to it. Offline replay uses the captured topic stems, not new
planning or a GPU. An unchanged verified count for (a) does **not** prove that the
requested marker was answered: this capture returned other exact source statements.

`postfix-4b.json` preserves two later real 4B generations against those fixed source
contexts, with no retrieval rerun and no patient context. The (a) question now explicitly
requests FINAL/page 3, so this is not an identical end-to-end comparison. Its FINAL
marker verifies; a second item fails an exact quote check because the model appended
`NOT_FOUND`. In (c), both full-paragraph quotes include an unrelated `must not be used`
warning and fail the unchanged negation check. These rejected drafts remain rejected.

`comparison.json` records all fourteen generations from two exact-payload bundles
on the same Tesla T4: five captured baseline payloads and two separately revised
no-patient-context payloads, each on 4B and 8B-AWQ. It includes full outputs or an
`outputRef` to a byte-identical baseline output, numeric expectations, and shared
payload hashes; private prompts are omitted. All fourteen outputs/hashes/counters
were independently matched against the private Colab result files. Count equality
is not answer fidelity: both models still include DRAFT in the revised FINAL
question, and the 8B baseline (b-2) verified cautions are not sepsis management.

For (b), `empty_draft=1` per problem records literal `NOT_FOUND`, not quote rejection.
Histograms count each reason at most once per rejected parsed candidate; a candidate
can have multiple reasons. Empty/malformed/truncated draft events have no parsed
candidate. Duplicates are excluded and counted separately. Every reason key is
closed, numeric and text-free. Diagnostics never count as passing evidence.
