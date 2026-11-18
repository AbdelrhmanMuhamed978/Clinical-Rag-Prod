import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { FIELD_LABELS, PROMPT_RULES, RETRIEVAL_KEYS, RETRIEVAL_RULES, isJsonObject,
  type JsonObject, type WorkflowErrors, type WorkflowField, type WorkflowFields } from "@/lib/workflow-config";
import vocabulary from "../../../../python-backend/clinical-vocabulary.json";

type Props = { step: number; fields: WorkflowFields; errors: WorkflowErrors; base: JsonObject; readOnly: boolean;
  onChange: (key: WorkflowField, value: string) => void };

// Input fields from facts.py; derived scores are not assumptions about parsed input.
const PARSER_FIELDS = ["sbp", "dbp", "map", "hr", "rr", "spo2", "temp", "hb", "wbc", "platelets",
  "creatinine", "potassium", "magnesium", "calcium", "albumin", "alt", "ast", "bilirubin", "age"] as const;

export function StepSettings({ step, fields, errors, base, readOnly, onChange }: Props) {
  function prompt(key: "plan" | "answer" | "rewrite", rows: number) {
    const id = `workflow-${key}`;
    return <div className="space-y-2">
      <Label htmlFor={id}>{FIELD_LABELS[key]}</Label>
      <p id={`${id}-help`} className="text-sm text-muted-foreground">
        Required variables: {PROMPT_RULES[key]["x-requiredPlaceholders"].map(token => `{${token}}`).join(" ")}. JSON braces and other literal tokens are kept.
      </p>
      <Textarea id={id} name={`prompts.${key}`} value={fields[key]} rows={rows} required
        maxLength={PROMPT_RULES[key].maxLength * 2} readOnly={readOnly}
        aria-invalid={!!errors[key]} aria-describedby={`${id}-help${errors[key] ? ` ${id}-error` : ""}`}
        className="resize-y text-base! focus-visible:outline-2 focus-visible:outline-offset-2"
        onChange={event => onChange(key, event.target.value)} />
      {errors[key] && <p id={`${id}-error`} className="text-sm text-destructive">{errors[key]}</p>}
    </div>;
  }

  if (step === 1) return <fieldset className="space-y-4">
    <legend className="font-medium">Read-only unit assumptions</legend>
    <p className="text-sm text-muted-foreground">Values without explicit units use these canonical units. These deterministic parser assumptions are not configurable in a workflow draft.</p>
    <dl className="grid gap-3 text-sm sm:grid-cols-2">
      {PARSER_FIELDS.map(key => <div key={key}>
        <dt className="font-medium">{vocabulary.fields[key].label}</dt><dd>{vocabulary.fields[key].unit}</dd>
      </div>)}
    </dl>
    <p className="text-sm text-muted-foreground">Unitless WBC and platelet counts ≥1000 are interpreted as /µL; ×10⁹/L is equivalent to ×10³/µL. Confirm original units and medication exposure.</p>
    <p className="text-sm text-muted-foreground">Supported explicit units are converted; unsupported explicit units are excluded rather than guessed.</p>
  </fieldset>;
  if (step === 2) return prompt("plan", 8);
  if (step === 3) return <div className="space-y-6">
    <fieldset className="space-y-4">
      <legend className="font-medium">Retrieval settings</legend>
      <div className="grid gap-5 sm:grid-cols-2">
        {RETRIEVAL_KEYS.map(key => {
          const rule = RETRIEVAL_RULES[key];
          const id = `workflow-${key}`;
          return <div key={key} className="space-y-2">
            <Label htmlFor={id}>{FIELD_LABELS[key]}</Label>
            <p id={`${id}-help`} className="text-sm text-muted-foreground">{rule.minimum}–{rule.maximum}{rule.type === "integer" ? ", whole numbers" : ""}</p>
            <Input id={id} name={`retrieval.${key}`} type="number" required min={rule.minimum} max={rule.maximum}
              step={rule.type === "integer" ? 1 : "any"} inputMode={rule.type === "integer" ? "numeric" : "decimal"}
              value={fields[key]} readOnly={readOnly} aria-invalid={!!errors[key]}
              aria-describedby={`${id}-help${errors[key] ? ` ${id}-error` : ""}`}
              className="min-h-12 text-base! focus-visible:outline-2 focus-visible:outline-offset-2"
              onChange={event => onChange(key, event.target.value)} />
            {errors[key] && <p id={`${id}-error`} className="text-sm text-destructive">{errors[key]}</p>}
          </div>;
        })}
      </div>
    </fieldset>
    {prompt("rewrite", 5)}
  </div>;
  if (step === 4) return prompt("answer", 10);
  if (step === 5) return <fieldset className="space-y-4">
    <legend className="font-medium">Locked verifier checks</legend>
    <p className="text-sm">The verifier is mandatory and cannot be disabled by a draft.</p>
    <ul className="list-disc space-y-2 ps-5 text-sm">
      <li>Recommendations must be supported by a matched source quote and citation.</li>
      <li>Minimum quote match: {isJsonObject(base.safety) && typeof base.safety.minQuoteMatch === "number" ? base.safety.minQuoteMatch : 0.88} (never below 0.88).</li>
      <li>Numbers must appear in the cited source.</li>
      <li>Recommendation strength, scope, conditions and negation must agree with the quote.</li>
      <li>The cited source must mention the question topic.</li>
      <li>Truncated generations are rejected rather than treated as verified output.</li>
      <li>Unsupported recommendations are excluded from verified summaries and conversation memory.</li>
      <li>Patient flags remain local; only currently reviewed, approved and enabled workspace rules participate in verified analysis.</li>
    </ul>
  </fieldset>;
  return null;
}
