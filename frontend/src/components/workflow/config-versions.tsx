"use client";

import { useMemo, useState } from "react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { boundConfigRun, configDifferences, parseWorkflowConfig, type JsonObject, type JsonValue } from "@/lib/workflow-config";
import type { ConfigVersionDetails } from "@/lib/types";

type Props = { versions: ConfigVersionDetails[]; selected: ConfigVersionDetails | undefined; published: JsonObject;
  busy: boolean; canEdit: boolean; isOwner: boolean; publishReason: string | null;
  onSelect: (id: string) => void; onLoad: () => void; onPublish: () => void; onDiscard: () => void; onRollback: () => void; onRunTests: () => void };
const display = (value: JsonValue | undefined) => value === undefined ? "(not set)" : JSON.stringify(value, null, 2);

export function ConfigVersions({ versions, selected, published, busy, canEdit, isOwner, publishReason,
  onSelect, onLoad, onPublish, onDiscard, onRollback, onRunTests }: Props) {
  const [showDifferences, setShowDifferences] = useState(false);
  const selectedJson = selected?.configJson;
  const parsed = useMemo(() => selectedJson ? parseWorkflowConfig(selectedJson) : null, [selectedJson]);
  const differences = useMemo(() => parsed ? configDifferences(published, parsed) : [], [published, parsed]);
  const run = boundConfigRun(selected);

  return <section className="space-y-4 rounded-xl border bg-card p-5">
    <h2 className="text-lg font-semibold">Versions &amp; drafts</h2>
    <p className="text-sm text-muted-foreground">Saved drafts are immutable. Editing a draft saves a new version; discarding keeps its history.</p>
    <div className="space-y-2">
      <Label htmlFor="workflow-version">Configuration version</Label>
      <select id="workflow-version" name="version" value={selected?.id ?? ""} disabled={busy || !versions.length}
        className="min-h-12 w-full rounded-lg border bg-background px-3 text-base focus-visible:outline-2 focus-visible:outline-offset-2"
        onChange={event => onSelect(event.target.value)}>
        {!versions.length && <option value="">No versions available</option>}
        {versions.map(version => <option key={version.id} value={version.id}>
          v{version.version} — {version.status} — {boundConfigRun(version)?.status ?? "no test run"}
        </option>)}
      </select>
    </div>
    {selected && <>
      <p className="text-sm">Latest test for v{selected.version}: <strong>{run?.status ?? "no test run"}</strong></p>
      <div className="flex flex-wrap gap-2">
        {selected.status === "draft" && <>
          <Button type="button" variant="outline" className="min-h-12" disabled={busy || !canEdit || !parsed} onClick={onLoad}>Load draft v{selected.version}</Button>
          <Button type="button" className="min-h-12" disabled={busy || publishReason !== null}
            aria-describedby="workflow-publish-reason" onClick={onPublish}>Publish draft v{selected.version}</Button>
          <Button type="button" variant="outline" className="min-h-12" disabled={busy || !canEdit} onClick={onRunTests}>Run tests on this draft</Button>
          <Button type="button" variant="destructive" className="min-h-12" disabled={busy || !canEdit} onClick={onDiscard}>Discard draft v{selected.version}</Button>
        </>}
        {(selected.status === "published" || selected.status === "archived") && <Button type="button" variant="outline"
          className="min-h-12" disabled={busy || !isOwner} onClick={onRollback}>Rollback v{selected.version} to draft</Button>}
      </div>
      <p id="workflow-publish-reason" className="text-sm text-muted-foreground">
        {publishReason === null ? "Latest real test passed; an owner can publish this exact draft." : `Publishing blocked: ${publishReason}`}
      </p>
      <p className="text-sm text-muted-foreground">Rollback creates a new draft, leaves published unchanged, and requires a fresh real test run.</p>
      <Button type="button" variant="outline" className="min-h-12" disabled={!parsed} aria-expanded={showDifferences}
        aria-controls="workflow-differences" onClick={() => setShowDifferences(value => !value)}>{showDifferences ? "Hide differences" : "Show differences"}</Button>
      {showDifferences && <div id="workflow-differences" data-testid="config-differences" className="space-y-3">
        <h3 className="font-medium">Differences from published</h3>
        {differences.length === 0 ? <p className="text-sm text-muted-foreground">No configuration differences.</p> : <ul className="space-y-4">
          {differences.map(change => <li key={change.path} className="space-y-2 rounded-lg border p-3">
            <code className="break-all font-semibold">{change.path}</code>
            <dl className="grid gap-3 sm:grid-cols-2">
              <div><dt className="text-sm font-medium">Published</dt><dd><pre className="max-h-48 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">{display(change.published)}</pre></dd></div>
              <div><dt className="text-sm font-medium">Selected v{selected.version}</dt><dd><pre className="max-h-48 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">{display(change.selected)}</pre></dd></div>
            </dl>
          </li>)}
        </ul>}
      </div>}
    </>}
  </section>;
}
