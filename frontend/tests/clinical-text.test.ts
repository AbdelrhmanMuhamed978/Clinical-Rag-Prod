import { describe, expect, it } from "vitest";
import vectors from "../../tests/fixtures/clinical-text-vectors.json";
import { normalizedDrugAlias, portableRegexProblem, validDrugName, validHttpSourceUrl } from "@/lib/clinical-text";
import { catalogRows, httpSourceUrl, validateCatalogRows } from "@/lib/rules";
import type { RuleVocabulary } from "@/lib/types";

const vocabulary: RuleVocabulary = { fields: {}, drug_classes: { nsaid: "NSAID", metformin: "Metformin" }, operators: [], statuses: [] };
describe("shared clinical text vectors", () => {
  it.each(vectors.aliases)("folds $input to $expected and rejects cross-drug collisions", vector => {
    expect(normalizedDrugAlias(vector.input)).toBe(vector.expected);
    const rows = catalogRows({ first: { classes: ["nsaid"], brands: [vector.input], regional_names: [] },
      second: { classes: ["metformin"], brands: [vector.expected], regional_names: [] } });
    expect(Object.values(validateCatalogRows(rows, vocabulary)).some(error => error.includes("two drugs"))).toBe(true);
  });
  it.each(vectors.names)("validates pinned name characters and UTF-16 length: $input / $valid", vector => {
    const input = (vector.input ?? String.fromCodePoint(...(vector.codepoints ?? []))).repeat(vector.repeat ?? 1);
    expect(validDrugName(input)).toBe(vector.valid);
    expect(Object.keys(validateCatalogRows(catalogRows({ [input]: { classes: ["nsaid"], brands: [], regional_names: [] } }), vocabulary)).length === 0).toBe(vector.valid);
  });
  it.each(vectors.urls)("validates unambiguous source syntax: $input / $valid", vector => {
    expect(validHttpSourceUrl(vector.input)).toBe(vector.valid);
    expect(httpSourceUrl(vector.input)).toBe(vector.valid ? vector.input : null);
  });
  it.each(vectors.regex)("inspects portable regex without executing it: $input / $valid", vector => {
    expect(portableRegexProblem(vector.input) === null).toBe(vector.valid);
  });
  it.each(vectors.regex_compilation)("leaves actual compilation authoritative without running browser regex: $input", vector => {
    expect(portableRegexProblem(vector.input)).toBeNull();
  });
});
