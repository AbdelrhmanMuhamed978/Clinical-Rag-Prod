"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, get } from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import type { Workspace } from "@/lib/types";

export function GovernancePolicy({ workspaceId, userId, canEdit }: { workspaceId: string; userId: string; canEdit: boolean }) {
  const workspace = useQuery<Workspace>({
    queryKey: ["workspace-governance", workspaceId, userId],
    enabled: canEdit && !!userId,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}`, { signal }),
  });
  const [allowOverride, setAllowOverride] = useState<boolean | null>(null);
  const allow = allowOverride ?? workspace.data?.allowSupersededGuidelines === true;
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  if (!canEdit) return null;
  async function save() {
    setBusy(true); setMessage("");
    try {
      await api(`/api/workspaces/${workspaceId}/guideline-policy`, { method: "PATCH", body: JSON.stringify({ allowSupersededGuidelines: allow }) });
      setMessage("Guideline policy saved. Superseded citations are excluded unless this owner opt-in is enabled.");
      await workspace.refetch();
    } catch { setMessage("The guideline policy could not be saved."); }
    finally { setBusy(false); }
  }
  return <Card>
    <CardHeader><CardTitle>Guideline policy</CardTitle></CardHeader>
    <CardContent className="space-y-3">
      <p className="text-sm text-slate-600">Superseded guidelines are excluded from retrieval by default. An owner may opt in for this workspace; affected citations remain labelled SUPERSEDED.</p>
      <label className="flex items-start gap-3 text-sm">
        <input type="checkbox" className="mt-1 size-5" checked={allow} disabled={busy || workspace.isPending} onChange={event => setAllowOverride(event.target.checked)} />
        <span>Allow superseded guidelines in retrieval</span>
      </label>
      <Button type="button" variant="outline" className="min-h-11" disabled={busy || workspace.isPending} onClick={() => void save()}>Save guideline policy</Button>
      {message && <p role="status" className="text-sm text-slate-700">{message}</p>}
    </CardContent>
  </Card>;
}
