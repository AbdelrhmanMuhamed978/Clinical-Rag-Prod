import type { DrugCatalog, Rule, RuleVocabulary } from "./types";
import { hasText, normalizedDrugAlias, portableRegexProblem, validDrugName, validHttpSourceUrl } from "./clinical-text";

export type ConditionInput = { key: string; field: string; op: string; value: string };
export type SourceInput = { key: string; title: string; url: string; quote: string; original: Record<string, unknown> };
export type RuleForm = {
  ruleId: string; title: string; level: string; message: string; caveat: string; linkTerms: string; caseLevel: boolean;
  drugClasses: string[]; mode: "any" | "all"; conditions: ConditionInput[]; sources: SourceInput[];
};
export type RuleDefinitionInput = {
  ruleId: string; title: string; level: string; message: string; caveat: string; linkTerms: string; caseLevel: boolean;
  drugClasses: string; appliesWhen: string; sources: string; enabled: boolean;
};
export type RuleFormErrors = Record<string, string>;
export type CatalogRow = { key: string; generic: string; classes: string[]; brands: string; regionalNames: string };

const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === "string");
const text = (value: unknown) => typeof value === "string" ? value : "";

// Stable comparisons are for editor snapshots only, never clinical evaluation or user regex execution.
function ordered(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(ordered);
  if (object(value)) return Object.fromEntries(Object.keys(value).sort().map(key => [key, ordered(value[key])]));
  return value;
}
const same = (left: unknown, right: unknown) => JSON.stringify(ordered(left)) === JSON.stringify(ordered(right));

export function sourceObject(source: SourceInput): Record<string, unknown> {
  const value: Record<string, unknown> = { ...source.original, title: source.title, url: source.url };
  // Retain absent/null quotes and all unknown provenance fields when they were not edited.
  if (source.quote !== text(source.original.quote)) value.quote = source.quote;
  return value;
}

export function newRuleForm(): RuleForm {
  return { ruleId: "", title: "", level: "individual_review", message: "", caveat: "", linkTerms: "", caseLevel: false,
    drugClasses: [], mode: "any", conditions: [], sources: [] };
}

export function readRuleForm(rule: Rule): { form: RuleForm; problems: string[] } {
  const problems: string[] = [];
  function parse(raw: string | undefined, label: string): unknown {
    try { return JSON.parse(raw ?? "null"); }
    catch { problems.push(`${label} contains unreadable JSON.`); return null; }
  }
  const classes = parse(rule.drugClasses, "Drug classes");
  const group = parse(rule.appliesWhen, "Conditions");
  const sources = parse(rule.sources, "Sources");
  const mode = object(group) && Object.keys(group).length === 1 && Array.isArray(group.all) ? "all" : "any";
  const conditions = object(group) ? group[mode] : null;
  const shape = object(group) && Object.keys(group).length === 1 && Array.isArray(conditions)
    && conditions.every(row => object(row) && Object.keys(row).length === 3
      && typeof row.field === "string" && typeof row.op === "string" && typeof row.value === "number" && Number.isFinite(row.value));
  if (!strings(classes)) problems.push("Drug classes must be rebuilt as an array.");
  if (!shape) problems.push("Conditions cannot be represented without rebuilding the group.");
  if (!Array.isArray(sources) || !sources.every(object)) problems.push("Sources must be rebuilt as source rows.");
  const form: RuleForm = {
    ruleId: rule.ruleId, title: rule.title, level: rule.level ?? "", message: rule.message ?? "", caveat: rule.caveat ?? "",
    linkTerms: rule.linkTerms ?? "", caseLevel: rule.caseLevel, drugClasses: strings(classes) ? classes : [], mode,
    conditions: shape ? (conditions as { field: string; op: string; value: number }[]).map((row, index) => ({ ...row, key: `condition-${index}`, value: String(row.value) })) : [],
    sources: Array.isArray(sources) ? sources.filter(object).map((source, index) => ({ key: `source-${index}`,
      title: text(source.title), url: text(source.url), quote: text(source.quote), original: source })) : [],
  };
  return { form, problems };
}

export function sameRuleForm(left: RuleForm, right: RuleForm): boolean {
  function comparable(form: RuleForm) {
    return { ...form, conditions: form.conditions.map(({ field, op, value }) => ({ field, op, value })), sources: form.sources.map(sourceObject) };
  }
  return same(comparable(left), comparable(right));
}

function keepJson(raw: string | undefined, value: unknown, rebuild: boolean) {
  if (!rebuild && raw != null) {
    try { if (same(JSON.parse(raw), value)) return raw; } catch { /* Explicit repair will replace invalid JSON. */ }
  }
  return JSON.stringify(value);
}

export function ruleDefinition(form: RuleForm, source?: Rule | null, rebuild = false): RuleDefinitionInput {
  return { ruleId: form.ruleId, title: form.title, level: form.level, message: form.message, caveat: form.caveat,
    linkTerms: form.linkTerms, caseLevel: form.caseLevel, enabled: false,
    drugClasses: keepJson(source?.drugClasses, form.drugClasses, rebuild),
    appliesWhen: keepJson(source?.appliesWhen, { [form.mode]: form.conditions.map(({ field, op, value }) => ({ field, op, value: Number(value) })) }, rebuild),
    sources: keepJson(source?.sources, form.sources.map(sourceObject), rebuild) };
}

export function rulePatch(form: RuleForm, source: Rule, rebuild = false): Partial<Omit<RuleDefinitionInput, "enabled">> & { expectedRevision: number } {
  const definition = ruleDefinition(form, source, rebuild);
  const changed: Partial<Omit<RuleDefinitionInput, "enabled">> = {};
  for (const key of Object.keys(definition) as (keyof typeof definition)[]) {
    if (key === "enabled") continue; // Only lifecycle actions may change the saved active switch.
    const previous = source[key] ?? (key === "caseLevel" ? false : "");
    if (definition[key] !== previous) Object.assign(changed, { [key]: definition[key] });
  }
  return { ...changed, expectedRevision: source.revision };
}

export function httpSourceUrl(value: string): string | null {
  return validHttpSourceUrl(value) ? value : null; // No WHATWG repair or normalization of editor input.
}

export function validateRuleForm(form: RuleForm, vocabulary: RuleVocabulary): RuleFormErrors {
  const errors: RuleFormErrors = {};
  if (!/^[A-Za-z0-9_-]{1,64}$/.test(form.ruleId) || /[\r\n]/.test(form.ruleId)) errors.ruleId = "Rule ID must use 1–64 letters, digits, underscores or hyphens.";
  if (!hasText(form.title) || form.title.length > 256) errors.title = "A title of at most 256 characters is required.";
  for (const [key, limit] of [["level", 64], ["message", 10000], ["caveat", 10000], ["linkTerms", 512]] as const)
    if (form[key].length > limit) errors[key] = `${key} must be at most ${limit} characters.`;
  if ((!form.caseLevel && !form.drugClasses.length) || form.drugClasses.some(value => !Object.hasOwn(vocabulary.drug_classes, value))
    || new Set(form.drugClasses).size !== form.drugClasses.length) errors.drugClasses = "Choose distinct known drug classes; medication rules need at least one.";
  if (!form.conditions.length || form.conditions.length > 64) errors.conditions = "Provide 1–64 condition rows.";
  for (const row of form.conditions) {
    if (!Object.hasOwn(vocabulary.fields, row.field)) errors[`${row.key}-field`] = "Choose a supported clinical field.";
    if (!vocabulary.operators.includes(row.op)) errors[`${row.key}-op`] = "Choose <, <=, > or >=.";
    if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(row.value.trim()) || !Number.isFinite(Number(row.value)))
      errors[`${row.key}-value`] = "Enter a finite numeric threshold in the displayed canonical unit.";
  }
  if (!form.sources.length || form.sources.length > 32) errors.sources = "Provide at least one supporting source (up to 32).";
  for (const source of form.sources) {
    if (!hasText(source.title) || source.title.length > 512) errors[`${source.key}-title`] = "Each source needs a title of at most 512 characters.";
    if (!httpSourceUrl(source.url) || source.url.length > 2048) errors[`${source.key}-url`] = "Use an HTTP(S) source URL without embedded credentials.";
    if (source.quote.length > 10000) errors[`${source.key}-quote`] = "Supporting quotes must be at most 10000 characters.";
  }
  const regexProblem = portableRegexProblem(form.linkTerms);
  if (regexProblem) errors.linkTerms = regexProblem;
  // The server compiles regex in the portable Python/.NET subset. Never run an editor regex in JavaScript.
  return errors;
}

export function catalogRows(catalog: DrugCatalog): CatalogRow[] {
  return Object.entries(catalog).map(([generic, entry], index) => ({ key: `drug-${index}`, generic, classes: [...entry.classes],
    brands: entry.brands.join("\n"), regionalNames: entry.regional_names.join("\n") }));
}
const lines = (value: string) => value.split(/\r?\n/).filter(hasText);
export function catalogDefinition(rows: CatalogRow[]): DrugCatalog {
  return Object.fromEntries(rows.map(row => [row.generic, { classes: row.classes, brands: lines(row.brands), regional_names: lines(row.regionalNames) }]));
}
export function sameCatalogRows(left: CatalogRow[], right: CatalogRow[]): boolean {
  const comparable = (rows: CatalogRow[]) => rows.map(({ generic, classes, brands, regionalNames }) => ({ generic, classes, brands, regionalNames }));
  return same(comparable(left), comparable(right));
}
export function validateCatalogRows(rows: CatalogRow[], vocabulary: RuleVocabulary): RuleFormErrors {
  const errors: RuleFormErrors = {};
  const aliases = new Map<string, string>();
  if (rows.length > 512) errors.catalog = "Use at most 512 generic entries.";
  for (const row of rows) {
    if (!row.classes.length || row.classes.some(name => !Object.hasOwn(vocabulary.drug_classes, name)) || new Set(row.classes).size !== row.classes.length)
      errors[`${row.key}-classes`] = "Each drug needs distinct known classes, with at least one class.";
    for (const [kind, names] of [["generic", [row.generic]], ["brands", lines(row.brands)], ["regionalNames", lines(row.regionalNames)]] as const) {
      if (names.length > 64) errors[`${row.key}-${kind}`] = "Use up to 64 aliases of each kind.";
      for (const name of names) {
        if (!validDrugName(name)) errors[`${row.key}-${kind}`] = "Names need 1–128 UTF-16 units, without control, format, private-use or unassigned characters.";
        const normalized = normalizedDrugAlias(name);
        if (aliases.has(normalized) && aliases.get(normalized) !== row.key)
          errors[`${row.key}-${kind}`] = "A normalized generic, brand or regional name cannot identify two drugs.";
        aliases.set(normalized, row.key);
      }
    }
  }
  if (JSON.stringify(catalogDefinition(rows)).length > 200000) errors.catalog = "Drug recognition must be at most 200000 characters.";
  return errors;
}

export function ruleStatusLabel(rule: Rule): string {
  if (rule.status === "draft") return "DRAFT: pending clinician review";
  if (rule.status === "in_review") return "In review: pending clinician approval";
  return rule.status === "approved" ? "Clinician approved" : "Retired";
}

export function currentRuleApproval(rule: Rule, catalogRevision?: number): boolean {
  return rule.status === "approved" && !!rule.reviewedBy && !!rule.reviewedAt
    && !!rule.updatedAt && Number.isFinite(Date.parse(rule.reviewedAt)) && Date.parse(rule.reviewedAt) >= Date.parse(rule.updatedAt)
    && (rule.caseLevel || (catalogRevision != null && rule.reviewedCatalogRevision === catalogRevision));
}
