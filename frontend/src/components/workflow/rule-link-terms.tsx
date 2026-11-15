"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { get, patch, post } from "@/lib/api";
import { configActionErrors } from "@/lib/workflow-config";
import { currentRuleApproval, ruleStatusLabel } from "@/lib/rules";
import { portableRegexProblem } from "@/lib/clinical-text";
import type { DrugCatalogSnapshot, Rule } from "@/lib/types";
import { GuardedLink, useUnsavedChanges } from "@/components/navigation-guard";
import { ConfirmAction } from "@/components/rules/confirm-action";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type Props = { workspaceId: string; userId: string; role?: string; hidden: boolean };
type Editor = { source: Rule; text: string; saved: string };
type Submission = { id: string; expectedRevision: number; text: string };
const editorFor = (rule: Rule): Editor => ({ source: rule, text: rule.linkTerms ?? "", saved: rule.linkTerms ?? "" });

export function RuleLinkTerms(props: Props) {
  return <RuleLinkTermsWorkspace key={`${props.workspaceId}:${props.userId}`} {...props} />;
}

function RuleLinkTermsWorkspace({ workspaceId, userId, role, hidden }: Props) {
  const id = useId();
  const qc = useQueryClient();
  const queryKey = ["rules", workspaceId, userId];
  const query = useQuery<Rule[]>({ queryKey, enabled: !!userId, queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/rules`, { signal }) });
  const catalog = useQuery<DrugCatalogSnapshot>({ queryKey: ["drugs", workspaceId, userId], enabled: !!userId,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/drugs`, { signal }) });
  const [editor, setEditor] = useState<Editor | null>(null);
  const [notice, setNotice] = useState("");
  const [validation, setValidation] = useState("");
  const mounted = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const dirty = !!editor && editor.text !== editor.saved;
  useUnsavedChanges(dirty);
  const canEdit = role === "owner" || role === "editor";
  const canReview = role === "owner" || role === "reviewer";
  const rules = query.data?.filter(rule => rule.workspaceId === workspaceId) ?? [];
  const fresh = rules.find(rule => rule.id === editor?.source.id);
  const stale = !!editor && (query.isError || !fresh || fresh.revision !== editor.source.revision);

  const remember = (rule: Rule, submitted?: Submission) => {
    if (!mounted.current || rule.workspaceId !== workspaceId) return;
    setEditor(current => {
      if (current?.source.id !== rule.id) return current;
      const next = editorFor(rule);
      return submitted && current.text !== submitted.text ? { ...next, text: current.text } : next;
    });
    qc.setQueryData<Rule[]>(queryKey, previous => previous?.map(item => item.id === rule.id ? rule : item));
    void qc.invalidateQueries({ queryKey });
  };
  const save = useMutation({
    mutationFn: (submitted: Submission) => patch<Rule>(`/api/workspaces/${workspaceId}/rules/${submitted.id}`,
      { linkTerms: submitted.text, expectedRevision: submitted.expectedRevision }),
    onSuccess: (rule, submitted) => {
      remember(rule, submitted);
      if (mounted.current) setNotice(rule.status === "approved" ? "No definition changes; clinician review is preserved."
        : "Link terms saved as a draft. Clinician re-review is required.");
    },
  });
  const approve = useMutation({
    mutationFn: (submitted: { id: string; expectedRevision: number }) => post<Rule>(`/api/workspaces/${workspaceId}/rules/${submitted.id}/approve`,
      { expectedRevision: submitted.expectedRevision }),
    onSuccess: rule => {
      remember(rule);
      if (mounted.current) setNotice(`Clinician approval saved; the rule remains ${rule.enabled ? "enabled" : "disabled"}. This does not mark a workflow step complete.`);
    },
  });
  const busy = save.isPending || approve.isPending;
  const errors = save.error || approve.error;
  useEffect(() => { if ((validation || errors) && !hidden) errorSummary.current?.focus(); }, [validation, errors, hidden]);

  // Freeze dirty and in-flight snapshots. Every definition/lifecycle/enablement change advances the revision.
  if (!editor && rules.length) setEditor(editorFor(rules[0]));
  else if (editor && fresh && !dirty && !busy && fresh.revision > editor.source.revision) setEditor(editorFor(fresh));
  const reviewed = editor && !dirty && !stale && !catalog.isError && !!catalog.data && currentRuleApproval(editor.source, catalog.data.revision);

  return <div hidden={hidden} className="space-y-4">
    <p className="text-sm text-muted-foreground">Link terms belong to the saved rule, not the configuration snapshot. Definition changes remove clinician approval; verified analysis uses only current, approved and enabled workspace rules.</p>
    <GuardedLink href={`/w/${workspaceId}/rules`} className="inline-block min-h-12 py-3 text-sm underline">Open the full Rules editor, evidence and canonical preview</GuardedLink>
    {query.isPending ? <p>Loading rules…</p> : query.isError ? <p role="alert">Could not load rules. Your editor snapshot is kept; refresh before saving.</p> : null}
    {!editor ? <p className="text-sm">No rules are available. Create a rule in Rules to configure reconciliation.</p> : <>
      <form method="post" action={`/api/workspaces/${workspaceId}/rules/${editor.source.id}`} aria-labelledby={`${id}-heading`}
        className="space-y-4" onSubmit={event => {
          event.preventDefault();
          if (!canEdit || stale || busy || editor.source.status === "retired" || editor.text.length > 512) return;
          if (!dirty) { setNotice("No link-term changes to save. Existing clinician review is preserved."); return; }
          const problem = portableRegexProblem(editor.text);
          if (problem) { setValidation(problem); return; }
          save.mutate({ id: editor.source.id, expectedRevision: editor.source.revision, text: editor.text });
        }}>
        <h3 id={`${id}-heading`} className="font-medium">Rule link terms</h3>
        <div className="space-y-2">
          <Label htmlFor={`${id}-rule`}>Rule</Label>
          <select id={`${id}-rule`} name="rule" value={editor.source.id} disabled={busy}
            className="min-h-12 w-full rounded-lg border bg-background px-3 text-base focus-visible:outline-2 focus-visible:outline-offset-2"
            onChange={event => {
              const selected = rules.find(rule => rule.id === event.target.value);
              if (!selected || (dirty && !window.confirm("Discard unsaved link terms and load another rule?"))) return;
              setEditor(editorFor(selected)); setNotice(""); setValidation(""); save.reset(); approve.reset();
            }}>
            {!fresh && <option value={editor.source.id}>{editor.source.title} (unavailable)</option>}
            {rules.map(rule => <option key={rule.id} value={rule.id}>{rule.title}</option>)}
          </select>
        </div>
        <p className="text-sm font-medium">{dirty ? "DRAFT: pending clinician review (unsaved link terms)" : ruleStatusLabel(editor.source)}</p>
        <p className="text-sm">Saved revision {editor.source.revision} · {editor.source.enabled ? "Enabled" : "Disabled"} · {reviewed ? "Review is current" : "Not a current approved definition for analysis"}</p>
        {editor.source.reviewedAt && <p className="break-all text-sm">Reviewed by {editor.source.reviewedBy} at <time dateTime={editor.source.reviewedAt}>{editor.source.reviewedAt}</time>; recognition revision {editor.source.reviewedCatalogRevision ?? "not bound"}.</p>}
        {dirty && <p className="text-sm font-medium">Unsaved link terms</p>}
        {stale && <p role="alert">This rule changed remotely or is unavailable. Your unsaved snapshot is kept; reload the saved rule before saving or approving.</p>}
        <div className="space-y-2">
          <Label htmlFor={`${id}-terms`}>Link terms (regular expression)</Label>
          <p id={`${id}-help`} className="text-sm text-muted-foreground">Portable case-insensitive Python/.NET regex, up to 512 UTF-16 units; repeat bounds up to 10000. Use | for alternatives and escape literal brackets/braces. Lookarounds, inline flags, backreferences, possessive quantifiers and nested classes are unsupported; use Rules for bounded Python regex/sample testing.</p>
          <Input id={`${id}-terms`} name="linkTerms" value={editor.text} maxLength={512} readOnly={!canEdit || approve.isPending || editor.source.status === "retired"}
            aria-describedby={`${id}-help`} aria-invalid={!!validation || undefined} spellCheck={false} autoCapitalize="none" autoCorrect="off"
            className="min-h-12 text-base! focus-visible:outline-2 focus-visible:outline-offset-2"
            onChange={event => { setEditor({ ...editor, text: event.target.value }); setValidation(""); setNotice(""); if (save.isError) save.reset(); }} />
        </div>
        {(validation || errors) && <div ref={errorSummary} tabIndex={-1} role="alert" className="rounded border border-destructive p-3 text-sm">
          {validation && <p>{validation}</p>}{errors && configActionErrors(errors, "rule").map(message => <p key={message}>{message}</p>)}
        </div>}
        <Button type="submit" className="min-h-12" disabled={!canEdit || stale || busy || editor.source.status === "retired"}>Save link terms</Button>
      </form>
      <div className="flex flex-wrap gap-2">
        <ConfirmAction label="Approve rule" title={`Approve saved rule revision ${editor.source.revision}?`} confirmLabel="Approve saved definition"
          description={`Confirm clinician review of the saved definition and sources in the full Rules editor. This approval applies to the saved revision only and leaves the rule ${editor.source.enabled ? "enabled" : "disabled"}. It does not complete a workflow step.`}
          disabled={!canReview || stale || dirty || busy || !["draft", "in_review"].includes(editor.source.status) || catalog.isError || !catalog.data}
          onConfirm={() => approve.mutate({ id: editor.source.id, expectedRevision: editor.source.revision })} />
        <Button type="button" variant="outline" className="min-h-12" disabled={busy} onClick={() => {
          if (dirty && !window.confirm("Discard unsaved link terms and reload the saved rule?")) return;
          if (fresh && !query.isError) { setEditor(editorFor(fresh)); setNotice("Saved rule reloaded."); setValidation(""); save.reset(); approve.reset(); }
          void query.refetch(); void catalog.refetch();
        }}>Reload saved rule</Button>
      </div>
      {!canEdit && <p className="text-sm text-muted-foreground">An editor or owner is required to change link terms. Reviewers may review, but may not edit definitions.</p>}
      {catalog.isError && <p role="alert">Saved recognition is unavailable. Approval stays disabled; repair or refresh it in Rules.</p>}
    </>}
    <p role="status" className="text-sm text-muted-foreground">{notice}</p>
  </div>;
}
