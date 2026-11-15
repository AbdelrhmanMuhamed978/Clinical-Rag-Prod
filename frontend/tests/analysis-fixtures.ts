import type { PipelineProgress, PipelineResult } from "@/lib/analysis";

export function pipelineResult(): PipelineResult {
  return {
    plan_source: "single_question", version: "3.2", disclaimer: "Decision support only. Check the source guideline and use clinical judgment.",
    not_covered: ["Uncovered topic"], warnings: ["Some citations come from DRAFT guidelines. Do not treat them as final."],
    problems: [{
      title: "Synthetic guideline question", status: "ok", queries: ["PRIVATE_QUERY_MARKER"],
      items: [{
        recommendation: "Synthetic verified recommendation.", quote: "Exact source words.\n  Keep this spacing.",
        citation: { id: "S1", label: "Synthetic Society 2024", section: "Source section", page: "12", strength: "strong", draft: true },
        patient_notes: [{ id: "patient-check", title: "Patient-specific screening check", level: "individual_review", reasons: ["PRIVATE_PATIENT_CHECK_MARKER"] }],
      }],
      unverified: [{ recommendation: "REJECTED_STATEMENT_MARKER", quote: "REJECTED_QUOTE_MARKER", issues: ["Quote not found in cited source.", "Scope mismatch."] }],
      sources: [{ id: "S1", label: "Synthetic Society 2024", section: "Source section", page: "12", strength: "strong", draft: true, excerpt: "RETRIEVED_EXCERPT_MARKER" }],
    }, {
      title: "Uncovered topic", status: "not_found", queries: [], items: [], unverified: [], sources: [],
    }],
  };
}

export function progressEvent(state: PipelineProgress["state"] = "started", mode: PipelineProgress["mode"] = "question"): PipelineProgress {
  return { message: "Checking synthetic source evidence", stage: "verification", state, stepId: "verification-1", mode };
}

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
