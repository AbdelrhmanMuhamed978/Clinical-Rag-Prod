"use client";

import { useParams } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useRef, useEffect } from "react";
import { get, post, patch, del } from "@/lib/api";
import type { TestCase } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";

type TestRunResultItem = { caseId: string; caseName: string; status: string; diff?: string | null; durationMilliseconds: number };
type RunTestsResponse = { ran: number; results: TestRunResultItem[] };

export default function TestsPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const qc = useQueryClient();
  const root = `/api/workspaces/${workspaceId}/tests`;
  const { data } = useQuery<TestCase[]>({
    queryKey: ["tests", workspaceId],
    queryFn: ({ signal }) => get(root, { signal }),
  });
  const [name, setName] = useState("");
  const [input, setInput] = useState("");
  const [expected, setExpected] = useState("");
  const [editing, setEditing] = useState<{ id: string; name: string; input: string; expected: string } | null>(null);
  const [runPhase, setRunPhase] = useState<"idle" | "running" | "done">("idle");
  const [runResults, setRunResults] = useState<TestRunResultItem[]>([]);
  const [selectedCaseIds, setSelectedCaseIds] = useState<Set<string>>(new Set());
  const mounted = useRef(false);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const cases = data ?? [];
  const create = useMutation({
    mutationFn: () => post<TestCase>(root, { name, inputText: input, expectedJson: expected || undefined }),
    onSuccess: () => {
      if (!mounted.current) return;
      qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
      setName(""); setInput(""); setExpected(""); setEditing(null);
    },
  });
  const updateCase = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: Partial<TestCase> }) => patch<TestCase>(`${root}/${id}`, dto),
    onSuccess: () => {
      if (!mounted.current) return;
      qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
      setEditing(null);
    },
  });
  const removeCase = useMutation({
    mutationFn: (id: string) => del(`${root}/${id}`),
    onSuccess: () => {
      if (!mounted.current) return;
      qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
      setSelectedCaseIds(new Set());
    },
  });
  const runTests = useMutation({
    mutationFn: async (ids: string[] | null) => {
      const res = await post<RunTestsResponse>(`${root}/run`, { caseIds: ids });
      return res;
    },
    onSuccess: (result: RunTestsResponse) => {
      if (!mounted.current) return;
      setRunResults(result.results.map(r => ({ caseId: r.caseId, caseName: r.caseName, status: r.status, diff: r.diff ?? null, durationMilliseconds: r.durationMilliseconds })));
      setRunPhase("done");
      qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
    },
  });
  const importSeed = useMutation({
    mutationFn: () => post<{ importedCount: number }>(`${root}/import-seed`, {}),
    onSuccess: () => {
      if (!mounted.current) return;
      qc.invalidateQueries({ queryKey: ["tests", workspaceId] });
      setRunPhase("done");
    },
  });

  const busy = create.isPending || updateCase.isPending || removeCase.isPending || runTests.isPending || importSeed.isPending;
  const allSelected = cases.length > 0 && cases.every(c => selectedCaseIds.has(c.id));

  function startRun(ids: string[] | null) {
    setRunPhase("running");
    setRunResults([]);
    runTests.mutate(ids);
  }

  function handleDeleteCase(id: string) {
    if (window.confirm("Delete this test case?")) removeCase.mutate(id);
  }

  function handleDeleteSelected() {
    if (selectedCaseIds.size === 0) return;
    if (!window.confirm(`Delete ${selectedCaseIds.size} selected case(s)?`)) return;
    const ids = [...selectedCaseIds];
    ids.forEach(id => removeCase.mutate(id));
    setSelectedCaseIds(new Set());
  }

  function statusBadge(status: string) {
    return status === "passed" ? <Badge variant="secondary">Passed</Badge>
      : status === "failed" ? <Badge variant="destructive">Failed</Badge>
      : <Badge variant="outline">Running</Badge>;
  }

  const expectedSchema = {
    mustHaveProblems: ["Substrings that must match problem titles"],
    mustNotHaveProblems: ["Substrings that must not match problem titles"],
    mustFlag: ["Fact flag ids that must appear"],
    mustFireRules: ["Rule ids that should fire"],
    minVerifiedItemsPerProblem: 1,
    requireDraftWarning: true,
    forbiddenPhrases: ["Phrases that must not appear"],
    noUnverifiedShown: true,
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Tests</h1>
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="outline" disabled={busy || runPhase === "running"} onClick={() => importSeed.mutate()}>Import seed cases</Button>
          <Button type="button" disabled={busy || runPhase === "running" || cases.length === 0} onClick={() => startRun(null)}>Run all {cases.length}</Button>
        </div>
      </div>
      {runPhase === "running" && <div className="rounded-lg border bg-amber-50 p-4">
        <p className="text-sm">Running tests through the verified pipeline… results appear below as they complete.</p>
      </div>}
      {runPhase === "done" && runResults.length > 0 && <div className="rounded-lg border bg-card p-4">
        <h2 className="text-sm font-semibold mb-2">Run results</h2>
        <div className="space-y-2">
          {runResults.map(r => <div key={r.caseId} className="flex items-center gap-3 text-sm">
            <span className="w-32 truncate">{r.caseName}</span>
            {statusBadge(r.status)}
            {r.durationMilliseconds > 0 && <span className="ml-auto text-muted-foreground">{r.durationMilliseconds.toFixed(0)} ms</span>}
          </div>)}
          {runResults.some(r => r.diff) && <details className="mt-3"><summary className="cursor-pointer text-sm">Show diffs</summary>
            <ul className="mt-2 space-y-1 text-xs text-muted-foreground">
              {runResults.filter(r => r.diff).map(r => <li key={r.caseId}>{r.caseName}: {r.diff}</li>)}
            </ul></details>}
        </div>
      </div>}
      <Separator />
      <Card>
        <CardHeader><CardTitle>New case</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          <Input value={name} onChange={e => setName(e.target.value)} placeholder="Case name" className="min-h-12" />
          <Textarea value={input} onChange={e => setInput(e.target.value)} rows={4} placeholder="Case text — the full clinical case to analyze" className="min-h-[120px]" />
          <Textarea value={expected} onChange={e => setExpected(e.target.value)} rows={4} placeholder="Expected output JSON (optional)" className="min-h-[120px]" />
          <p className="text-xs text-muted-foreground">Expected JSON schema: {JSON.stringify(expectedSchema)}</p>
          <Button type="button" disabled={busy || !name || !input} onClick={() => create.mutate()}>Add case</Button>
        </CardContent>
      </Card>
      <div className="space-y-3">
        {cases.length === 0 && <p className="text-sm">No test cases. Add a case or import seed cases.</p>}
        {cases.map(c => {
          const lastRun = c.lastRunAt ? new Date(c.lastRunAt).toLocaleString() : null;
          const isEditing = editing?.id === c.id;
          const selected = selectedCaseIds.has(c.id);
          return (
            <Card key={c.id} className="border">
              <CardContent className="p-4 space-y-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex items-center gap-3">
                    <input type="checkbox" checked={selected} onChange={() => {
                      const next = new Set(selectedCaseIds);
                      if (next.has(c.id)) next.delete(c.id); else next.add(c.id);
                      setSelectedCaseIds(next);
                    }} className="size-4 accent-primary" />
                    {isEditing ? (
                      <div className="space-y-1">
                        <Input value={editing.name} onChange={e => setEditing(prev => prev ? { ...prev, name: e.target.value } : null)} className="min-h-10 text-base font-medium" />
                        <Textarea value={editing.input} onChange={e => setEditing(prev => prev ? { ...prev, input: e.target.value } : null)} rows={3} className="min-h-[80px]" />
                        <Textarea value={editing.expected} onChange={e => setEditing(prev => prev ? { ...prev, expected: e.target.value } : null)} rows={3} className="min-h-[80px]" />
                        <div className="flex gap-2">
                          <Button type="button" variant="outline" size="sm" onClick={() => { updateCase.mutate({ id: c.id, dto: { name: editing.name, inputText: editing.input, expectedJson: editing.expected || undefined } }); }}>Save</Button>
                          <Button type="button" variant="outline" size="sm" onClick={() => setEditing(null)}>Cancel</Button>
                        </div>
                      </div>
                    ) : (
                      <div className="flex items-center gap-2">
                        <span className="font-medium truncate w-48">{c.name}</span>
                        <Button type="button" variant="ghost" size="sm" onClick={() => setEditing({ id: c.id, name: c.name, input: c.inputText, expected: c.expectedJson ?? "" })}>Edit</Button>
                      </div>
                    )}
                  </div>
                  <div className="flex items-center gap-2">
                    {lastRun && <Badge variant="secondary">Last run: {lastRun}</Badge>}
                    <Button type="button" variant="outline" size="sm" disabled={busy || runPhase === "running"} onClick={() => startRun([c.id])}>Run</Button>
                    <Button type="button" variant="destructive" size="sm" onClick={() => handleDeleteCase(c.id)}>Delete</Button>
                  </div>
                </div>
                <p className="text-sm text-muted-foreground line-clamp-2">{c.inputText}</p>
                {c.expectedJson && <details className="mt-2"><summary className="cursor-pointer text-xs">Expected output</summary><pre className="mt-1 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 text-xs">{c.expectedJson}</pre></details>}
                {c.lastRunJson && <details className="mt-2"><summary className="cursor-pointer text-xs">Last run output</summary><pre className="mt-1 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 text-xs">{c.lastRunJson}</pre></details>}
              </CardContent>
            </Card>
          );
        })}
        {cases.length > 0 && <div className="border-t pt-4 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={allSelected} onChange={() => {
              if (allSelected) setSelectedCaseIds(new Set());
              else setSelectedCaseIds(new Set(cases.map(c => c.id)));
            }} className="size-4 accent-primary" />
            <span className="text-sm">{selectedCaseIds.size} of {cases.length} selected</span>
          </div>
          <div className="flex gap-2">
            <Button type="button" variant="outline" size="sm" disabled={busy || runPhase === "running" || selectedCaseIds.size === 0} onClick={() => startRun([...selectedCaseIds])}>Run selected ({selectedCaseIds.size})</Button>
            <Button type="button" variant="destructive" size="sm" disabled={busy || selectedCaseIds.size === 0} onClick={handleDeleteSelected}>Delete selected</Button>
          </div>
        </div>}
      </div>
    </div>
  );
}
