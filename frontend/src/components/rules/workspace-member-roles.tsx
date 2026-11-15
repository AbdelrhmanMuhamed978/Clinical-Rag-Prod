"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, get, patch } from "@/lib/api";
import { configActionErrors } from "@/lib/workflow-config";
import type { WorkspaceMember } from "@/lib/types";
import { useUnsavedChanges } from "@/components/navigation-guard";
import { Button } from "@/components/ui/button";
import { ConfirmDialog, type Confirmation } from "./confirm-action";
import { createRequestScope } from "@/lib/request-scope";

const roles = [
  { value: "viewer", label: "Viewer", description: "Read workspace resources; no definition edits, review or administration." },
  { value: "editor", label: "Editor", description: "Edit definitions, recognition and configuration drafts; cannot approve rules or administer membership." },
  { value: "reviewer", label: "Reviewer", description: "Read, approve, retire and toggle rules; cannot edit definitions, recognition or workspace administration." },
  { value: "owner", label: "Owner", description: "Full workspace administration, publication, editing and review. At least one owner must remain." },
] as const;
type Submission = { userId: string; role: string; expectedRole: string };
type Editor = { savedRole: string; selectedRole: string };

type RemovalCallback = (memberId: string) => boolean | Promise<boolean>;
export function WorkspaceMemberRoles(props: { workspaceId: string; userId: string; canAdmin: boolean; onRemoved?: RemovalCallback }) {
  return <MemberRolesWorkspace key={`${props.workspaceId}:${props.userId}`} {...props} />;
}

function MemberRolesWorkspace({ workspaceId, userId, canAdmin, onRemoved }: { workspaceId: string; userId: string; canAdmin: boolean; onRemoved?: RemovalCallback }) {
  const id = useId();
  const query = useQuery<WorkspaceMember[]>({ queryKey: ["workspace-members", workspaceId, userId], enabled: !!userId && canAdmin,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/members`, { signal }) });
  return <section className="space-y-4 rounded-xl border bg-card p-5" aria-labelledby={`${id}-heading`}>
    <h2 id={`${id}-heading`} className="text-xl font-semibold">Workspace members</h2>
    <p className="text-sm text-muted-foreground">Owners may change or remove member access. Reviewer remains a separate review capability, not an administrative or editing role. Assign another owner before removing or demoting the last owner.</p>
    {!canAdmin && <p className="text-sm">Owner permission must be verified before loading or changing member access.</p>}
    {canAdmin && query.isPending && <p>Loading existing workspace members…</p>}
    {canAdmin && query.isError && <div className="space-y-2"><p role="alert">Member access could not be refreshed. Existing editor snapshots are kept and saves are disabled.</p>
      <Button type="button" variant="outline" className="min-h-12" disabled={!canAdmin} onClick={() => void query.refetch()}>Retry members</Button></div>}
    {query.data?.map(member => <MemberRoleForm key={member.userId} member={member} workspaceId={workspaceId} userId={userId}
      visible={canAdmin} canAdmin={canAdmin && !query.isError} lastOwner={member.role === "owner" && query.data.filter(item => item.role === "owner").length < 2}
      onRemoved={onRemoved} onRefresh={() => void query.refetch()} />)}
    {canAdmin && query.data?.length === 0 && <p>No existing members are available for provisioning.</p>}
  </section>;
}

function MemberRoleForm({ member, workspaceId, userId, visible, canAdmin, lastOwner, onRemoved, onRefresh }: {
  member: WorkspaceMember; workspaceId: string; userId: string; visible: boolean; canAdmin: boolean; lastOwner: boolean;
  onRemoved?: RemovalCallback; onRefresh: () => void;
}) {
  const id = useId();
  const qc = useQueryClient();
  const mounted = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  const [editor, setEditor] = useState<Editor>({ savedRole: member.role, selectedRole: member.role });
  const [notice, setNotice] = useState("");
  const [validation, setValidation] = useState("");
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [requests] = useState(createRequestScope);
  const [removing, setRemoving] = useState(false);
  useEffect(() => () => requests.cancel(), [requests]);
  useEffect(() => { if (!canAdmin) requests.cancel(); }, [canAdmin, requests]);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const dirty = editor.selectedRole !== editor.savedRole;
  useUnsavedChanges(dirty);
  const save = useMutation({
    mutationFn: async (submitted: Submission) => {
      const request = requests.start();
      try {
        const saved = await patch<Pick<WorkspaceMember, "userId" | "role">>(
          `/api/workspaces/${workspaceId}/members/${submitted.userId}/role`, { role: submitted.role, expectedRole: submitted.expectedRole }, { signal: request.signal });
        if (!request.isCurrent()) return null;
        if (saved?.userId !== submitted.userId || saved.role !== submitted.role) throw new Error("Member role was not confirmed.");
        return saved;
      } catch (cause) { if (request.isCurrent()) throw cause; return null; }
      finally { if (request.isCurrent()) request.finish(); }
    },
    onSuccess: (saved, submitted) => {
      if (!mounted.current || !saved || saved.userId !== member.userId) return;
      setEditor(current => current.savedRole !== submitted.expectedRole ? current : {
        savedRole: saved.role, selectedRole: current.selectedRole === submitted.role ? saved.role : current.selectedRole,
      });
      qc.setQueryData<WorkspaceMember[]>(["workspace-members", workspaceId, userId], previous => previous?.map(item => item.userId === saved.userId ? { ...item, role: saved.role } : item));
      void qc.invalidateQueries({ queryKey: ["workspace-members", workspaceId, userId] });
      void qc.invalidateQueries({ queryKey: ["rules", workspaceId] });
      void qc.invalidateQueries({ queryKey: ["config", workspaceId] });
      setValidation(""); setNotice(`Saved ${saved.role} access for ${member.email}. Rule definitions and approvals were not changed.`);
    },
  });
  if (!dirty && !save.isPending && editor.savedRole !== member.role) setEditor({ savedRole: member.role, selectedRole: member.role });
  const stale = editor.savedRole !== member.role;
  const messages = [...(validation ? [validation] : []), ...(save.error ? configActionErrors(save.error, "member access") : [])];
  useEffect(() => { if (validation || save.error) errorSummary.current?.focus(); }, [validation, save.error]);

  if (!canAdmin && confirmation) setConfirmation(null);
  if (!canAdmin && removing) setRemoving(false);
  // Keep the scoped draft in memory, but remove member details and pending dialogs from the DOM.
  if (!visible) return null;

  return <section className="space-y-3 rounded-lg border p-4" aria-labelledby={`${id}-heading`}>
    <h3 id={`${id}-heading`} className="break-all font-semibold">{member.email}</h3>
    <p className="break-all text-sm">Member: {member.userId} · Saved role: {editor.savedRole}</p>
    {dirty && <p className="text-sm font-medium">Unsaved member-role change</p>}
    {stale && <p role="alert" className="text-sm">Member access changed remotely. Your selection is kept; reload the saved role before trying again.</p>}
    {lastOwner && <p className="text-sm">Last workspace owner — assign another owner before demoting or removing this member.</p>}
    <>
      <form method="post" action={`/api/workspaces/${workspaceId}/members/${member.userId}/role`} aria-labelledby={`${id}-heading`} noValidate
        className="space-y-3" onSubmit={event => {
          event.preventDefault();
            if (!canAdmin || stale || save.isPending || requests.active || removing || (lastOwner && editor.selectedRole !== "owner")) return;
          if (!dirty) { setNotice("No access changes to save."); return; }
           if (!roles.some(role => role.value === editor.selectedRole)) { setValidation("Choose viewer, editor, reviewer or owner access."); return; }
          const submitted: Submission = { userId: member.userId, role: editor.selectedRole, expectedRole: editor.savedRole };
          setConfirmation({ title: `Change member access to ${submitted.role}?`, confirmLabel: "Save member access",
            description: `Change ${member.email} from ${submitted.expectedRole} to ${submitted.role} in this workspace only. Reviewer access permits clinical rule review but does not permit editing or administration. The change is audited.`,
            onConfirm: () => save.mutate(submitted) });
        }}>
        <fieldset disabled={!canAdmin} className="space-y-2"><legend className="font-medium">New role for {member.email}</legend>
          {roles.map(role => <div key={role.value} className="flex min-h-12 items-start gap-3 rounded border px-3">
            <input id={`${id}-${role.value}`} name={`${id}-role`} type="radio" required value={role.value} checked={editor.selectedRole === role.value}
               disabled={removing || (lastOwner && role.value !== "owner")}
               className="my-4 size-5 shrink-0 accent-primary" onChange={() => { setEditor({ ...editor, selectedRole: role.value }); setValidation(""); setNotice(""); if (save.isError) save.reset(); }} />
            <label htmlFor={`${id}-${role.value}`} className="min-h-12 flex-1 cursor-pointer py-3"><span className="font-medium">{role.label}</span><span className="block text-sm text-muted-foreground">{role.description}</span></label>
          </div>)}
        </fieldset>
        {!!messages.length && <div ref={errorSummary} tabIndex={-1} role="alert" className="rounded border border-destructive p-3 text-sm">{messages.map(message => <p key={message}>{message}</p>)}</div>}
         <Button type="submit" className="min-h-12" disabled={!canAdmin || stale || save.isPending || removing || lastOwner}>Save member role</Button>
      </form>
      <Button type="button" variant="outline" className="min-h-12" disabled={save.isPending || !canAdmin} onClick={() => {
        if (dirty && !window.confirm("Discard the unsaved member-role selection and reload saved access?")) return;
        setEditor({ savedRole: member.role, selectedRole: member.role }); setValidation(""); save.reset(); setNotice("Saved member role reloaded."); onRefresh();
      }}>Reload member role</Button>
    </>
    <Button type="button" variant="destructive" className="min-h-12" disabled={!canAdmin || stale || save.isPending || removing || lastOwner}
      onClick={() => setConfirmation({ title: `Remove ${member.email}?`, confirmLabel: "Remove member",
        description: "Remove this member's access to this workspace only. Their account and audit history remain. The last owner cannot be removed.",
        onConfirm: () => { void (async () => {
          if (requests.active || !canAdmin || lastOwner || stale) return;
          const request = requests.start(); setRemoving(true); setNotice("");
          try {
            const result = await api<{ removed: boolean }>(`/api/workspaces/${workspaceId}/members/${member.userId}?expectedRole=${encodeURIComponent(editor.savedRole)}`, { method: "DELETE", signal: request.signal });
            if (!request.isCurrent()) return;
            if (result?.removed !== true) throw new Error("Removal was not confirmed.");
             const exited = await onRemoved?.(member.userId);
             if (!request.isCurrent() || exited) return;
            void qc.invalidateQueries({ queryKey: ["workspace-members", workspaceId, userId] });
            void qc.invalidateQueries({ queryKey: ["rules", workspaceId] });
            setNotice("Member removed.");
          } catch (cause) { if (request.isCurrent()) setValidation(configActionErrors(cause, "member access").join(" ")); }
          finally { if (request.isCurrent()) { request.finish(); setRemoving(false); } }
        })(); } })}>Remove member</Button>
    {canAdmin && <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} />}
    <p role="status" className="text-sm text-muted-foreground">{notice}</p>
  </section>;
}
