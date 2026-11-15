"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { patch } from "@/lib/api";
import { catalogDefinition, catalogRows, sameCatalogRows, validateCatalogRows, type CatalogRow, type RuleFormErrors } from "@/lib/rules";
import { configActionErrors } from "@/lib/workflow-config";
import type { DrugCatalogRepairSnapshot, DrugCatalogSnapshot, RuleVocabulary } from "@/lib/types";
import { useUnsavedChanges } from "@/components/navigation-guard";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { ClassPicker } from "./class-picker";
import { ConfirmDialog, type Confirmation } from "./confirm-action";

type Editor = { revision: number; rows: CatalogRow[]; saved: CatalogRow[]; repairing: boolean };
type Submission = { expectedRevision: number; rows: CatalogRow[] };
const editorFor = (snapshot: DrugCatalogSnapshot): Editor => { const rows = catalogRows(snapshot.catalog); return { revision: snapshot.revision, rows, saved: rows, repairing: false }; };

type Props = {
  workspaceId: string; userId: string; vocabulary: RuleVocabulary; snapshot?: DrugCatalogSnapshot;
  canEdit: boolean; loading: boolean; failed: boolean; onRefresh: () => void; repair?: DrugCatalogRepairSnapshot;
  repairLoading?: boolean; repairFailed?: boolean; invalid?: boolean;
};
export function DrugCatalogEditor(props: Props) {
  return <CatalogWorkspace key={`${props.workspaceId}:${props.userId}`} {...props} />;
}
function CatalogWorkspace({ workspaceId, userId, vocabulary, snapshot, canEdit, loading, failed, onRefresh, repair, repairLoading, repairFailed, invalid }: Props) {
  const id = useId();
  const qc = useQueryClient();
  const sequence = useRef(0);
  const mounted = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [errors, setErrors] = useState<RuleFormErrors>({});
  const [notice, setNotice] = useState("");
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const dirty = !!editor && (editor.repairing || !sameCatalogRows(editor.rows, editor.saved));
  useUnsavedChanges(dirty);
  const save = useMutation({
    mutationFn: (submission: Submission) => patch<DrugCatalogSnapshot>(`/api/workspaces/${workspaceId}/drugs`,
      { catalog: catalogDefinition(submission.rows), expectedRevision: submission.expectedRevision }),
    onSuccess: (saved, submission) => {
      if (!mounted.current) return;
      setEditor(current => current && current.revision === submission.expectedRevision
        ? { revision: saved.revision, rows: sameCatalogRows(current.rows, submission.rows) ? submission.rows : current.rows, saved: submission.rows, repairing: false } : current);
      qc.setQueryData(["drugs", workspaceId, userId], saved);
      void qc.invalidateQueries({ queryKey: ["rules", workspaceId, userId] });
      setErrors({}); setNotice(saved.revision === submission.expectedRevision ? "No recognition definition changes; existing reviews are preserved."
        : `Saved recognition revision ${saved.revision}. Non-retired medication rules now require clinician review.`);
    },
  });
  const messages = [...Object.values(errors), ...(save.error ? configActionErrors(save.error, "recognition") : [])];
  useEffect(() => { if (Object.keys(errors).length || save.error) errorSummary.current?.focus(); }, [errors, save.error]);
  if (snapshot && !failed && (!editor || (!dirty && !save.isPending && snapshot.revision > editor.revision))) setEditor(editorFor(snapshot));
  const stale = !!editor && (editor.repairing ? !failed || !repair || repairFailed || repair.revision !== editor.revision
    : failed || !snapshot || snapshot.revision !== editor.revision);
  const selected = editor?.rows.find(row => row.key === selectedKey) ?? editor?.rows[0];
  function update(row: CatalogRow) {
    setEditor(current => current ? { ...current, rows: current.rows.map(item => item.key === row.key ? row : item) } : current);
    setErrors({}); setNotice(""); if (save.isError) save.reset();
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editor || !canEdit || stale || save.isPending) return;
    if (!dirty) { setNotice("No recognition changes to save. Existing reviews are preserved."); return; }
    const validation = validateCatalogRows(editor.rows, vocabulary);
    setErrors(validation);
    if (Object.keys(validation).length) return;
    const submission: Submission = { expectedRevision: editor.revision,
      rows: editor.rows.map(row => ({ ...row, classes: [...row.classes] })) };
    setConfirmation({ title: "Save changed medication recognition?", confirmLabel: "Save recognition changes",
      description: `This replaces saved recognition revision ${submission.expectedRevision} for this workspace. All non-retired medication rules will become drafts and lose current approval; case-level rules and other workspaces are unchanged.`,
      onConfirm: () => save.mutate(submission) });
  }
  function reload() {
    if (save.isPending || (dirty && !window.confirm("Discard unsaved medication recognition changes and reload the saved catalog?"))) return;
    if (snapshot && !failed) { setEditor(editorFor(snapshot)); setSelectedKey(null); setErrors({}); save.reset(); }
    onRefresh(); setNotice("Reloading saved recognition.");
  }
  if (loading && !editor) return <p>Loading saved medication recognition…</p>;
  const repairPanel = failed && invalid && <section className="space-y-3 rounded-lg border p-4" aria-labelledby={`${id}-repair-heading`}>
    <h3 id={`${id}-repair-heading`} className="font-semibold">Invalid stored recognition — explicit repair required</h3>
    <p className="text-sm">Analysis and preview remain blocked. No default or empty catalog has been substituted.</p>
    {repairLoading && <p role="status">Loading authorized repair evidence and its exact saved revision…</p>}
    {repairFailed && <p role="alert">Repair evidence could not be refreshed. Do not reuse an old revision; retry recognition.</p>}
    {!canEdit && <p className="text-sm">An editor or owner must load the revision-bound evidence and rebuild recognition.</p>}
    {canEdit && repair && <>
      <p className="text-sm">Invalid stored revision {repair.revision} · Last changed by {repair.updatedBy ?? "unknown"} at {repair.updatedAt ?? "unknown"}.</p>
      <ul className="list-disc ps-5 text-sm">{repair.errors.map(error => <li key={error}>{error}</li>)}</ul>
      <details><summary className="cursor-pointer text-sm">Stored catalog evidence (never executed)</summary><pre className="mt-2 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">{repair.storedCatalogJson}</pre></details>
      <Button type="button" variant="outline" className="min-h-12" disabled={save.isPending || !!repairFailed || editor?.repairing && editor.revision === repair.revision}
        onClick={() => {
          const evidence = repair;
          setConfirmation({ title: `Rebuild invalid recognition revision ${evidence.revision}?`, confirmLabel: "Start catalog repair",
            description: "Start an empty structured editor explicitly, discarding any unsaved catalog edits. Nothing is saved yet. Add and verify the generics, classes and aliases, then confirm the replacement. Saving an empty replacement explicitly disables medication recognition. The corrupt evidence is retained in the save audit.",
            onConfirm: () => { setEditor({ revision: evidence.revision, rows: [], saved: [], repairing: true }); setSelectedKey(null); setErrors({}); save.reset(); setNotice("Explicit catalog repair started; no recognition has been saved or substituted."); } });
        }}>Rebuild invalid catalog</Button>
    </>}
    <Button type="button" variant="outline" className="min-h-12" disabled={save.isPending} onClick={onRefresh}>Retry recognition</Button>
  </section>;
  if (!editor) return <section className="space-y-3"><h2 className="text-lg font-semibold">Drug recognition</h2>
    {repairPanel || <><p role="alert">Saved recognition could not be loaded. No default or empty catalog has been substituted.</p>
      <Button type="button" variant="outline" className="min-h-12" onClick={onRefresh}>Retry recognition</Button></>}
    <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} /></section>;

  return <section className="space-y-5" aria-labelledby={`${id}-heading`}>
    <header className="flex flex-wrap items-center justify-between gap-3"><h2 id={`${id}-heading`} className="text-xl font-semibold">Drug recognition</h2>
      <Button type="button" variant="outline" className="min-h-12" disabled={save.isPending} onClick={reload}>Reload saved recognition</Button>
    </header>
    {repairPanel}
    <p className="text-sm">{editor.repairing ? "Repairing invalid stored revision" : "Saved revision"} {editor.revision}{!editor.repairing && snapshot?.isDefault ? " — embedded defaults, not a workspace override" : " — workspace override"}. Generics, multiple classes, brands and regional names are data-driven. Exact names do not prove exposure; fuzzy matches always require VERIFY.</p>
    <p className="text-sm text-muted-foreground">Preview always uses the saved catalog, never unsaved edits. Saving an empty catalog explicitly disables medication recognition.</p>
    {dirty && <p className="font-medium">Unsaved recognition changes</p>}
    {stale && <p role="alert">Saved recognition changed or is unavailable. Your edits are kept; reload saved recognition before saving.</p>}
    {!canEdit && <p className="text-sm text-muted-foreground">An editor or owner is required to change recognition. Reviewer permission does not permit editing.</p>}
    <form method="post" action={`/api/workspaces/${workspaceId}/drugs`} aria-labelledby={`${id}-heading`} noValidate onSubmit={submit} className="space-y-5">
      <div className="grid items-start gap-5 lg:grid-cols-[minmax(12rem,1fr)_minmax(0,3fr)]">
        <div className="space-y-3"><h3 className="font-medium">Generics ({editor.rows.length})</h3>
          <ul className="max-h-96 space-y-1 overflow-auto rounded-lg border p-2">
            {editor.rows.map(row => <li key={row.key}><Button type="button" variant="ghost" className="min-h-12 w-full justify-start whitespace-normal text-start"
              aria-pressed={selected?.key === row.key} onClick={() => setSelectedKey(row.key)}>{row.generic || "New generic (name required)"}</Button></li>)}
          </ul>
          <Button type="button" variant="outline" className="min-h-12" disabled={!canEdit || stale || editor.rows.length >= 512} onClick={() => {
            const row: CatalogRow = { key: `${id}-new-${sequence.current++}`, generic: "", classes: [], brands: "", regionalNames: "" };
            setEditor({ ...editor, rows: [...editor.rows, row] }); setSelectedKey(row.key); setNotice(""); setErrors({});
          }}>Add generic</Button>
        </div>
        {selected ? <div className="min-w-0 space-y-5 rounded-xl border bg-card p-5">
          <div className="space-y-2"><Label htmlFor={`${id}-generic`}>Generic medication name (required)</Label>
            <Input id={`${id}-generic`} name="genericName" required maxLength={128} value={selected.generic} readOnly={!canEdit}
              spellCheck={false} autoCapitalize="none" autoCorrect="off" className="min-h-12 text-base!" aria-invalid={!!errors[`${selected.key}-generic`] || undefined}
              onChange={event => update({ ...selected, generic: event.target.value })} />
          </div>
          <ClassPicker legend="Medication classes (at least one required)" vocabulary={vocabulary} selected={selected.classes} disabled={!canEdit}
            invalid={!!errors[`${selected.key}-classes`] || undefined} onChange={classes => update({ ...selected, classes })} />
          {(["brands", "regionalNames"] as const).map(kind => <div key={kind} className="space-y-2"><Label htmlFor={`${id}-${kind}`}>{kind === "brands" ? "Brand names" : "Regional names"} (one per line)</Label>
            <Textarea id={`${id}-${kind}`} name={kind} rows={4} value={selected[kind]} readOnly={!canEdit} maxLength={64 * 129}
              spellCheck={false} autoCapitalize="none" autoCorrect="off" className="text-base!" aria-invalid={!!errors[`${selected.key}-${kind}`] || undefined}
              onChange={event => update({ ...selected, [kind]: event.target.value })} />
          </div>)}
          <Button type="button" variant="outline" className="min-h-12" disabled={!canEdit || stale || save.isPending}
            onClick={() => setConfirmation({ title: "Remove this generic from the editor?", confirmLabel: "Remove generic entry",
              description: `Remove ${selected.generic || "the unnamed generic"} and its aliases from unsaved recognition? The saved catalog is unchanged until you save.`,
              onConfirm: () => {
                setEditor(current => current ? { ...current, rows: current.rows.filter(row => row.key !== selected.key) } : current);
                setSelectedKey(null); setErrors({}); setNotice("");
              } })}>Remove generic</Button>
        </div> : <p className="text-sm">This catalog is empty. Saving it explicitly disables medication name recognition.</p>}
      </div>
      {!!messages.length && <div ref={errorSummary} tabIndex={-1} role="alert" className="space-y-2 rounded border border-destructive p-4">
        <p className="font-medium">Recognition changes could not be saved.</p><ul className="list-disc ps-5 text-sm">{messages.map(message => <li key={message}>{message}</li>)}</ul>
      </div>}
      <Button type="submit" className="min-h-12" disabled={!canEdit || stale || save.isPending}>Save drug catalog</Button>
    </form>
    <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} />
    <p role="status" className="text-sm text-muted-foreground">{notice}</p>
  </section>;
}
