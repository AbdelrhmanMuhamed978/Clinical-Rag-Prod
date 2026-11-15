"use client";
import { useEffect, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import { get } from "@/lib/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { clearWorkspaceQueries } from "@/lib/workspace-management";
import { createRequestScope } from "@/lib/request-scope";
import type { RuleAccess } from "@/lib/types";
import { useAuth } from "@/providers/AuthProvider";
import { WorkspaceMemberRoles } from "@/components/rules/workspace-member-roles";
import { WorkspaceAdministration } from "@/components/workspace-administration";
import { ColabSettings } from "@/components/operations/colab-settings";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

export default function SettingsPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const { user } = useAuth();
  return <SettingsWorkspace key={`${workspaceId}:${user?.uid}`} workspaceId={workspaceId} userId={user?.uid ?? ""} />;
}

function SettingsWorkspace({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const router = useRouter();
  const qc = useQueryClient();
  const [exits] = useState(createRequestScope);
  useEffect(() => () => exits.cancel(), [exits]);
  const access = useQuery<RuleAccess>({ queryKey: ["rules", workspaceId, userId, "access"], enabled: !!userId,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/rules/access`, { signal }) });
  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Settings</h1>
      <ColabSettings key={userId} userId={userId} />
      <Card>
        <CardHeader><CardTitle>ASP.NET backend</CardTitle></CardHeader>
        <CardContent className="space-y-2">
          <Label htmlFor="settings-api-base">API base</Label>
          <Input id="settings-api-base" name="apiBase" value={process.env.NEXT_PUBLIC_API_BASE || "http://localhost:5100"} readOnly className="min-h-12 text-base!" />
        </CardContent>
      </Card>
      {access.isError && <p role="alert">Workspace permissions could not be verified. Member administration stays disabled.</p>}
      <WorkspaceMemberRoles workspaceId={workspaceId} userId={userId} canAdmin={!access.isError && access.data?.role === "owner"}
        onRemoved={async memberId => {
          if (memberId !== access.data?.userId) return false;
          const exit = exits.start();
          try {
            await clearWorkspaceQueries(qc, workspaceId);
            if (exit.isCurrent()) router.replace("/workspaces");
            return true;
          } finally { if (exit.isCurrent()) exit.finish(); }
        }} />
      <WorkspaceAdministration workspaceId={workspaceId} userId={userId} role={access.isError ? undefined : access.data?.role} />
    </div>
  );
}
