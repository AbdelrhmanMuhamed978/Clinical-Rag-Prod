"use client";

import { useEffect, useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, get } from "@/lib/api";
import { clearWorkspaceQueries, workspaceActionError } from "@/lib/workspace-management";
import { createRequestScope } from "@/lib/request-scope";
import type { Workspace } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ConfirmDialog, type Confirmation } from "@/components/rules/confirm-action";
import { toast } from "sonner";

type Invitation = { id: string; email: string; role: string };
export function WorkspaceAdministration(props: { workspaceId: string; userId: string; role?: string }) {
  return <Administration key={`${props.workspaceId}:${props.userId}:${props.role ?? "unverified"}`} {...props} />;
}

function Administration({ workspaceId, userId, role }: { workspaceId: string; userId: string; role?: string }) {
  const id = useId(); const qc = useQueryClient(); const router = useRouter();
  const [requests] = useState(createRequestScope);
  useEffect(() => () => requests.cancel(), [requests]);
  const [busy, setBusy] = useState(false); const [notice, setNotice] = useState(""); const [error, setError] = useState("");
  const [nameDraft, setName] = useState<string | null>(null);
  const [email, setEmail] = useState(""); const [inviteRole, setInviteRole] = useState("viewer");
  const [confirmation, setConfirmation] = useState<(Confirmation & { admin: boolean }) | null>(null);
  const canAdmin = role === "owner";
  const root = `/api/workspaces/${workspaceId}`;
  const workspaceKey = ["workspace", workspaceId, userId];
  const inviteKey = ["workspace-invitations", workspaceId, userId];
  const workspace = useQuery<Workspace>({ queryKey: workspaceKey, enabled: !!role && !!userId,
    queryFn: ({ signal }) => get(root, { signal, cache: "no-store" }), retry: false });
  const invitations = useQuery<Invitation[]>({ queryKey: inviteKey, enabled: canAdmin && !!userId,
    queryFn: ({ signal }) => get(`${root}/invitations`, { signal, cache: "no-store" }), retry: false });
  const blocked = busy || !role || !workspace.data || workspace.isError || (canAdmin && (invitations.isError || !invitations.data));
  if (confirmation && (!role || (confirmation.admin && !canAdmin))) setConfirmation(null);

  async function mutate(path: string, method: string, body: unknown, admin: boolean, apply: (value: unknown) => string, exit = false) {
    if (blocked || requests.active || (admin && !canAdmin)) return;
    const request = requests.start(); setBusy(true); setNotice(""); setError("");
    try {
      const result = await api(path, { method, body: body === undefined ? undefined : JSON.stringify(body), signal: request.signal });
      if (!request.isCurrent()) return;
      const message = apply(result); setNotice(message);
      if (exit) {
        toast.warning(message); // Keep cleanup warnings visible after navigation.
        // Drop workspace data before leaving; nothing from a deleted/left workspace should remain in a live view.
        await clearWorkspaceQueries(qc, workspaceId);
        if (!request.isCurrent()) return;
        router.replace("/workspaces");
      } else await Promise.all([qc.invalidateQueries({ queryKey: workspaceKey }), qc.invalidateQueries({ queryKey: inviteKey }), qc.invalidateQueries({ queryKey: ["workspaces"] })]);
    } catch (cause) { if (request.isCurrent()) setError(workspaceActionError(cause)); }
    finally { if (request.isCurrent()) { request.finish(); setBusy(false); } }
  }
  function confirm(title: string, description: string, label: string, path: string, method: string, admin: boolean, field: string, exit = false) {
    setConfirmation({ title, description, confirmLabel: label, admin, onConfirm: () => void mutate(path, method, undefined, admin, value => {
      if (typeof value !== "object" || value === null || !(field in value) || (value as Record<string, unknown>)[field] !== true) throw new Error("Missing completion evidence.");
      const warning = "warning" in value ? value.warning : null;
      if (typeof warning === "string") return warning;
      return `${label} completed.`;
    }, exit) });
  }
  return <section className="space-y-4 rounded-xl border bg-card p-5" aria-labelledby={`${id}-title`}>
    <h2 id={`${id}-title`} className="text-xl font-semibold">Workspace administration</h2>
    <p className="break-all text-sm">{workspaceId}</p>
    {(workspace.isError || (canAdmin && invitations.isError)) && <p role="alert">Workspace settings could not be refreshed. Changes stay disabled.</p>}
    {canAdmin && <>
      <form method="post" action={root} aria-labelledby={`${id}-rename`} className="space-y-2" onSubmit={event => {
        event.preventDefault(); const expectedName = workspace.data?.name; const name = (nameDraft ?? expectedName ?? "").trim();
        if (!expectedName || blocked) return;
        void mutate(root, "PATCH", { name, expectedName }, true, value => {
          const saved = value as Workspace; if (saved?.id !== workspaceId || saved.name !== name) throw new Error("Rename was not confirmed.");
          setName(current => current === name || current?.trim() === name ? null : current); return "Workspace renamed.";
        });
      }}>
        <h3 id={`${id}-rename`} className="font-semibold">Rename workspace</h3>
        <Label htmlFor={`${id}-name`}>Workspace name</Label>
        <Input id={`${id}-name`} name="name" className="min-h-12 text-base!" required maxLength={128} value={nameDraft ?? workspace.data?.name ?? ""} onChange={event => setName(event.target.value)} disabled={busy} />
        <Button type="submit" className="min-h-12" disabled={blocked}>Save workspace name</Button>
      </form>
      <form method="post" action={`${root}/invite`} aria-labelledby={`${id}-invite`} className="space-y-2" onSubmit={event => {
        event.preventDefault(); const submittedEmail = email.trim().toLowerCase(); const submittedRole = inviteRole;
        if (blocked) return;
        setConfirmation({ title: `Invite ${submittedEmail} as ${submittedRole}?`, confirmLabel: "Confirm invitation", admin: true,
          description: `Save pending ${submittedRole} access for ${submittedEmail}. ${submittedRole === "owner" ? "Owner grants full administration, publication and workspace deletion. " : ""}Access begins when the matching verified email signs in. No invitation email is sent.`,
          onConfirm: () => void mutate(`${root}/invite`, "POST", { email: submittedEmail, role: submittedRole }, true, value => {
          const saved = value as Invitation & { status: string };
          if (saved?.email !== submittedEmail || saved.role !== submittedRole || saved.status !== "pending") throw new Error("Invitation was not confirmed.");
          setEmail(current => current.trim().toLowerCase() === submittedEmail ? "" : current); return "Pending invitation saved. It is accepted when this verified email next signs in; no email is sent by this app.";
        }) });
      }}>
        <h3 id={`${id}-invite`} className="font-semibold">Invite member</h3>
        <p className="text-sm text-muted-foreground">Invitations grant access only after sign-in with the same verified email. Share the app URL with the invitee.</p>
        <Label htmlFor={`${id}-email`}>Invite email</Label>
        <Input id={`${id}-email`} name="email" className="min-h-12 text-base!" type="email" inputMode="email" autoComplete="email" autoCapitalize="none" spellCheck={false} required maxLength={256} value={email} onChange={event => setEmail(event.target.value)} disabled={busy} />
        <Label htmlFor={`${id}-role`}>Invitation role</Label>
        <select id={`${id}-role`} name="role" className="min-h-12 rounded border bg-background px-3 text-base" value={inviteRole} onChange={event => setInviteRole(event.target.value)} disabled={busy}>
          {['viewer', 'editor', 'reviewer', 'owner'].map(value => <option key={value} value={value}>{value}</option>)}
        </select>
        <p className="text-sm">Reviewer can approve rules but cannot edit or administer. Owner grants full administration, including deleting this workspace.</p>
        <Button type="submit" className="min-h-12" disabled={blocked}>Save invitation</Button>
      </form>
      <section aria-labelledby={`${id}-pending`}><h3 id={`${id}-pending`} className="font-semibold">Pending invitations</h3>
        {invitations.data?.map(invite => <div key={invite.id} className="my-2 flex flex-wrap items-center gap-3 rounded border p-3">
          <p className="break-all">{invite.email} · {invite.role} · pending</p>
          <Button type="button" className="min-h-12" variant="outline" disabled={blocked} onClick={() => confirm("Cancel pending invitation?", `Cancel access for ${invite.email}. If it was already accepted, refresh the member list instead.`, "Cancel invitation", `${root}/invitations/${invite.id}`, "DELETE", true, "cancelled")}>Cancel invitation</Button>
        </div>)}
        {invitations.data?.length === 0 && <p className="text-sm">No pending invitations.</p>}
      </section>
      <Button type="button" className="min-h-12" variant="destructive" disabled={blocked} onClick={() => confirm("Delete this workspace?", "Soft-delete this workspace and permanently delete its documents, chunks, guidance and uploaded PDFs. All workspace access ends. Audit and configuration history remain. This cannot be undone.", "Delete workspace", root, "DELETE", true, "deleted", true)}>Delete workspace</Button>
    </>}
    <Button type="button" className="min-h-12" variant="outline" disabled={blocked} onClick={() => confirm("Leave this workspace?", "Remove your access to this workspace. Your account and audit history remain. The last owner must assign another owner first.", "Leave workspace", `${root}/leave`, "POST", false, "removed", true)}>Leave workspace</Button>
    <Button type="button" className="min-h-12" variant="outline" disabled={busy || !role} onClick={() => { void workspace.refetch(); if (canAdmin) void invitations.refetch(); }}>Refresh workspace settings</Button>
    {error && <p role="alert">{error}</p>}
    <p role="status">{notice}</p>
    <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} />
  </section>;
}
