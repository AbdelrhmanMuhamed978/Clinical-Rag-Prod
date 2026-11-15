"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, api, get } from "@/lib/api";
import { createRequestScope } from "@/lib/request-scope";
import { operationalSettingError, type ColabOperationalStatus, type OperationalConnectionStatus, type PlatformAdminAccess } from "@/lib/operations";
import { useUnsavedChanges } from "@/components/navigation-guard";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

const settingsKey = (userId: string) => ["operations", userId, "colab-settings"];
const statusKey = (userId: string) => ["operations", userId, "colab-status"];
const accessKey = (userId: string) => ["operations", userId, "admin-access"];

function Heartbeat({ value, stale }: { value: string | null; stale: boolean }) {
  if (!value || !Number.isFinite(Date.parse(value))) return <>Not received</>;
  return <><time dateTime={value}>{new Date(value).toLocaleString()}</time>{stale ? " (stale)" : " (recent)"}</>;
}

export function ColabSettings({ userId }: { userId: string }) {
  const access = useQuery<PlatformAdminAccess>({ queryKey: accessKey(userId), enabled: !!userId, retry: false,
    queryFn: ({ signal }) => get("/api/admin/access", { signal, cache: "no-store" }) });
  const canAdmin = !access.isError && access.data?.isAdmin === true;
  const health = useQuery<ColabOperationalStatus>({ queryKey: statusKey(userId), enabled: !!userId, retry: false,
    refetchInterval: 30000, queryFn: ({ signal }) => get("/api/operations/colab", { signal, cache: "no-store" }) });
  const settings = useQuery<OperationalConnectionStatus>({ queryKey: settingsKey(userId), enabled: !!userId && canAdmin,
    retry: false, queryFn: ({ signal }) => get("/api/admin/colab-settings", { signal, cache: "no-store" }) });
  const denied = settings.error instanceof ApiError && (settings.error.status === 401 || settings.error.status === 403);
  const status = health.data;

  return <Card>
    <CardHeader><CardTitle><h2>Colab operations</h2></CardTitle></CardHeader>
    <CardContent className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm">Live status: <strong>{health.isError ? "Status unavailable" : !status ? "Checking" : status.model.ready ? "Ready" : "Offline or not ready"}</strong></p>
        <Button type="button" variant="outline" className="min-h-12" disabled={!userId || health.isFetching || settings.isFetching}
          onClick={() => { void health.refetch(); if (canAdmin) void settings.refetch(); if (access.isError) void access.refetch(); }}>Refresh status</Button>
      </div>
      {health.isError && <p role="alert">Live model status could not be verified. Previously fetched details may be out of date.</p>}
      <dl className="grid gap-x-4 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
        <dt>Model</dt><dd className="break-words">{status?.model.model ?? "Unavailable"}</dd>
        <dt>Embedding model</dt><dd className="break-words">{status?.model.embeddingModel ?? "Unavailable"}{status?.model.embeddingDimensions ? ` (${status.model.embeddingDimensions} dimensions)` : ""}</dd>
        <dt>GPU</dt><dd>{status?.model.gpu?.available ? status.model.gpu.name ?? "Available" : "Unavailable"}</dd>
        <dt>Last heartbeat</dt><dd><Heartbeat value={status?.connection.lastHeartbeatAt ?? null} stale={status?.connection.heartbeatStale ?? true} /></dd>
        <dt>Manual override</dt><dd>{status ? status.connection.manualOverride ? "Active — notebook registration blocked" : "Off" : "Unknown"}</dd>
      </dl>
      {access.isError && <p role="alert">Platform administration could not be verified. Connection editing stays disabled.</p>}
      {!canAdmin && !access.isPending && !access.isError && <p className="text-sm text-muted-foreground">Only provisioned platform administrators can change this connection. Workspace ownership does not grant platform administration.</p>}
      {canAdmin && settings.isError && <p role="alert">Connection settings could not be verified. Refresh status before editing.</p>}
      {canAdmin && settings.isPending && <p>Loading connection settings…</p>}
      {canAdmin && settings.data && !denied && <ConnectionEditor key={userId} userId={userId} setting={settings.data} verified={!settings.isError} />}
    </CardContent>
  </Card>;
}

type Draft = { baseUrl: string; apiKey: string; manualOverride: boolean; expectedRevision: number };

function ConnectionEditor({ userId, setting, verified }: { userId: string; setting: OperationalConnectionStatus; verified: boolean }) {
  const id = useId();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [requests] = useState(createRequestScope);
  useEffect(() => () => requests.cancel(), [requests]);
  const form = draft ?? { baseUrl: setting.baseUrl ?? "", apiKey: "", manualOverride: setting.manualOverride, expectedRevision: setting.revision };
  const dirty = draft !== null && (form.baseUrl !== (setting.baseUrl ?? "") || form.apiKey !== ""
    || form.manualOverride !== setting.manualOverride || form.expectedRevision !== setting.revision);
  useUnsavedChanges(dirty || pending);

  function change(value: Partial<Draft>) { setDraft({ ...form, ...value }); setError(null); setNotice(null); }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (requests.active || !verified || (!dirty && setting.configured)) return;
    const request = requests.start();
    setPending(true); setError(null); setNotice(null);
    try {
      const saved = await api<OperationalConnectionStatus>("/api/admin/colab-settings", {
        method: "PUT", signal: request.signal, body: JSON.stringify(form),
      });
      if (!request.isCurrent()) return;
      queryClient.setQueryData(settingsKey(userId), saved);
      setDraft(null); // The replacement key remains only in this form's memory and is cleared after confirmation.
      setNotice(saved.manualOverride ? "Connection saved. Notebook registration is blocked by manual override."
        : "Connection saved. Rerun notebook registration to resume automatic heartbeats.");
      void queryClient.invalidateQueries({ queryKey: statusKey(userId) });
    } catch (cause) {
      if (!request.isCurrent()) return;
      setError(operationalSettingError(cause));
      if (cause instanceof ApiError && cause.status === 403) void queryClient.invalidateQueries({ queryKey: accessKey(userId) });
    } finally {
      if (request.isCurrent()) setPending(false);
      request.finish();
    }
  }

  return <form method="post" aria-labelledby={`${id}-heading`} onSubmit={save} className="space-y-4 border-t pt-4">
    <h3 id={`${id}-heading`} className="font-semibold">Platform-wide connection</h3>
    <p className="text-sm text-muted-foreground">These changes affect every workspace. API keys are protected in the backend database and are never returned to the browser.</p>
    {form.expectedRevision !== setting.revision && <p role="alert">The connection changed while you were editing. Discard your changes to load the latest revision.</p>}
    <fieldset disabled={pending || !verified} className="space-y-4">
      <legend className="sr-only">Colab connection settings</legend>
      <div className="space-y-1">
        <Label htmlFor={`${id}-url`}>Colab base URL (required)</Label>
        <p id={`${id}-url-help`} className="text-sm text-muted-foreground">Public HTTPS origin only; no path, credentials or query string.</p>
        <Input id={`${id}-url`} name="baseUrl" type="url" required maxLength={2048} autoComplete="url" autoCorrect="off" autoCapitalize="none" spellCheck={false}
          aria-describedby={`${id}-url-help`} value={form.baseUrl} onChange={event => change({ baseUrl: event.target.value })} className="min-h-12 text-base!" />
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-key`}>Replacement API key{setting.hasApiKey ? " (optional)" : " (required)"}</Label>
        <p id={`${id}-key-help`} className="text-sm text-muted-foreground">16–4096 printable ASCII characters without spaces.{setting.hasApiKey ? " Leave blank to keep the current key." : " A key is required for the first connection."}</p>
        <Input id={`${id}-key`} name="apiKey" type="password" autoComplete="new-password" autoCorrect="off" autoCapitalize="none" spellCheck={false}
          required={!setting.hasApiKey} minLength={16} maxLength={4096} pattern="[\x21-\x7E]+" aria-describedby={`${id}-key-help`}
          value={form.apiKey} onChange={event => change({ apiKey: event.target.value })} className="min-h-12 text-base!" />
      </div>
      <div className="flex min-h-12 items-center gap-3">
        <input id={`${id}-override`} name="manualOverride" type="checkbox" checked={form.manualOverride}
          aria-describedby={`${id}-override-help`} onChange={event => change({ manualOverride: event.target.checked })} className="size-6 accent-primary" />
        <Label htmlFor={`${id}-override`} className="py-3">Manual override</Label>
      </div>
      <p id={`${id}-override-help`} className="text-sm text-muted-foreground">Override blocks notebook registration and heartbeats. After releasing it, rerun the notebook tunnel/registration cell; old heartbeats cannot restore the connection.</p>
      <div className="flex flex-wrap gap-3">
        <Button type="submit" className="min-h-12" disabled={pending || (!dirty && setting.configured)}>Save connection</Button>
        <Button type="button" variant="outline" className="min-h-12" disabled={!dirty || pending}
          onClick={() => { setDraft(null); setError(null); setNotice(null); }}>Discard changes</Button>
      </div>
    </fieldset>
    <div role="alert">{error}</div>
    <div role="status" aria-live="polite">{notice}</div>
  </form>;
}
