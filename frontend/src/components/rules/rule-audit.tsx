"use client";

import { useId, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { get } from "@/lib/api";
import type { RuleAuditPage } from "@/lib/types";
import { Button } from "@/components/ui/button";

function formatted(value: string | null) {
  if (value == null) return "No snapshot (creation or deletion).";
  try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; }
}

type Props = {
  workspaceId: string; userId: string; ruleId: string | null; active: boolean; onWorkspaceHistory: () => void;
};

export function RuleAudit(props: Props) {
  return <ScopedRuleAudit key={JSON.stringify([props.workspaceId, props.userId, props.ruleId])} {...props} />;
}

function ScopedRuleAudit({ workspaceId, userId, ruleId, active, onWorkspaceHistory }: Props) {
  const id = useId();
  const [skip, setSkip] = useState(0);
  const query = useQuery<RuleAuditPage>({ queryKey: ["rules", workspaceId, userId, "audit", ruleId ?? "workspace", skip],
    enabled: active && !!userId, queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/rules/${ruleId ? `${ruleId}/` : ""}audit?skip=${skip}&take=25`, { signal }) });
  return <section className="space-y-4" aria-labelledby={`${id}-heading`}>
    <header className="flex flex-wrap items-center justify-between gap-3"><h2 id={`${id}-heading`} className="text-xl font-semibold">{ruleId ? "Rule audit history" : "Workspace rule and recognition audit"}</h2>
      {ruleId && <Button type="button" variant="outline" className="min-h-12" onClick={onWorkspaceHistory}>Show workspace history</Button>}
    </header>
    <p className="text-sm text-muted-foreground">Append-only saved definition, review, enablement, recognition and member-role evidence. Deleting a rule does not delete its history. Preview case inputs and regex samples are never added here.</p>
    {query.isPending ? <p>Loading audit history…</p> : query.isError ? <div className="space-y-2"><p role="alert">Could not load audit history.</p><Button type="button" onClick={() => void query.refetch()}>Retry history</Button></div>
      : !query.data?.entries.length ? <p>No audit entries on this page.</p> : <ol className="space-y-3">
        {query.data.entries.map(entry => <li key={entry.id} className="space-y-2 rounded-xl border bg-card p-4">
          <p className="font-medium">{entry.action.replaceAll("_", " ")}{entry.ruleKey ? ` — ${entry.ruleKey}` : ""}</p>
          <p className="break-all text-sm"><time dateTime={entry.createdAt}>{entry.createdAt}</time> · Actor: {entry.actorId ?? "migration / system"}</p>
          {(["beforeJson", "afterJson"] as const).map(key => <details key={key} className="text-sm"><summary className="cursor-pointer">{key === "beforeJson" ? "Before snapshot" : "After snapshot"}</summary>
            <pre className="mt-2 max-h-96 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3">{formatted(entry[key])}</pre>
          </details>)}
        </li>)}
      </ol>}
    <div className="flex flex-wrap items-center gap-3"><Button type="button" variant="outline" className="min-h-12" disabled={skip === 0 || query.isFetching} onClick={() => setSkip(value => Math.max(0, value - 25))}>Newer entries</Button>
      <p className="text-sm">Page {Math.floor(skip / 25) + 1}</p><Button type="button" variant="outline" className="min-h-12" disabled={!query.data?.hasMore || query.isFetching || query.isError}
        onClick={() => { if (query.data?.nextSkip != null) setSkip(query.data.nextSkip); }}>Older entries</Button>
    </div>
  </section>;
}
