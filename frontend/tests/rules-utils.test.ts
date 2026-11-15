import { describe, expect, it } from "vitest";
import { catalogDefinition, catalogRows, currentRuleApproval, newRuleForm, readRuleForm, ruleDefinition, rulePatch,
  sameCatalogRows, sameRuleForm, sourceObject, validateCatalogRows, validateRuleForm } from "@/lib/rules";
import { catalogSnapshot, syntheticRule, vocabulary } from "./rules-fixtures";

describe("structured rule serialization", () => {
  it("round-trips supported JSON byte-for-byte and sends only the revision for an exact no-op", () => {
    const rule = syntheticRule(); const { form, problems } = readRuleForm(rule);
    expect(problems).toEqual([]);
    expect(validateRuleForm(form, vocabulary)).toEqual({});
    const definition = ruleDefinition(form, rule);
    expect(definition.drugClasses).toBe(rule.drugClasses);
    expect(definition.appliesWhen).toBe(rule.appliesWhen);
    expect(definition.sources).toBe(rule.sources);
    expect(rulePatch(form, rule)).toEqual({ expectedRevision: 3 });
  });
  it("preserves absent/null quotes, unknown source metadata, and disabled-only create semantics", () => {
    for (const original of [{ title: "Test", url: "https://example.org" }, { title: "Test", url: "https://example.org", quote: null, future: [1] }]) {
      const rule = syntheticRule({ sources: JSON.stringify([original]), enabled: true });
      const { form } = readRuleForm(rule);
      expect(sourceObject(form.sources[0])).toEqual(original);
      expect(ruleDefinition(form, rule).enabled).toBe(false);
      expect(rulePatch(form, rule)).not.toHaveProperty("enabled");
    }
  });
  it("changes only edited source properties while retaining copied provenance", () => {
    const rule = syntheticRule(); const { form } = readRuleForm(rule);
    form.sources[0] = { ...form.sources[0], title: "Edited evidence", quote: "Edited copied wording." };
    const patch = rulePatch(form, rule);
    expect(Object.keys(patch).sort()).toEqual(["expectedRevision", "sources"]);
    expect(JSON.parse(patch.sources!)[0]).toEqual({ ...JSON.parse(rule.sources!)[0], title: "Edited evidence", quote: "Edited copied wording." });
  });
  it("keeps editor row keys out of payloads and out of dirty comparisons", () => {
    const { form } = readRuleForm(syntheticRule());
    const changedKeys = { ...form, conditions: form.conditions.map(row => ({ ...row, key: "other-condition" })),
      sources: form.sources.map(source => ({ ...source, key: "other-source" })) };
    expect(sameRuleForm(form, changedKeys)).toBe(true);
    expect(ruleDefinition(changedKeys).appliesWhen).not.toContain("key");
    expect(ruleDefinition(changedKeys).sources).not.toContain("other-source");
  });
  it("reports unrepresentable legacy groups instead of pretending they are valid editor rows", () => {
    const rule = syntheticRule({ drugClasses: "broken", appliesWhen: '{"any":[{"field":"creatinine","op":">=","value":1.5,"future":true}]}', sources: "null" });
    const { form, problems } = readRuleForm(rule);
    expect(problems.length).toBeGreaterThanOrEqual(3);
    expect(form.conditions).toEqual([]);
    expect(form.sources).toEqual([]);
    expect(validateRuleForm(form, vocabulary)).toHaveProperty("conditions");
    expect(ruleDefinition(form, rule, true).appliesWhen).toBe('{"any":[]}');
  });
  it.each(["", "NaN", "Infinity", "1e999", "2,5", "2 mg/dL"])("rejects a noncanonical threshold: %s", value => {
    const { form } = readRuleForm(syntheticRule()); form.conditions[0].value = value;
    expect(validateRuleForm(form, vocabulary)).toHaveProperty("condition-0-value");
  });
  it("accepts finite decimal and exponent thresholds and serializes them as numbers", () => {
    const { form } = readRuleForm(syntheticRule()); form.mode = "all"; form.conditions[0].value = " 1.5e1 ";
    expect(validateRuleForm(form, vocabulary)).toEqual({});
    expect(JSON.parse(ruleDefinition(form).appliesWhen)).toEqual({ all: [{ field: "creatinine", op: ">=", value: 15 }] });
  });
  it("rejects inherited vocabulary keys, duplicate classes, unsupported comparisons and incomplete sources", () => {
    const { form } = readRuleForm(syntheticRule());
    form.drugClasses = ["nsaid", "nsaid", "toString"];
    form.conditions[0] = { ...form.conditions[0], field: "constructor", op: "==" };
    form.sources[0] = { ...form.sources[0], title: " ", url: "https:example.org" };
    expect(Object.keys(validateRuleForm(form, vocabulary)).sort()).toEqual([
      "condition-0-field", "condition-0-op", "drugClasses", "source-0-title", "source-0-url",
    ]);
  });
  it("requires a complete new candidate and allows classless case-level conditions", () => {
    const empty = newRuleForm();
    expect(Object.keys(validateRuleForm(empty, vocabulary)).sort()).toEqual(["conditions", "drugClasses", "ruleId", "sources", "title"]);
    const { form } = readRuleForm(syntheticRule()); form.caseLevel = true; form.drugClasses = [];
    expect(validateRuleForm(form, vocabulary)).toEqual({});
  });
  it("rejects nonportable regex lexically but leaves actual compilation to the services", () => {
    const { form } = readRuleForm(syntheticRule()); form.linkTerms = "a++";
    expect(validateRuleForm(form, vocabulary)).toHaveProperty("linkTerms");
    form.linkTerms = "[z-a]"; // Lexically portable; the Python/.NET compilers reject the reversed range.
    expect(validateRuleForm(form, vocabulary)).toEqual({});
  });
});

describe("recognition serialization and current approval", () => {
  it("retains multiple classes, regional names and line spelling without hidden normalization", () => {
    const snapshot = catalogSnapshot(); const rows = catalogRows(snapshot.catalog);
    expect(catalogDefinition(rows)).toEqual(snapshot.catalog);
    rows[0].brands = "Synthetic Brand\n\nStraße\n";
    expect(catalogDefinition(rows).syntheticdrug.brands).toEqual(["Synthetic Brand", "Straße"]);
    expect(sameCatalogRows(rows, rows.map(row => ({ ...row, key: "changed" })))).toBe(true);
    expect(catalogDefinition([])).toEqual({});
  });
  it("rejects cross-drug folded collisions but permits aliases for the same generic", () => {
    const rows = catalogRows({ first: { classes: ["nsaid"], brands: ["Straße", "STRASSE"], regional_names: [] } });
    expect(validateCatalogRows(rows, vocabulary)).toEqual({});
    rows.push({ key: "second", generic: "second", classes: ["metformin"], brands: "strasse", regionalNames: "" });
    expect(validateCatalogRows(rows, vocabulary)).toHaveProperty("second-brands");
  });
  it("rejects duplicate/unknown classes, too many aliases and overlength names", () => {
    const rows = catalogRows(catalogSnapshot().catalog);
    rows[0].classes = ["nsaid", "nsaid", "constructor"]; rows[0].generic = "💊".repeat(65);
    rows[0].brands = Array.from({ length: 65 }, (_, index) => `brand-${index}`).join("\n");
    const errors = validateCatalogRows(rows, vocabulary);
    expect(errors).toHaveProperty("drug-0-classes"); expect(errors).toHaveProperty("drug-0-generic"); expect(errors).toHaveProperty("drug-0-brands");
  });
  it("binds medication review to the saved catalog but not case-level review", () => {
    const rule = syntheticRule({ status: "approved", reviewedBy: "clinician", reviewedAt: "2026-11-15T00:02:00Z", reviewedCatalogRevision: 2 });
    expect(currentRuleApproval(rule, 2)).toBe(true);
    expect(currentRuleApproval(rule, 3)).toBe(false);
    expect(currentRuleApproval(rule)).toBe(false);
    expect(currentRuleApproval({ ...rule, caseLevel: true })).toBe(true);
  });
  it.each([
    { reviewedBy: null }, { reviewedAt: null }, { reviewedAt: "invalid" }, { updatedAt: "invalid" },
    { updatedAt: undefined }, { reviewedAt: "2026-11-15T00:00:00Z" }, { status: "draft" as const },
  ])("fails closed on missing, invalid, stale or cleared review evidence: %j", override => {
    const rule = syntheticRule({ status: "approved", reviewedBy: "clinician", reviewedAt: "2026-11-15T00:02:00Z", reviewedCatalogRevision: 2, ...override });
    expect(currentRuleApproval(rule, 2)).toBe(false);
  });
});
