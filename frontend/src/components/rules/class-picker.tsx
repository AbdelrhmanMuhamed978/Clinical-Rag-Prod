"use client";

import { useId } from "react";
import type { RuleVocabulary } from "@/lib/types";

export function ClassPicker({ legend, vocabulary, selected, disabled, invalid, onChange }: {
  legend: string; vocabulary: RuleVocabulary; selected: string[]; disabled: boolean; invalid?: boolean;
  onChange: (classes: string[]) => void;
}) {
  const id = useId();
  const choices = { ...Object.fromEntries(selected.filter(value => !Object.hasOwn(vocabulary.drug_classes, value)).map(value => [value, `Unsupported class: ${value}`])), ...vocabulary.drug_classes };
  return <fieldset className="space-y-2" disabled={disabled} aria-invalid={invalid || undefined}>
    <legend className="mb-2 font-medium">{legend}</legend>
    <div className="grid gap-1 sm:grid-cols-2">
      {Object.entries(choices).map(([value, label], index) => <div key={value} className="flex min-h-12 items-center gap-3 rounded border px-3">
        <input id={`${id}-${index}`} name="drugClasses" type="checkbox" className="size-5 shrink-0 accent-primary" value={value}
          checked={selected.includes(value)} onChange={event => onChange(event.target.checked ? [...selected, value] : selected.filter(item => item !== value))} />
        <label htmlFor={`${id}-${index}`} className="flex min-h-12 flex-1 cursor-pointer items-center text-sm">{label}</label>
      </div>)}
    </div>
  </fieldset>;
}
