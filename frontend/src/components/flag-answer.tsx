"use client";

import { useState } from "react";
import { useParams } from "next/navigation";
import { api } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

export const FLAG_REASONS = [
  { value: "incorrect", label: "Incorrect statement" },
  { value: "unsafe", label: "Potentially unsafe" },
  { value: "outdated", label: "Outdated guideline" },
  { value: "missing-context", label: "Missing important context" },
  { value: "citation-mismatch", label: "Citation mismatch" },
  { value: "other", label: "Other" },
] as const;

export function FlagAnswer({ runId, configVersion }: { runId: string; configVersion?: number | null }) {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const [reason, setReason] = useState<string>("incorrect");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");

  async function submit() {
    setBusy(true);
    setMessage("");
    try {
      await api(`/api/workspaces/${workspaceId}/analyses/${runId}/flag`, {
        method: "POST", body: JSON.stringify({ reason, note: note.trim() || null }),
      });
      setMessage("Flag recorded for owner review. No case text was stored with it.");
      setNote("");
    } catch {
      setMessage("The flag could not be recorded. Try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="rounded-lg border border-slate-200 bg-slate-50 p-3">
      <summary className="cursor-pointer text-sm font-medium text-slate-800">Flag this answer for governance review</summary>
      <p className="mt-2 text-xs text-slate-600">Stores the run ID, configuration version and reason category. Do not include patient or case text in the optional note.</p>
      <div className="mt-3 space-y-3">
        <div className="space-y-1">
          <Label htmlFor={`flag-reason-${runId}`}>Reason category</Label>
          <select id={`flag-reason-${runId}`} value={reason} onChange={event => setReason(event.target.value)}
            className="min-h-11 w-full rounded-md border border-slate-300 bg-white px-3 text-sm">
            {FLAG_REASONS.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <Label htmlFor={`flag-note-${runId}`}>Optional note (no case text)</Label>
          <Textarea id={`flag-note-${runId}`} value={note} maxLength={1000} rows={2}
            onChange={event => setNote(event.target.value)} />
        </div>
        <p className="text-xs text-slate-500">Configuration version: {configVersion ?? "not recorded"}</p>
        <Button type="button" variant="outline" className="min-h-11" disabled={busy} onClick={() => void submit()}>Record flag</Button>
        {message && <p role="status" className="text-sm text-slate-700">{message}</p>}
      </div>
    </details>
  );
}
