"use client";

import { useQuery } from "@tanstack/react-query";
import { get } from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import type { AnswerFlag } from "@/lib/types";

export function GovernanceFlags({ workspaceId, userId, canView }: { workspaceId: string; userId: string; canView: boolean }) {
  const flags = useQuery<{ flags: AnswerFlag[] }>({
    queryKey: ["governance-flags", workspaceId, userId],
    enabled: canView && !!userId,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/flags`, { signal }),
  });
  if (!canView) return null;
  const flagList = Array.isArray(flags.data?.flags) ? flags.data.flags : [];
  return (
    <Card>
      <CardHeader><CardTitle>Flagged answers / evaluation backlog</CardTitle></CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-slate-600">Owner-only. Flags contain run ID, configuration version and reason category; case text is not stored.</p>
        {flags.isPending && <p className="text-sm">Loading flags…</p>}
        {flags.isError && <p role="alert" className="text-sm text-rose-700">Could not load the evaluation backlog.</p>}
        {!flags.isPending && !flags.isError && flagList.length === 0 && <p className="text-sm text-slate-600">No flags recorded.</p>}
        {flagList.length > 0 && <ul className="space-y-2">
          {flagList.map(flag => <li key={flag.id} className="rounded-md border border-slate-200 p-3 text-sm">
            <p className="font-medium">{flag.reason.replaceAll("-", " ")} · config v{flag.configVersion ?? "unknown"}</p>
            <p className="text-xs text-slate-500">Run {flag.analysisRunId} · {new Date(flag.createdAt).toLocaleString()}</p>
            {flag.note && <p className="mt-1 text-slate-700">{flag.note}</p>}
          </li>)}
        </ul>}
      </CardContent>
    </Card>
  );
}
