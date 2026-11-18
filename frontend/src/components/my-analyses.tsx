"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { FileText, RefreshCw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { api } from "@/lib/api";
import { analysesQueryKey, parseAnalysisList, parseSavedAnalysis, type AnalysisListEntry, type SavedAnalysis } from "@/lib/analysis";
import { createRequestScope } from "@/lib/request-scope";
import { AnalysisResult } from "@/components/analysis-result";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import styles from "./my-analyses.module.css";

export function MyAnalyses({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const [expanded, setExpanded] = useState(false);
  const [opened, setOpened] = useState<SavedAnalysis | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<AnalysisListEntry | null>(null);
  const [requests] = useState(createRequestScope);
  const dialog = useRef<HTMLDialogElement>(null);
  const dialogTitleId = useId();
  const dialogDescriptionId = useId();
  const queryClient = useQueryClient();
  const queryKey = analysesQueryKey(workspaceId, userId);
  const { data, isFetching, error, refetch } = useQuery({
    queryKey,
    queryFn: async ({ signal }) => parseAnalysisList(await api<unknown>(`/api/workspaces/${workspaceId}/analyses?mine=true`, {
      signal, cache: "no-store",
    }), workspaceId),
    enabled: expanded,
    retry: false,
  });
  useEffect(() => () => requests.cancel(), [requests]);

  async function openAnalysis(entry: AnalysisListEntry) {
    if (requests.active) return;
    const request = requests.start();
    setBusyId(entry.id);
    setOpened(null);
    setActionError(null);
    try {
      const response = await api<unknown>(`/api/workspaces/${workspaceId}/analyses/${entry.id}`, { signal: request.signal, cache: "no-store" });
      if (request.isCurrent()) setOpened(parseSavedAnalysis(response, workspaceId, entry.id));
    } catch {
      if (request.isCurrent()) setActionError("Could not open this analysis. No unverified content was displayed.");
    } finally {
      if (request.isCurrent()) { request.finish(); setBusyId(null); }
    }
  }

  async function deleteAnalysis(entry: AnalysisListEntry) {
    if (requests.active) return;
    const request = requests.start();
    setBusyId(entry.id);
    setActionError(null);
    try {
      await api<void>(`/api/workspaces/${workspaceId}/analyses/${entry.id}`, { method: "DELETE", signal: request.signal, cache: "no-store" });
      if (!request.isCurrent()) return;
      setOpened((current) => current?.id === entry.id ? null : current);
      void queryClient.invalidateQueries({ queryKey });
      toast.success("Analysis deleted.");
    } catch {
      if (request.isCurrent()) setActionError("Could not delete this analysis. Refresh the list before trying again.");
    } finally {
      if (request.isCurrent()) { request.finish(); setBusyId(null); }
    }
  }

  function confirmDelete(entry: AnalysisListEntry) {
    if (requests.active || !dialog.current) return;
    setPendingDelete(entry);
    dialog.current.returnValue = "";
    if (!dialog.current.open) dialog.current.showModal();
  }

  return (
    <>
      <details className="rounded-xl border border-slate-200 bg-white" onToggle={(event) => {
        const isOpen = event.currentTarget.open;
        setExpanded(isOpen);
        if (!isOpen) { requests.cancel(); setBusyId(null); setOpened(null); setActionError(null); }
      }}>
        <summary className="cursor-pointer px-4 py-3 text-sm font-semibold text-slate-800">
          <FileText className="mr-2 inline h-4 w-4" aria-hidden="true" /> My analyses
        </summary>
        <div className="space-y-3 border-t border-slate-100 p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-xs text-slate-600">Only your runs in this workspace. Case results are off by default; audit-only runs can still be opened or deleted.</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void refetch()} disabled={isFetching}>
              <RefreshCw className="mr-1 h-3 w-3" aria-hidden="true" /> Refresh analyses
            </Button>
          </div>
          {isFetching && <progress aria-label="Loading your analyses" className="w-28 accent-indigo-600" />}
          {error && <p role="alert" className="text-sm text-rose-700">Could not load your analyses. Try refreshing.</p>}
          {data?.length === 0 && <p className="text-sm text-slate-500">No analyses yet.</p>}
          {data && data.length > 0 && (
            <ul role="list" className="max-h-72 space-y-2 overflow-y-auto">
              {data.map((entry) => (
                <li key={entry.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-slate-200 p-3">
                  <div className="space-y-1 text-xs text-slate-600">
                    <time dateTime={entry.createdAt}>{entry.createdAt}</time>
                    <p>Configuration {entry.configVersion ?? "legacy"} · <Badge variant="secondary">{entry.hasResult ? "Saved content" : "Audit only"}</Badge></p>
                  </div>
                  <div className="flex gap-2">
                    <Button type="button" size="sm" variant="outline" disabled={busyId !== null} onClick={() => void openAnalysis(entry)} aria-label={`Open analysis ${entry.createdAt}`}>Open</Button>
                    <Button type="button" size="sm" variant="destructive" disabled={busyId !== null} onClick={() => confirmDelete(entry)} aria-label={`Delete analysis ${entry.createdAt}`}>
                      <Trash2 className="mr-1 h-3 w-3" aria-hidden="true" /> Delete
                    </Button>
                  </div>
                </li>
              ))}
            </ul>
          )}
          {busyId && <progress aria-label="Opening or deleting an analysis" className="w-28 accent-indigo-600" />}
          {actionError && <p role="alert" className="text-sm text-rose-700">{actionError}</p>}
          {opened && (
            <div className="space-y-3 rounded-lg border border-indigo-200 bg-indigo-50/20 p-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="text-base font-semibold text-slate-900">Saved analysis</h2>
                <Button type="button" variant="outline" size="sm" onClick={() => setOpened(null)}>Close saved result</Button>
              </div>
              <p className="text-xs text-slate-600">Opening a saved analysis does not add it to this conversation. Patient-specific checks and context are not saved; rerun a case before applying a saved statement.</p>
              {opened.result ? (
                <div className="max-h-[60dvh] overflow-y-auto"><AnalysisResult result={opened.result} configVersion={opened.configVersion} showPatientChecks={false} /></div>
              ) : (
                <p className="text-sm text-slate-600">No verified result is stored for this run. Result storage was disabled, the analysis did not complete, or legacy unverified content was withheld.</p>
              )}
            </div>
          )}
        </div>
      </details>

      <dialog ref={dialog} className={`${styles.confirmation} m-auto max-h-[80dvh] w-[min(28rem,calc(100%-2rem))] overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 text-slate-900 shadow-xl`} aria-labelledby={dialogTitleId} aria-describedby={dialogDescriptionId} onClose={(event) => {
        const choice = event.currentTarget.returnValue;
        event.currentTarget.returnValue = "";
        const entry = pendingDelete;
        setPendingDelete(null);
        if (choice === "delete" && entry) void deleteAnalysis(entry);
      }}>
        <h2 id={dialogTitleId} className="text-lg font-semibold">Delete this analysis?</h2>
        <p id={dialogDescriptionId} className="mt-2 text-sm text-slate-600">Delete the audit row and any stored result for the run from {pendingDelete?.createdAt}. This cannot be undone.</p>
        <form method="dialog" className="mt-5 flex justify-end gap-2">
          <Button type="submit" value="cancel" variant="outline" autoFocus>Cancel</Button>
          <Button type="submit" value="delete" variant="destructive">Delete analysis</Button>
        </form>
      </dialog>
    </>
  );
}
