"use client";
import { useParams } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useEffect, useMemo, useRef, type FormEvent } from "react";
import { ApiError, get, post } from "@/lib/api";
import { createRequestScope } from "@/lib/request-scope";
import type { ConfigVersion, ConfigVersionDetails, ConfigVersionsResponse, TestCase, TestRun, TestRunResult, RunTestsResponse } from "@/lib/types";
import { useAuth } from "@/providers/AuthProvider";
import { boundConfigRun, composeWorkflowConfig, configActionErrors, parseWorkflowConfig, publishBlockingReason,
  sameWorkflowFields, validateWorkflowFields, workflowEditorState,
  type WorkflowEditorState, type WorkflowErrors, type WorkflowField, type WorkflowFields } from "@/lib/workflow-config";
import { useUnsavedChanges } from "@/components/navigation-guard";
import { ConfigVersions } from "@/components/workflow/config-versions";
import { RuleLinkTerms } from "@/components/workflow/rule-link-terms";
import { StepSettings } from "@/components/workflow/step-settings";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { CheckCircle, ShieldCheck } from "lucide-react";

const STEPS = [
  { id: 1, label: "Read case", icon: "📖", desc: "Parse clinical values & meds" },
  { id: 2, label: "Plan", icon: "📋", desc: "Identify clinical problems" },
  { id: 3, label: "Retrieve", icon: "🔍", desc: "Search guideline chunks" },
  { id: 4, label: "Draft", icon: "✍️", desc: "Generate recommendations" },
  { id: 5, label: "Verify", icon: "🔒", desc: "Safety-locked quote check" },
  { id: 6, label: "Reconcile", icon: "🔗", desc: "Link patient flags" },
];

export default function WorkflowPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const { user } = useAuth();
  // Route/auth ownership changes replace the whole editor, not just its query result.
  return <WorkflowWorkspace key={`${workspaceId}:${user?.uid}`} workspaceId={workspaceId} userId={user?.uid ?? ""} />;
}

type DraftSubmission = { configJson: string; publishedId: string; sourceId: string; fields: WorkflowFields };
type TestSession = { configId: string; attempt: number; baseline: Record<string, string[]>; rows: TestRunResult[] };

function WorkflowWorkspace({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const qc = useQueryClient();
  const publishedKey = ["config", workspaceId, userId, "published"];
  const versionsKey = ["config", workspaceId, userId, "versions"];
  const publishedQuery = useQuery<ConfigVersion>({ queryKey: publishedKey, queryFn: () => get(`/api/workspaces/${workspaceId}/config/published`) });
  const versionsQuery = useQuery<ConfigVersionsResponse>({ queryKey: versionsKey, queryFn: () => get(`/api/workspaces/${workspaceId}/config/versions`) });
  const cfg = publishedQuery.data;
  const publishedJson = cfg?.configJson;
  const parsed = useMemo(() => publishedJson ? parseWorkflowConfig(publishedJson) : null, [publishedJson]);
  const [editor, setEditor] = useState<WorkflowEditorState | null>(null);
  const [activeStep, setActiveStep] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [formErrors, setFormErrors] = useState<WorkflowErrors>({});
  const [notice, setNotice] = useState("");
  const [testSession, setTestSession] = useState<TestSession | null>(null);
  const testScope = useRef(createRequestScope());
  const mounted = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  const stepHeading = useRef<HTMLHeadingElement>(null);
  const focusStep = useRef(false);
  useEffect(() => { mounted.current = true; const scope = testScope.current;
    return () => { mounted.current = false; scope.cancel(); }; }, []);
  useEffect(() => { if (focusStep.current) { stepHeading.current?.focus(); focusStep.current = false; } }, [activeStep]);
  const dirty = !!editor && !sameWorkflowFields(editor.fields, editor.savedFields);
  useUnsavedChanges(dirty);
  const role = versionsQuery.isError ? undefined : versionsQuery.data?.role.toLowerCase();
  const canEdit = role === "owner" || role === "editor";
  const isOwner = role === "owner";
  const versions = versionsQuery.data?.versions ?? [];
  const selected = versions.find(version => version.id === selectedId) ?? versions.find(version => version.status === "draft")
    ?? versions.find(version => version.id === cfg?.id) ?? versions[0];

  function remember(version: ConfigVersion) {
    qc.setQueryData<ConfigVersionsResponse>(versionsKey, previous => {
      if (!previous) return previous;
      const existing = previous.versions.find(item => item.id === version.id);
      const detail: ConfigVersionDetails = { ...version, latestTestRun: existing?.latestTestRun ?? null,
        publishBlockingReason: version.status === "draft" ? existing?.publishBlockingReason ?? "no test run" : "only drafts can be published" };
      return { ...previous, versions: [detail, ...previous.versions.filter(item => item.id !== version.id)].sort((left, right) => right.version - left.version) };
    });
  }
  function refresh() { void qc.invalidateQueries({ queryKey: ["config", workspaceId] }); }
  const saveDraft = useMutation({
    mutationFn: (submitted: DraftSubmission) => post<ConfigVersion>(`/api/workspaces/${workspaceId}/config/draft`,
      { configJson: submitted.configJson, publishedConfigId: submitted.publishedId }),
    onSuccess: (version, submitted) => {
      if (!mounted.current) return;
      const next = workflowEditorState(version, submitted.publishedId);
      if (!next) { setNotice("The saved version could not be read. Your edits are still kept; refresh before continuing."); return; }
      setEditor(current => current?.source.id !== submitted.sourceId ? current
        : sameWorkflowFields(current.fields, submitted.fields) ? next : { ...next, fields: current.fields });
      setSelectedId(version.id); remember(version); setFormErrors({});
      setNotice(`Saved draft v${version.version}. Real tests are required before publishing.`); refresh();
    },
  });
  const publish = useMutation({
    mutationFn: (id: string) => post<ConfigVersion>(`/api/workspaces/${workspaceId}/config/${id}/publish`, {}),
    onSuccess: version => {
      if (!mounted.current) return;
      remember(version); qc.setQueryData(publishedKey, version); setSelectedId(version.id);
      setNotice(`Published v${version.version}.`); refresh();
    },
  });
  const discard = useMutation({
    mutationFn: (id: string) => post<ConfigVersion>(`/api/workspaces/${workspaceId}/config/${id}/discard`, {}),
    onSuccess: version => {
      if (!mounted.current) return;
      remember(version);
      if (editor?.source.id === version.id && cfg) setEditor(workflowEditorState(cfg, cfg.id, parsed));
      setNotice(`Draft v${version.version} discarded; version history is kept.`); refresh();
    },
  });
  const rollback = useMutation({
    mutationFn: (version: number) => post<ConfigVersion>(`/api/workspaces/${workspaceId}/config/rollback/${version}`, {}),
    onSuccess: version => {
      if (!mounted.current || !cfg) return;
      setEditor(workflowEditorState(version, cfg.id)); remember(version); setSelectedId(version.id); setFormErrors({});
      setNotice(`Restored as draft v${version.version}. Published is unchanged; fresh tests are required.`); refresh();
    },
  });
  const runTests = useMutation({
    mutationFn: async (configId: string) => {
      const operation = testScope.current.start();
      try {
        const cases = await get<TestCase[]>(`/api/workspaces/${workspaceId}/tests`, { signal: operation.signal });
        if (!cases.length) throw new ApiError(400, "Workspace has no test cases; add a case and run tests.");
        if (cases.length > 10) throw new ApiError(400, "Run at most 10 cases per request. Use the Tests page to run bounded subsets, then rerun the full current suite before publishing.");
        const histories = await Promise.all(cases.map(c => get<TestRun[]>(`/api/workspaces/${workspaceId}/tests/runs/${c.id}`, { signal: operation.signal })));
        if (!operation.isCurrent()) throw new DOMException("Test request cancelled.", "AbortError");
        setTestSession({ configId, attempt: Date.now(), baseline: Object.fromEntries(cases.map((c, index) => [c.id, histories[index].map(run => run.id)])),
          rows: cases.map(c => ({ id: "", caseId: c.id, caseName: c.name, status: "queued", durationMilliseconds: 0 })) });
        return await post<RunTestsResponse>(`/api/workspaces/${workspaceId}/tests/run`, { configId, caseIds: cases.map(c => c.id) }, { signal: operation.signal });
      } finally { operation.finish(); }
    },
    onSuccess: (response, configId) => {
      if (!mounted.current) return;
      setTestSession(current => current?.configId === configId ? { ...current, rows: response.results } : current);
      setNotice(`Tests finished for the selected draft. Publication uses the refreshed server gate, not the run response.`);
    },
    onSettled: async () => {
      if (!mounted.current) return;
      // Keep actions disabled until the server gate has been fetched, on success AND failure.
      await qc.invalidateQueries({ queryKey: versionsKey });
      await qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
    },
  });
  const testProgress = useQuery({
    queryKey: ["test-progress", workspaceId, userId, testSession?.attempt],
    enabled: runTests.isPending && !!testSession,
    refetchInterval: runTests.isPending ? 1000 : false,
    retry: false,
    queryFn: async ({ signal }) => {
      const session = testSession!;
      const rows = await Promise.all(session.rows.map(async row => {
        const history = await get<TestRun[]>(`/api/workspaces/${workspaceId}/tests/runs/${row.caseId}`, { signal });
        const latest = history.find(run => run.configId === session.configId && !session.baseline[row.caseId].includes(run.id));
        return latest ? { ...row, ...latest } : row;
      }));
      return { attempt: session.attempt, rows };
    },
  });
  // Polling affects display only; no client comparison or cached progress can enable publication.
  const progressRows = runTests.isPending && testProgress.data && testProgress.data.attempt === testSession?.attempt
    ? testProgress.data.rows : testSession?.rows ?? [];
  const lifecyclePending = publish.isPending || discard.isPending || rollback.isPending;
  const busy = saveDraft.isPending || lifecyclePending || runTests.isPending;
  const mutationError = saveDraft.error || publish.error || discard.error || rollback.error;
  const errorMessages = [...Object.values(formErrors), ...(mutationError ? configActionErrors(mutationError) : []),
    ...(runTests.error ? [runTests.error instanceof ApiError ? runTests.error.message : "Tests could not be completed. Refresh the server gate and retry."] : [])];
  useEffect(() => { if (Object.keys(formErrors).length || mutationError) errorSummary.current?.focus(); }, [formErrors, mutationError]);

  // Initialize exactly once per published ID, with no synchronization effect tied to parsed JSON or keystrokes.
  // A dirty editor pins its original complete snapshot when a background publication arrives.
  if (cfg && parsed && (!editor || (!dirty && !busy && editor.publishedId !== cfg.id))) setEditor(workflowEditorState(cfg, cfg.id, parsed));

  const stalePublished = !!editor && !!cfg && editor.publishedId !== cfg.id;
  const publishReason = publishBlockingReason(selected, role, dirty, runTests.isPending || versionsQuery.isFetching || versionsQuery.isError);
  const sourceDetails = versions.find(version => version.id === editor?.source.id && version.configJson === editor.source.configJson);
  const run = dirty ? null : boundConfigRun(sourceDetails);
  const completedSteps = run?.completedSteps ?? [];
  function clearErrors() { setFormErrors({}); saveDraft.reset(); publish.reset(); discard.reset(); rollback.reset(); runTests.reset(); }
  function updateField(key: WorkflowField, value: string) {
    setEditor(current => current ? { ...current, fields: { ...current.fields, [key]: value } } : current);
    setFormErrors(previous => { const next = { ...previous }; delete next[key]; return next; });
    if (saveDraft.isError) saveDraft.reset(); setNotice("");
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editor || !canEdit || stalePublished || busy) return;
    const errors = validateWorkflowFields(editor.fields);
    setFormErrors(errors);
    if (Object.keys(errors).length) return;
    saveDraft.mutate({ configJson: JSON.stringify(composeWorkflowConfig(editor.base, editor.fields)),
      publishedId: editor.publishedId, sourceId: editor.source.id, fields: { ...editor.fields } });
  }
  function reloadPublished() {
    if (!cfg || !parsed || (dirty && !window.confirm("Discard unsaved changes and reload the published configuration?"))) return;
    setEditor(workflowEditorState(cfg, cfg.id, parsed)); setSelectedId(cfg.id); clearErrors(); setNotice("Published configuration reloaded.");
  }

  if (publishedQuery.isPending) return <div><h1 className="text-2xl font-bold">Workflow</h1><p>Loading published configuration…</p></div>;
  if (!cfg || !parsed || !editor) return <div className="space-y-4"><h1 className="text-2xl font-bold">Workflow</h1>
    <p role="alert">The published configuration could not be loaded. No configuration changes have been made.</p>
    <Button type="button" onClick={() => void publishedQuery.refetch()}>Retry</Button></div>;

  return <div className="space-y-6">
    <header className="flex flex-wrap items-center justify-between gap-4">
      <h1 className="text-2xl font-bold">Workflow</h1>
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="secondary">Published v{cfg.version}</Badge>
        {dirty && <Badge variant="outline">Unsaved changes</Badge>}
        <Button type="submit" form="workflow-config-form" variant="outline" className="min-h-12" aria-describedby="workflow-save-reason"
          disabled={!canEdit || stalePublished || busy}>Save draft</Button>
        <Button type="button" variant="outline" className="min-h-12" disabled={busy} onClick={reloadPublished}>Reload published</Button>
      </div>
    </header>
    <p id="workflow-save-reason" className="text-sm text-muted-foreground">
      {stalePublished ? "Published configuration changed. Your unsaved edits are kept; reload published before saving."
        : !canEdit ? "An editor or owner is required to save drafts. Permissions must load successfully first."
          : `Editing ${editor.source.status} v${editor.source.version}. Save keeps every untouched configuration key.`}
    </p>
    <p role="status" className="text-sm text-muted-foreground">{notice}</p>
    <nav aria-label="Workflow steps">
      <ol className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
        {STEPS.map(step => <li key={step.id}>
          <Button type="button" variant="outline" aria-current={activeStep === step.id ? "step" : undefined}
            aria-controls="workflow-settings-panel" className={`relative h-auto min-h-40 w-full flex-col whitespace-normal p-4 text-center focus-visible:outline-2 focus-visible:outline-offset-2 ${activeStep === step.id ? "border-primary bg-accent" : ""}`}
            onClick={() => { focusStep.current = activeStep !== step.id; setActiveStep(step.id); }}>
            <span aria-hidden="true" className="text-3xl">{step.icon}</span>
            <span className="font-semibold">{step.label}</span>
            <span className="text-xs text-muted-foreground">{step.desc}</span>
            <span className="text-xs">{completedSteps.includes(step.id) ? "Completed" : "Not completed"}</span>
            {step.id === 5 && <ShieldCheck aria-hidden="true" className="absolute end-2 top-2 size-4" />}
            {completedSteps.includes(step.id) && <CheckCircle aria-hidden="true" className="absolute start-2 top-2 size-4" />}
          </Button>
        </li>)}
      </ol>
    </nav>
    <p className="text-sm text-muted-foreground">Step completion uses only the latest real test of the loaded v{editor.source.version}: {run?.status ?? "no test run"}. Opening settings never completes a step.</p>
    {errorMessages.length > 0 && <div ref={errorSummary} tabIndex={-1} role="alert" className="space-y-2 rounded-lg border border-destructive p-4">
      <p className="font-medium">The draft or version action could not be completed.</p>
      <ul className="list-disc ps-5 text-sm">{errorMessages.map(message => <li key={message}>{message}</li>)}</ul>
    </div>}
    <section id="workflow-settings-panel" className="space-y-5 rounded-xl border bg-card p-5">
      <h2 ref={stepHeading} id="workflow-settings-heading" tabIndex={-1} className="text-lg font-semibold focus-visible:outline-2">{STEPS[activeStep - 1].label} settings</h2>
      <form id="workflow-config-form" method="post" action={`/api/workspaces/${workspaceId}/config/draft`}
        aria-labelledby="workflow-settings-heading" noValidate onSubmit={submit} hidden={activeStep === 6}>
        <StepSettings step={activeStep} fields={editor.fields} errors={formErrors} base={editor.base}
          readOnly={!canEdit || lifecyclePending} onChange={updateField} />
      </form>
      {/* Kept mounted, but hidden/inaccessible outside Reconcile, so unsaved rule edits survive step switches. */}
      <RuleLinkTerms workspaceId={workspaceId} userId={userId} role={role} hidden={activeStep !== 6} />
    </section>
    <details className="rounded-xl border bg-card p-5">
      <summary className="cursor-pointer font-medium">Complete draft JSON (read-only preview)</summary>
      <pre className="mt-4 max-h-96 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-4 text-xs">{JSON.stringify(composeWorkflowConfig(editor.base, editor.fields), null, 2)}</pre>
    </details>
    {versionsQuery.isError && <p role="alert">Could not load versions or test permissions. Publishing stays disabled.</p>}
    <ConfigVersions versions={versions} selected={selected} published={parsed} busy={busy || versionsQuery.isFetching}
      canEdit={canEdit} isOwner={isOwner} publishReason={publishReason} onSelect={setSelectedId}
      onLoad={() => {
        if (!selected || !canEdit || (dirty && !window.confirm("Discard unsaved changes and load this draft?"))) return;
        setEditor(workflowEditorState(selected, cfg.id)); clearErrors(); setNotice(`Loaded draft v${selected.version}. Saving edits creates a new draft.`);
      }}
      onPublish={() => { if (selected && publishReason === null && !busy) publish.mutate(selected.id); }}
      onDiscard={() => {
        if (!selected || !canEdit || busy) return;
        if (window.confirm(`Discard draft v${selected.version}? History is kept.${dirty && editor.source.id === selected.id ? " Unsaved edits to this draft will be lost." : ""}`)) discard.mutate(selected.id);
      }}
      onRollback={() => {
        if (!selected || !isOwner || busy) return;
        if (window.confirm(`Restore v${selected.version} as a new draft? Published stays unchanged; fresh tests are required.${dirty ? " Unsaved configuration edits will be lost." : ""}`)) rollback.mutate(selected.version);
      }}
      onRunTests={() => { if (selected?.status === "draft" && canEdit && !busy) { runTests.reset(); setTestSession(null); runTests.mutate(selected.id); } }}
    />
    {testSession && <section className="space-y-3 rounded-xl border bg-card p-5" aria-labelledby="draft-test-heading">
      <h2 id="draft-test-heading" className="text-lg font-semibold">Draft test progress</h2>
      <p className="text-sm">Configuration: {testSession.configId}. {progressRows.filter(row => row.status === "passed" || row.status === "failed").length} of {progressRows.length} completed.</p>
      <progress aria-labelledby="draft-test-heading" className="h-3 w-full" max={Math.max(1, progressRows.length)}
        value={progressRows.filter(row => row.status === "passed" || row.status === "failed").length} />
      {testProgress.isError && runTests.isPending && <p className="text-sm">Progress is temporarily unavailable. The run is still pending; publication remains blocked.</p>}
      <ul className="space-y-3">{progressRows.map(row => <li key={row.caseId} className="rounded-lg border p-3">
        <p className="font-medium">{row.caseName}</p><p className="text-sm">{row.status}{row.failureCode ? ` (${row.failureCode})` : ""}</p>
        {row.diff && <p className="mt-2 whitespace-pre-wrap text-sm">{row.diff}</p>}
      </li>)}</ul>
      <p className="text-sm text-muted-foreground">Diff positions refer to expectation entries and result problems, without storing clinical text. Tests do not establish clinical validity.</p>
    </section>}
  </div>;
}
