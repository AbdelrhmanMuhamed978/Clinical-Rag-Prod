"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useParams } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, del, get, patch, post } from "@/lib/api";
import type { DrugCatalogRepairSnapshot, DrugCatalogSnapshot, Rule, RuleAccess, RuleVocabulary } from "@/lib/types";
import { currentRuleApproval, newRuleForm, readRuleForm, ruleDefinition, rulePatch, ruleStatusLabel, sameRuleForm,
  validateRuleForm, type RuleDefinitionInput, type RuleForm, type RuleFormErrors } from "@/lib/rules";
import { configActionErrors } from "@/lib/workflow-config";
import { useAuth } from "@/providers/AuthProvider";
import { useUnsavedChanges } from "@/components/navigation-guard";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { CanonicalPreview } from "@/components/rules/canonical-preview";
import { DrugCatalogEditor } from "@/components/rules/drug-catalog-editor";
import { RuleFormFields } from "@/components/rules/rule-form-fields";
import { ConfirmAction } from "@/components/rules/confirm-action";
import { RuleAudit } from "@/components/rules/rule-audit";

type Editor = { source: Rule | null; form: RuleForm; saved: RuleForm; problems: string[]; rebuilding: boolean };
type Submission = { id: string | null; form: RuleForm; payload: RuleDefinitionInput | ReturnType<typeof rulePatch> };
type RuleAction = { id: string; expectedRevision: number; action: "submit" | "approve" | "retire" | "reopen" | "enablement"; enabled?: boolean };
const editorFor = (rule: Rule): Editor => { const { form, problems } = readRuleForm(rule); return { source: rule, form, saved: form, problems, rebuilding: false }; };

export default function RulesPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const { user } = useAuth();
  return <RulesWorkspace key={`${workspaceId}:${user?.uid}`} workspaceId={workspaceId} userId={user?.uid ?? ""} />;
}

function RulesWorkspace({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const qc = useQueryClient();
  const root = `/api/workspaces/${workspaceId}/rules`;
  const queryKey = ["rules", workspaceId, userId];
  const query = useQuery<Rule[]>({ queryKey, enabled: !!userId, queryFn: ({ signal }) => get(root, { signal }) });
  const access = useQuery<RuleAccess>({ queryKey: [...queryKey, "access"], enabled: !!userId, queryFn: ({ signal }) => get(`${root}/access`, { signal }) });
  const vocabulary = useQuery<RuleVocabulary>({ queryKey: [...queryKey, "vocabulary"], enabled: !!userId, queryFn: ({ signal }) => get(`${root}/vocabulary`, { signal }) });
  const catalog = useQuery<DrugCatalogSnapshot>({ queryKey: ["drugs", workspaceId, userId], enabled: !!userId,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/drugs`, { signal }) });
  const [editor, setEditor] = useState<Editor | null>(null);
  const [tab, setTab] = useState<"rules" | "drugs" | "audit">("rules");
  const [search, setSearch] = useState("");
  const [auditRuleId, setAuditRuleId] = useState<string | null>(null);
  const [errors, setErrors] = useState<RuleFormErrors>({});
  const [notice, setNotice] = useState("");
  const mounted = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const dirty = !!editor && (!sameRuleForm(editor.form, editor.saved) || editor.rebuilding);
  useUnsavedChanges(dirty);
  const permission = access.isError ? undefined : access.data;
  const canEdit = permission?.canEdit === true;
  const canReview = permission?.canReview === true;
  const canDelete = permission?.canDelete === true;
  const invalidCatalog = catalog.error instanceof ApiError && catalog.error.status === 409;
  const catalogRepair = useQuery<DrugCatalogRepairSnapshot>({ queryKey: ["drugs", workspaceId, userId, "repair"],
    enabled: !!userId && canEdit && invalidCatalog,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/drugs/repair`, { signal }) });
  const rules = query.data?.filter(rule => rule.workspaceId === workspaceId) ?? [];
  const fresh = rules.find(rule => rule.id === editor?.source?.id);
  const stale = !!editor?.source && (query.isError || !fresh || fresh.revision !== editor.source.revision);
  const repairNeeded = !!editor?.problems.length && !editor.rebuilding;
  function refresh() { void qc.invalidateQueries({ queryKey }); }
  function remember(rule: Rule, submitted?: Submission) {
    if (!mounted.current || rule.workspaceId !== workspaceId) return;
    setEditor(current => {
      const originalId = submitted ? submitted.id : rule.id;
      if (!current || (current.source?.id ?? null) !== originalId) return current;
      const next = editorFor(rule);
      return submitted && !sameRuleForm(current.form, submitted.form) ? { ...next, form: current.form } : next;
    });
    qc.setQueryData<Rule[]>(queryKey, previous => previous ? [rule, ...previous.filter(item => item.id !== rule.id)].sort((a, b) => a.title.localeCompare(b.title)) : [rule]);
    refresh();
  }
  const save = useMutation({ mutationFn: (submission: Submission) => submission.id
    ? patch<Rule>(`${root}/${submission.id}`, submission.payload) : post<Rule>(root, submission.payload),
    onSuccess: (rule, submitted) => {
      remember(rule, submitted);
      if (mounted.current) { setErrors({}); setNotice(rule.status === "approved" ? "No definition changes; existing review is preserved." : `Saved draft revision ${rule.revision}. Clinician review is required before analysis.`); }
    },
  });
  const transition = useMutation({ mutationFn: ({ id, action, expectedRevision, enabled }: RuleAction) => post<Rule>(`${root}/${id}/${action}`,
    action === "enablement" ? { expectedRevision, enabled } : { expectedRevision }),
    onSuccess: rule => { remember(rule); if (mounted.current) setNotice(`${ruleStatusLabel(rule)}. ${rule.enabled ? "Enabled" : "Disabled"}. Only current, approved and enabled saved rules run.`); },
  });
  const remove = useMutation({ mutationFn: ({ id, expectedRevision }: { id: string; expectedRevision: number }) => del(`${root}/${id}?expectedRevision=${expectedRevision}`),
    onSuccess: (_, submitted) => {
      if (!mounted.current) return;
      qc.setQueryData<Rule[]>(queryKey, previous => previous?.filter(rule => rule.id !== submitted.id));
      setEditor(null); setAuditRuleId(submitted.id); setTab("audit"); setNotice("Rule deleted. Its audit history is retained."); refresh();
    },
  });
  const importDefaults = useMutation({ mutationFn: () => post<{ imported: number; skipped: number; rules: Rule[] }>(`${root}/import-default`, {}),
    onSuccess: result => {
      if (!mounted.current) return;
      qc.setQueryData<Rule[]>(queryKey, previous => [...(previous ?? []), ...result.rules].sort((a, b) => a.title.localeCompare(b.title)));
      setNotice(`Imported ${result.imported} disabled DRAFT candidates; skipped ${result.skipped} existing IDs. Nothing was approved or activated.`); refresh();
    },
  });
  const lifecyclePending = transition.isPending || remove.isPending || importDefaults.isPending;
  const busy = save.isPending || lifecyclePending;
  const mutationError = save.error || transition.error || remove.error || importDefaults.error;
  const messages = [...Object.values(errors), ...(mutationError ? configActionErrors(mutationError, "rule") : [])];
  useEffect(() => { if ((Object.keys(errors).length || mutationError) && tab === "rules") errorSummary.current?.focus(); }, [errors, mutationError, tab]);
  if (!editor && rules.length) setEditor(editorFor(rules[0]));
  else if (editor?.source && fresh && !dirty && !busy && fresh.revision > editor.source.revision) setEditor(editorFor(fresh));

  function clearErrors() { setErrors({}); save.reset(); transition.reset(); remove.reset(); importDefaults.reset(); setNotice(""); }
  function selectRule(rule?: Rule) {
    if (busy || (dirty && !window.confirm("Discard unsaved rule definition changes and load another rule?"))) return;
    if (rule) setEditor(editorFor(rule));
    else { const form = newRuleForm(); setEditor({ source: null, form, saved: form, problems: [], rebuilding: false }); }
    clearErrors();
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editor || !canEdit || stale || repairNeeded || busy || editor.source?.status === "retired" || !vocabulary.data || vocabulary.isError) return;
    if (editor.source && !dirty) { setNotice("No definition changes to save. Existing review is preserved."); return; }
    const validation = validateRuleForm(editor.form, vocabulary.data);
    setErrors(validation);
    if (Object.keys(validation).length) return;
    save.mutate({ id: editor.source?.id ?? null, form: editor.form,
      payload: editor.source ? rulePatch(editor.form, editor.source, editor.rebuilding) : ruleDefinition(editor.form) });
  }
  const saved = editor?.source;
  const previewValidation = editor && vocabulary.data ? validateRuleForm(editor.form, vocabulary.data) : {};
  const previewBlocked = catalog.isError || !catalog.data ? "saved recognition is unavailable; refresh recognition first."
    : repairNeeded ? "confirm an explicit legacy repair first." : Object.keys(previewValidation).length ? "complete the required definition fields, condition rows and sources." : undefined;
  const snapshotAction = (action: RuleAction["action"], enabled?: boolean) => {
    if (saved) transition.mutate({ id: saved.id, expectedRevision: saved.revision, action, enabled });
  };
  const actionBlocked = !saved || stale || dirty || busy || query.isError;

  return <div className="space-y-6">
    <header className="space-y-3"><div className="flex flex-wrap items-center justify-between gap-3"><h1 className="text-2xl font-bold">Rules</h1>
      <Badge variant="secondary">{permission?.role ?? "Permissions loading"}</Badge></div>
      <p className="text-sm text-muted-foreground">Verified analysis uses only approved, enabled database rules from this workspace. Default candidates are import-only, publicly sourced disabled drafts; review their evidence and screening thresholds before approval.</p>
    </header>
    <nav aria-label="Rules sections" className="flex flex-wrap gap-2">{(["rules", "drugs", "audit"] as const).map(value => <Button key={value} type="button" variant={tab === value ? "secondary" : "outline"}
      className="min-h-12" aria-current={tab === value ? "page" : undefined} aria-controls={`rules-panel-${value}`} onClick={() => setTab(value)}>{value === "rules" ? "Rule definitions" : value === "drugs" ? "Drug recognition" : "Audit history"}</Button>)}</nav>
    {access.isError && <p role="alert">Could not verify editing or review permissions. All mutation controls stay disabled.</p>}
    {vocabulary.isError ? <div className="space-y-3"><p role="alert">Could not load the shared clinical vocabulary. Editing and preview remain unavailable.</p><Button type="button" onClick={() => void vocabulary.refetch()}>Retry vocabulary</Button></div>
      : !vocabulary.data ? <p>Loading clinical vocabulary…</p> : <>
        <div id="rules-panel-rules" hidden={tab !== "rules"} className="space-y-5">
          <div className="flex flex-wrap gap-2"><Button type="button" className="min-h-12" disabled={!canEdit || busy} onClick={() => selectRule()}>New rule</Button>
            <ConfirmAction label="Import default rules" title="Import disabled default candidates?" confirmLabel="Import disabled drafts"
              description="Import the publicly sourced default candidates as disabled drafts only. Existing IDs, including edited or retired rules, are skipped. This does not activate clinical policy."
              disabled={!canEdit || busy} onConfirm={() => importDefaults.mutate()} />
            <Button type="button" variant="outline" className="min-h-12" disabled={busy} onClick={() => { refresh(); void catalog.refetch(); }}>Refresh saved data</Button>
          </div>
          {query.isPending ? <p>Loading rules…</p> : query.isError ? <p role="alert">Could not load saved rules. Your editor snapshot is kept; refresh before saving or reviewing.</p> : null}
          <div className="grid items-start gap-5 xl:grid-cols-[minmax(14rem,1fr)_minmax(0,3fr)]">
            <aside className="space-y-3" aria-label="Saved rules"><search><form method="get" action={root} onSubmit={event => event.preventDefault()} className="space-y-2">
              <Label htmlFor="rules-filter">Filter saved rules</Label><Input id="rules-filter" name="filter" type="search" value={search} className="min-h-12 text-base!" onChange={event => setSearch(event.target.value)} />
            </form></search>
              <ul className="max-h-[36rem] space-y-2 overflow-auto">{rules.filter(rule => `${rule.title} ${rule.ruleId} ${rule.status}`.toLowerCase().includes(search.toLowerCase())).map(rule => <li key={rule.id} className="rounded-xl border bg-card p-3">
                <Button type="button" variant="ghost" className="min-h-12 w-full justify-start whitespace-normal text-start" aria-pressed={saved?.id === rule.id} disabled={busy} onClick={() => selectRule(rule)}>{rule.title}</Button>
                <p className="break-all text-xs">{ruleStatusLabel(rule)}</p><p className="text-xs text-muted-foreground">{rule.ruleId} · revision {rule.revision} · {rule.enabled ? "enabled" : "disabled"}</p>
              </li>)}</ul>
              {!query.isPending && !query.isError && !rules.length && <p className="text-sm">No saved rules. Create a rule or explicitly import disabled defaults.</p>}
            </aside>
            {editor ? <div className="min-w-0 space-y-5">
              <section className="space-y-4 rounded-xl border bg-card p-5" aria-labelledby="rule-definition-heading">
                <h2 id="rule-definition-heading" className="text-xl font-semibold">{saved ? "Saved rule definition" : "New disabled draft"}</h2>
                <p className="font-medium">{dirty ? "DRAFT: pending clinician review (unsaved definition)" : saved ? ruleStatusLabel(saved) : "DRAFT: pending clinician review"}</p>
                {saved && <p className="text-sm">Saved revision {saved.revision} · {saved.enabled ? "Enabled" : "Disabled"} · {currentRuleApproval(saved, catalog.isError ? undefined : catalog.data?.revision) && !dirty ? "Review is current" : "Not a current approved definition for analysis"}</p>}
                {saved?.reviewedAt && <p className="break-all text-sm">Reviewed by {saved.reviewerName || saved.reviewedBy || "not recorded"} at <time dateTime={saved.reviewedAt}>{saved.reviewedAt}</time>; source {saved.approvedSourceUrl ? <a className="underline" href={saved.approvedSourceUrl} target="_blank" rel="noreferrer">source URL</a> : "not recorded"}; recognition revision {saved.reviewedCatalogRevision ?? "not bound"}.</p>}
                {dirty && <p className="text-sm font-medium">Unsaved rule changes</p>}
                {stale && <div className="space-y-2"><p role="alert">This rule changed remotely or was deleted. Your unsaved snapshot is kept; reload the saved rule before saving or reviewing.</p>
                  <Button type="button" variant="outline" className="min-h-12" disabled={busy} onClick={() => {
                    if (dirty && !window.confirm("Discard unsaved rule changes and reload the saved definition?")) return;
                    if (fresh && !query.isError) setEditor(editorFor(fresh)); else { setEditor(null); refresh(); }
                    clearErrors();
                  }}>Reload saved rule</Button></div>}
                {repairNeeded && <div className="space-y-3 rounded border p-3"><p className="text-sm">This legacy definition is not fully representable. Nothing has been silently replaced: {editor.problems.join(" ")}</p>
                  <ConfirmAction label="Rebuild legacy definition" title="Rebuild this legacy definition as a draft?" confirmLabel="Start explicit repair"
                    description="Rebuild unsupported JSON sections using the editor. Check the stored evidence below and supply complete condition, class and source rows before saving. The previous definition and review remain in audit history."
                    disabled={!canEdit || busy || stale || saved?.status === "retired"} onConfirm={() => setEditor(current => current ? { ...current, rebuilding: true } : current)} />
                  <details><summary className="cursor-pointer text-sm">Stored legacy JSON</summary><pre className="mt-2 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">{JSON.stringify({ drugClasses: saved?.drugClasses, appliesWhen: saved?.appliesWhen, sources: saved?.sources }, null, 2)}</pre></details>
                </div>}
                <form method="post" action={saved ? `${root}/${saved.id}` : root} aria-labelledby="rule-definition-heading" noValidate className="space-y-5" onSubmit={submit}>
                  <RuleFormFields form={editor.form} errors={errors} vocabulary={vocabulary.data} readOnly={!canEdit || lifecyclePending || repairNeeded || saved?.status === "retired"}
                    onChange={form => { setEditor(current => current ? { ...current, form } : current); setErrors({}); setNotice(""); if (save.isError) save.reset(); }} />
                  {!!messages.length && <div ref={errorSummary} role="alert" tabIndex={-1} className="space-y-2 rounded border border-destructive p-4">
                    <p className="font-medium">The rule action could not be completed.</p><ul className="list-disc ps-5 text-sm">{messages.map(message => <li key={message}>{message}</li>)}</ul></div>}
                  <Button type="submit" className="min-h-12" disabled={!canEdit || stale || busy || repairNeeded || saved?.status === "retired"}>Save rule draft</Button>
                </form>
                {!canEdit && <p className="text-sm text-muted-foreground">An editor or owner is required to change definitions. Reviewers can review without editing or administering the workspace.</p>}
                {saved && <div className="space-y-3 border-t pt-4"><p className="text-sm">Lifecycle actions apply only to the saved revision, never unsaved fields. Approval does not turn on a disabled candidate. Enablement alone does not approve it.</p>
                  <div className="flex flex-wrap gap-2">
                    <Button type="button" variant="outline" className="min-h-12" disabled={!canEdit || actionBlocked || saved.status !== "draft"} onClick={() => snapshotAction("submit")}>Submit for review</Button>
                    <ConfirmAction label="Approve rule" title={`Approve saved rule revision ${saved.revision}?`} confirmLabel="Approve saved definition"
                      description={`Confirm clinician review of the saved sources, conditions, thresholds and recognition revision ${catalog.data?.revision ?? "unavailable"}. This is not patient-specific treatment advice. The rule will remain ${saved.enabled ? "enabled" : "disabled"} after approval.`}
                      disabled={!canReview || actionBlocked || repairNeeded || !["draft", "in_review"].includes(saved.status) || catalog.isError || !catalog.data} onConfirm={() => snapshotAction("approve")} />
                    <ConfirmAction label={saved.enabled ? "Disable rule" : "Enable rule"} title={saved.enabled ? "Disable this saved rule?" : "Enable this saved rule?"} confirmLabel={saved.enabled ? "Disable saved rule" : "Enable saved rule"}
                      description="Only current approved and enabled rules run. This switch does not change the reviewed definition or approve a draft. Retired rules cannot be enabled."
                      disabled={!canReview || actionBlocked || saved.status === "retired"} onConfirm={() => snapshotAction("enablement", !saved.enabled)} />
                    <ConfirmAction label="Retire rule" title="Retire and disable this saved rule?" confirmLabel="Retire saved rule"
                      description="The rule will stop participating in analysis. Its definition, prior review and audit history are retained. Reopening later produces a draft requiring a fresh review."
                      disabled={!canReview || actionBlocked || saved.status === "retired"} onConfirm={() => snapshotAction("retire")} />
                    <Button type="button" variant="outline" className="min-h-12" disabled={!canEdit || actionBlocked || saved.status !== "retired"} onClick={() => snapshotAction("reopen")}>Reopen as draft</Button>
                    <ConfirmAction label="Delete rule" title="Delete this saved rule?" confirmLabel="Delete saved rule"
                      description="The saved rule will be removed from this workspace. Its append-only audit evidence is retained; deletion cannot be undone through this editor."
                      disabled={!canDelete || actionBlocked} onConfirm={() => remove.mutate({ id: saved.id, expectedRevision: saved.revision })} />
                    <Button type="button" variant="outline" className="min-h-12" onClick={() => { setAuditRuleId(saved.id); setTab("audit"); }}>View rule audit</Button>
                  </div>
                </div>}
              </section>
              <CanonicalPreview workspaceId={workspaceId} userId={userId} vocabulary={vocabulary.data} catalogRevision={catalog.isError ? undefined : catalog.data?.revision}
                definition={Object.keys(previewValidation).length || repairNeeded ? null : ruleDefinition(editor.form, saved, editor.rebuilding)}
                active={tab === "rules"} blockedReason={previewBlocked} />
            </div> : <p className="text-sm">Select a saved rule, or create a new disabled draft.</p>}
          </div>
        </div>
        <div id="rules-panel-drugs" hidden={tab !== "drugs"}><DrugCatalogEditor workspaceId={workspaceId} userId={userId} vocabulary={vocabulary.data}
          snapshot={catalog.data} canEdit={canEdit} loading={catalog.isPending} failed={catalog.isError} invalid={invalidCatalog}
          repair={invalidCatalog && !catalogRepair.isError ? catalogRepair.data : undefined}
          repairLoading={canEdit && invalidCatalog && catalogRepair.isPending} repairFailed={canEdit && invalidCatalog && catalogRepair.isError}
          onRefresh={() => { void catalog.refetch(); if (canEdit && invalidCatalog) void catalogRepair.refetch(); }} /></div>
        <div id="rules-panel-audit" hidden={tab !== "audit"}><RuleAudit key={auditRuleId ?? "workspace"} workspaceId={workspaceId} userId={userId} ruleId={auditRuleId} active={tab === "audit"} onWorkspaceHistory={() => setAuditRuleId(null)} /></div>
      </>}
    <p role="status" className="text-sm text-muted-foreground">{notice}</p>
  </div>;
}
