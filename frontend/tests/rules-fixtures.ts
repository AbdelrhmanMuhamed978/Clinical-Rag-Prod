import asset from "../../python-backend/clinical-vocabulary.json";
import type { DrugCatalogSnapshot, Rule, RulePreview, RuleVocabulary } from "@/lib/types";

// Synthetic fixtures only; thresholds and example.org wording are not clinical evidence.
export const vocabulary = asset as RuleVocabulary;
export function syntheticRule(overrides: Partial<Rule> = {}): Rule {
  return {
    id: "rule-1", workspaceId: "workspace-1", ruleId: "synthetic-screen", title: "Synthetic screening candidate",
    drugClasses: '[ "nsaid" ]', appliesWhen: '{ "any": [ { "field": "creatinine", "op": ">=", "value": 1.5 } ] }',
    level: "individual_review", message: "Synthetic review message.", caveat: "Test fixture, not clinical advice.",
    sources: JSON.stringify([{ title: "Synthetic evidence", url: "https://example.org/source", quote: "Synthetic wording.",
      provenance: { keep: [1, 2], fetchedAt: "2026-11-15" }, future: true }], null, 2),
    linkTerms: "renal|kidney", caseLevel: false, enabled: false, status: "draft", revision: 3,
    createdBy: "author-1", updatedBy: "author-1", createdAt: "2026-11-15T00:00:00Z", updatedAt: "2026-11-15T00:01:00Z",
    reviewedBy: null, reviewedAt: null, reviewedCatalogRevision: null, ...overrides,
  };
}
export function catalogSnapshot(overrides: Partial<DrugCatalogSnapshot> = {}): DrugCatalogSnapshot {
  return { revision: 2, isDefault: false, updatedBy: "author-1", updatedAt: "2026-11-15T00:00:00Z",
    catalog: { syntheticdrug: { classes: ["nsaid", "nephrotoxin"], brands: ["Synthetic Brand"], regional_names: ["اسم تجريبي"] } }, ...overrides };
}
export function previewResult(overrides: Partial<RulePreview> = {}): RulePreview {
  return { would_fire: true, catalog_revision: 2, reasons: ["Synthetic condition matched."], warnings: [],
    values: { creatinine: 2 }, derived: {},
    unit_details: { creatinine: { unit: "mg/dL", source_unit: null, converted: false, assumed: true } },
    meds: [], regex: { matched: true, timed_out: false }, notice: "Simulation only; not treatment advice.", ...overrides };
}
export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
export function unloadIsBlocked(): boolean {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}
