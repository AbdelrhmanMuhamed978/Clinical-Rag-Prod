"use client";

import { useId, useRef } from "react";
import type { RuleForm, RuleFormErrors } from "@/lib/rules";
import { httpSourceUrl } from "@/lib/rules";
import type { RuleVocabulary } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { ClassPicker } from "./class-picker";

const control = "min-h-12 w-full rounded-lg border bg-background px-3 text-base focus-visible:outline-2 focus-visible:outline-offset-2";

export function RuleFormFields({ form, errors, vocabulary, readOnly, onChange }: {
  form: RuleForm; errors: RuleFormErrors; vocabulary: RuleVocabulary; readOnly: boolean; onChange: (form: RuleForm) => void;
}) {
  const id = useId();
  const sequence = useRef(0);
  const nextKey = (kind: string) => `${id}-${kind}-${sequence.current++}`;
  const set = <Key extends keyof RuleForm>(key: Key, value: RuleForm[Key]) => onChange({ ...form, [key]: value });
  const invalid = (key: string) => !!errors[key] || undefined;
  const described = (key: string, help?: string) => [help, errors[key] ? `${id}-error-${key}` : undefined].filter(Boolean).join(" ") || undefined;
  const error = (key: string) => errors[key] ? <p id={`${id}-error-${key}`} className="text-sm text-destructive">{errors[key]}</p> : null;

  return <div className="space-y-6">
    <div className="space-y-2"><Label htmlFor={`${id}-key`}>Rule ID (required)</Label>
      <Input id={`${id}-key`} name="ruleId" required maxLength={64} pattern={String.raw`[A-Za-z0-9_\-]{1,64}`} value={form.ruleId}
        readOnly={readOnly} spellCheck={false} autoCapitalize="none" autoCorrect="off" autoComplete="off" className="min-h-12 text-base!"
        aria-invalid={invalid("ruleId")} aria-describedby={described("ruleId", `${id}-key-help`)} onChange={event => set("ruleId", event.target.value)} />
      <p id={`${id}-key-help`} className="text-sm text-muted-foreground">Use 1–64 letters, digits, underscores or hyphens.</p>{error("ruleId")}
    </div>
    <div className="space-y-2"><Label htmlFor={`${id}-title`}>Rule title (required)</Label>
      <Input id={`${id}-title`} name="title" required maxLength={256} value={form.title} readOnly={readOnly} className="min-h-12 text-base!"
        aria-invalid={invalid("title")} aria-describedby={described("title")} onChange={event => set("title", event.target.value)} />{error("title")}
    </div>
    <div className="space-y-2"><Label htmlFor={`${id}-level`}>Display level</Label>
      <Input id={`${id}-level`} name="level" maxLength={64} value={form.level} readOnly={readOnly} className="min-h-12 text-base!"
        aria-invalid={invalid("level")} onChange={event => set("level", event.target.value)} />{error("level")}
    </div>
    <div className="space-y-2"><Label htmlFor={`${id}-message`}>Screening message</Label>
      <Textarea id={`${id}-message`} name="message" rows={3} maxLength={10000} value={form.message} readOnly={readOnly} className="text-base!"
        aria-invalid={invalid("message")} onChange={event => set("message", event.target.value)} />{error("message")}
    </div>
    <div className="space-y-2"><Label htmlFor={`${id}-caveat`}>Clinical caveat and limitations</Label>
      <Textarea id={`${id}-caveat`} name="caveat" rows={3} maxLength={10000} value={form.caveat} readOnly={readOnly} className="text-base!"
        aria-invalid={invalid("caveat")} onChange={event => set("caveat", event.target.value)} />{error("caveat")}
    </div>
    <div className="flex min-h-12 items-center gap-3"><input id={`${id}-case`} name="caseLevel" type="checkbox" checked={form.caseLevel}
      disabled={readOnly} className="size-5 accent-primary" onChange={event => set("caseLevel", event.target.checked)} />
      <label htmlFor={`${id}-case`}>Case-level rule (does not require a recognized medication)</label>
    </div>
    <ClassPicker legend="Applicable drug classes" vocabulary={vocabulary} selected={form.drugClasses} disabled={readOnly}
      invalid={invalid("drugClasses")} onChange={classes => set("drugClasses", classes)} />{error("drugClasses")}
    <fieldset className="space-y-4" disabled={readOnly}>
      <legend className="mb-2 font-semibold">Clinical conditions (required)</legend>
      <p className="text-sm text-muted-foreground">Use the canonical units shown below. A screening threshold is not a diagnosis or an automatic instruction to stop medication.</p>
      <fieldset className="space-y-2"><legend className="font-medium">Combine condition rows</legend>
        {(["any", "all"] as const).map(mode => <div key={mode} className="flex min-h-12 items-center gap-3">
          <input id={`${id}-${mode}`} name="conditionMode" type="radio" className="size-5 accent-primary" value={mode}
            checked={form.mode === mode} onChange={() => set("mode", mode)} /><label htmlFor={`${id}-${mode}`}>{mode === "any" ? "Any row matches (OR)" : "All rows match (AND)"}</label>
        </div>)}
      </fieldset>
      {form.conditions.map((row, index) => <fieldset key={row.key} className="space-y-3 rounded-lg border p-4">
        <legend className="px-1 text-sm font-medium">Condition {index + 1}</legend>
        <div className="grid items-start gap-3 sm:grid-cols-[minmax(0,2fr)_minmax(5rem,1fr)_minmax(0,1fr)]">
          <div className="space-y-2"><Label htmlFor={`${id}-${row.key}-field`}>Field {index + 1}</Label>
            <select id={`${id}-${row.key}-field`} name="conditionField" className={control} value={row.field} required
              aria-invalid={invalid(`${row.key}-field`)} aria-describedby={described(`${row.key}-field`)}
              onChange={event => set("conditions", form.conditions.map(item => item.key === row.key ? { ...item, field: event.target.value } : item))}>
              {!Object.hasOwn(vocabulary.fields, row.field) && <option value={row.field}>Unsupported field: {row.field || "choose a field"}</option>}
              {Object.entries(vocabulary.fields).map(([value, detail]) => <option key={value} value={value}>{detail.label}</option>)}
            </select>{error(`${row.key}-field`)}
          </div>
          <div className="space-y-2"><Label htmlFor={`${id}-${row.key}-op`}>Comparison {index + 1}</Label>
            <select id={`${id}-${row.key}-op`} name="conditionOperator" className={control} value={row.op} required
              aria-invalid={invalid(`${row.key}-op`)} onChange={event => set("conditions", form.conditions.map(item => item.key === row.key ? { ...item, op: event.target.value } : item))}>
              {!vocabulary.operators.includes(row.op) && <option value={row.op}>Unsupported: {row.op}</option>}
              {vocabulary.operators.map(value => <option key={value} value={value}>{value}</option>)}
            </select>{error(`${row.key}-op`)}
          </div>
          <div className="space-y-2"><Label htmlFor={`${id}-${row.key}-value`}>Threshold {index + 1}</Label>
            <Input id={`${id}-${row.key}-value`} name="conditionValue" inputMode="decimal" required value={row.value}
              className="min-h-12 text-base!" aria-invalid={invalid(`${row.key}-value`)} aria-describedby={described(`${row.key}-value`, `${id}-${row.key}-unit`)}
              onChange={event => set("conditions", form.conditions.map(item => item.key === row.key ? { ...item, value: event.target.value } : item))} />
            <p id={`${id}-${row.key}-unit`} className="text-sm text-muted-foreground">{vocabulary.fields[row.field]?.unit ?? "Choose a supported field"}</p>{error(`${row.key}-value`)}
          </div>
        </div>
        <Button type="button" variant="outline" className="min-h-12" onClick={() => set("conditions", form.conditions.filter(item => item.key !== row.key))}>Remove condition {index + 1}</Button>
      </fieldset>)}
      {error("conditions")}
      <Button type="button" variant="outline" className="min-h-12" disabled={form.conditions.length >= 64}
        onClick={() => set("conditions", [...form.conditions, { key: nextKey("condition"), field: Object.keys(vocabulary.fields)[0], op: vocabulary.operators[0], value: "" }])}>Add condition</Button>
    </fieldset>
    <fieldset className="space-y-4" disabled={readOnly}>
      <legend className="mb-2 font-semibold">Supporting sources (at least one required)</legend>
      <p className="text-sm text-muted-foreground">Provide real public URLs and copy the supporting wording. Use a complete HTTP(S) URL with // and an ASCII host (punycode for international domains), no credentials or whitespace; encode spaces as %20. Source provenance and other saved metadata are retained when editing a row.</p>
      {form.sources.map((source, index) => <fieldset key={source.key} className="space-y-3 rounded-lg border p-4">
        <legend className="px-1 text-sm font-medium">Source {index + 1}</legend>
        {(["title", "url"] as const).map(key => <div key={key} className="space-y-2"><Label htmlFor={`${id}-${source.key}-${key}`}>Source {key === "title" ? "title" : "URL"} {index + 1} (required)</Label>
          <Input id={`${id}-${source.key}-${key}`} name={`source${key}`} type={key === "url" ? "url" : "text"} required maxLength={key === "url" ? 2048 : 512}
            value={source[key]} className="min-h-12 text-base!" spellCheck={key !== "url"} autoCapitalize={key === "url" ? "none" : undefined}
            autoCorrect={key === "url" ? "off" : undefined} aria-invalid={invalid(`${source.key}-${key}`)} aria-describedby={described(`${source.key}-${key}`)}
            onChange={event => set("sources", form.sources.map(item => item.key === source.key ? { ...item, [key]: event.target.value } : item))} />{error(`${source.key}-${key}`)}
        </div>)}
        <div className="space-y-2"><Label htmlFor={`${id}-${source.key}-quote`}>Supporting quote {index + 1}</Label>
          <Textarea id={`${id}-${source.key}-quote`} name="sourceQuote" value={source.quote} maxLength={10000} rows={3} className="text-base!"
            aria-invalid={invalid(`${source.key}-quote`)} onChange={event => set("sources", form.sources.map(item => item.key === source.key ? { ...item, quote: event.target.value } : item))} />{error(`${source.key}-quote`)}
        </div>
        {httpSourceUrl(source.url) && <a className="inline-block min-h-12 py-3 text-sm underline" href={httpSourceUrl(source.url)!} target="_blank" rel="noopener noreferrer" referrerPolicy="no-referrer">Open public source {index + 1}</a>}
        <details className="text-sm"><summary className="cursor-pointer">Saved source metadata {index + 1}</summary><pre className="mt-2 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3">{JSON.stringify(source.original, null, 2)}</pre></details>
        <Button type="button" variant="outline" className="min-h-12" onClick={() => set("sources", form.sources.filter(item => item.key !== source.key))}>Remove source {index + 1}</Button>
      </fieldset>)}
      {error("sources")}
      <Button type="button" variant="outline" className="min-h-12" disabled={form.sources.length >= 32}
        onClick={() => set("sources", [...form.sources, { key: nextKey("source"), title: "", url: "", quote: "", original: {} }])}>Add source</Button>
    </fieldset>
    <div className="space-y-2"><Label htmlFor={`${id}-regex`}>Link terms (regular expression)</Label>
      <p id={`${id}-regex-help`} className="text-sm text-muted-foreground">Portable case-insensitive Python/.NET regex, up to 512 UTF-16 units; repeat bounds up to 10000. Use | for alternatives and escape literal brackets/braces. Lookarounds, inline flags, backreferences, possessive quantifiers and nested character classes are unsupported; matching is tested in the bounded Python preview.</p>
      <Input id={`${id}-regex`} name="linkTerms" value={form.linkTerms} maxLength={512} readOnly={readOnly} spellCheck={false}
        autoCapitalize="none" autoCorrect="off" className="min-h-12 text-base!" aria-invalid={invalid("linkTerms")}
        aria-describedby={described("linkTerms", `${id}-regex-help`)} onChange={event => set("linkTerms", event.target.value)} />{error("linkTerms")}
    </div>
  </div>;
}
