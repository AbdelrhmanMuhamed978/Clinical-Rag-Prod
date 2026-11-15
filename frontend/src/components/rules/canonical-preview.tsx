"use client";

import { useEffect, useId, useState } from "react";
import { api } from "@/lib/api";
import type { RulePreview, RuleVocabulary } from "@/lib/types";
import type { RuleDefinitionInput } from "@/lib/rules";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

type PreviewState = { fingerprint: string; result?: RulePreview; error?: string };

type Props = {
  workspaceId: string; definition: RuleDefinitionInput | null; vocabulary: RuleVocabulary;
  catalogRevision?: number; active: boolean; blockedReason?: string; userId?: string;
};
export function CanonicalPreview(props: Props) {
  return <PrivatePreview key={`${props.workspaceId}:${props.userId ?? ""}`} {...props} />;
}
function PrivatePreview({ workspaceId, definition, vocabulary, catalogRevision, active, blockedReason }: Props) {
  const id = useId();
  const [text, setText] = useState("");
  const [sample, setSample] = useState("");
  const [live, setLive] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<PreviewState | null>(null);
  const body = definition && catalogRevision != null && !blockedReason
    ? JSON.stringify({ rule: definition, text, regexSample: sample, expectedCatalogRevision: catalogRevision }) : null;
  const inputFingerprint = body == null ? null : `${workspaceId}:${body}`;
  const [binding, setBinding] = useState({ fingerprint: inputFingerprint, version: 0 });
  const [manualVersion, setManualVersion] = useState<number | null>(null);
  // A manual run is bound to one exact input version. Editing (even then undoing) never sends another request.
  if (binding.fingerprint !== inputFingerprint) setBinding({ fingerprint: inputFingerprint, version: binding.version + 1 });
  const requested = live || manualVersion === binding.version;
  // Inputs, bindings and results live only in this mounted editor, never in React Query or browser storage.
  const fingerprint = requested && inputFingerprint != null ? `${binding.version}:${attempt}:${inputFingerprint}` : null;
  useEffect(() => {
    if (!active || body == null || fingerprint == null) return;
    if (state?.fingerprint === fingerprint) return; // Do not resend a completed manual run when returning to this tab.
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void api<RulePreview>(`/api/workspaces/${workspaceId}/rules/preview`, {
        method: "POST", body, signal: controller.signal, cache: "no-store",
      }).then(result => {
        if (controller.signal.aborted) return;
        if (result.catalog_revision !== JSON.parse(body).expectedCatalogRevision)
          setState({ fingerprint, error: "Recognition changed. Refresh the saved catalog before previewing." });
        else setState({ fingerprint, result });
      }).catch(() => {
        if (!controller.signal.aborted) setState({ fingerprint, error: "Canonical preview could not be completed. Check the definition and refresh saved recognition; no approval or result was stored." });
      });
    }, 400);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [workspaceId, body, fingerprint, active, state?.fingerprint]);
  const current = active && fingerprint != null && state?.fingerprint === fingerprint ? state : null;
  const result = current?.result;

  return <section className="space-y-4 rounded-xl border bg-card p-5" aria-labelledby={`${id}-heading`}>
    <h3 id={`${id}-heading`} className="text-lg font-semibold">Canonical “would fire” preview</h3>
    <p id={`${id}-privacy`} className="text-sm text-muted-foreground">Use synthetic or de-identified text. Preview sends inputs by private POST to deterministic Python only: no retrieval, generation, approval, or stored case inputs. Closing this workspace clears the inputs.</p>
    <form method="post" action={`/api/workspaces/${workspaceId}/rules/preview`} aria-labelledby={`${id}-heading`} className="space-y-4"
      onSubmit={event => {
        event.preventDefault();
        if (!active || !definition || catalogRevision == null || blockedReason) return;
        setManualVersion(binding.version); setAttempt(value => value + 1);
      }}>
    <div className="space-y-2">
      <Label htmlFor={`${id}-case`}>Case text for preview</Label>
      <Textarea id={`${id}-case`} name="previewText" rows={4} value={text} maxLength={100000} autoComplete="off"
        aria-describedby={`${id}-privacy`} className="text-base!" onChange={event => setText(event.target.value)} />
    </div>
    <div className="space-y-2">
      <Label htmlFor={`${id}-sample`}>Regex sample text</Label>
      <Textarea id={`${id}-sample`} name="regexSample" rows={2} value={sample} maxLength={10000} autoComplete="off"
        className="text-base!" onChange={event => setSample(event.target.value)} />
    </div>
    <div className="flex flex-wrap items-center gap-3">
      <Button type="submit" variant="outline" className="min-h-12" disabled={!active || !definition || catalogRevision == null || !!blockedReason}>Run canonical preview</Button>
      <div className="flex min-h-12 items-center gap-2">
        <input id={`${id}-live`} name="livePreview" type="checkbox" className="size-5 accent-primary" checked={live}
          onChange={event => { setLive(event.target.checked); setManualVersion(null); }} />
        <label htmlFor={`${id}-live`}>Live preview (after a pause in typing)</label>
      </div>
      <Button type="button" variant="ghost" className="min-h-12" onClick={() => { setText(""); setSample(""); setState(null); setLive(false); setManualVersion(null); setAttempt(0); }}>Clear private preview</Button>
    </div>
    </form>
    <p className="text-sm text-muted-foreground">Preview simulates firing even for disabled drafts. It never activates a rule. Saved recognition revision: {catalogRevision ?? "unavailable"}.</p>
    {blockedReason && <p className="text-sm">Preview paused: {blockedReason}</p>}
    {active && fingerprint && !current && <p role="status" className="text-sm">Updating canonical preview… Previous results are hidden because the inputs or definition changed.</p>}
    {active && !live && manualVersion != null && manualVersion !== binding.version && <p role="status" className="text-sm">Preview inputs changed. Run canonical preview again; manual mode does not send while you type.</p>}
    {current?.error && <p role="alert" className="text-sm text-destructive">{current.error}</p>}
    {result && <div className="space-y-4" data-testid="canonical-rule-result">
      <p role="status" className="font-semibold">{result.would_fire ? "Would fire (simulation only)" : "Would not fire (simulation only)"}</p>
      <p className="text-sm">Regex sample: {result.regex.timed_out ? "timed out safely; no match" : result.regex.matched ? "matched" : "not matched"}. Matching is case-insensitive and bounded in Python, not JavaScript.</p>
      <ul className="list-disc space-y-1 ps-5 text-sm">{result.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
      <div className="space-y-2"><h4 className="font-medium">Canonical values and derived screens</h4>
        <dl className="grid gap-2 sm:grid-cols-2">
          {Object.entries({ ...result.values, ...result.derived }).filter(([field]) => Object.hasOwn(vocabulary.fields, field)).map(([field, value]) => <div key={field} className="rounded bg-muted p-3 text-sm">
            <dt>{vocabulary.fields[field].label}</dt><dd className="font-medium">{String(value)} {vocabulary.fields[field].unit}
              {result.unit_details[field]?.assumed ? " — unit assumed" : result.unit_details[field]?.converted ? ` — converted from ${result.unit_details[field].source_unit}` : ""}</dd>
          </div>)}
        </dl>
      </div>
      <div className="space-y-2"><h4 className="font-medium">Recognized medication names</h4>
        {!result.meds.length ? <p className="text-sm">No medication recognized in this saved catalog.</p> : <ul className="space-y-2 text-sm">
          {result.meds.map(med => <li key={med.generic} className="rounded border p-3"><span className="font-medium">{med.input} → {med.generic}</span>
            <span className="block">{med.classes.map(name => vocabulary.drug_classes[name] ?? name).join("; ")}</span>
            <span className="block">{med.verify ? "VERIFY: fuzzy name match; confirm medication identity and exposure." : "Exact name match; still confirm medication exposure."}</span>
          </li>)}
        </ul>}
      </div>
      {!!result.warnings.length && <div className="space-y-2"><h4 className="font-medium">Parsing warnings</h4><ul className="list-disc space-y-1 ps-5 text-sm">{result.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul></div>}
      <p className="text-sm text-muted-foreground">{result.notice} A creatinine cutoff is not an AKI diagnosis; low blood pressure or a shock index is not a shock diagnosis.</p>
    </div>}
  </section>;
}
