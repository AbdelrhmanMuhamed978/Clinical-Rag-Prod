"use client";

import { useEffect, useId, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import styles from "./upload-dialog.module.css";

export type GuidelineUpload = {
  file: File; society: string; title?: string; version?: string; year: number;
  guidelineStatus: "final" | "draft" | "superseded";
  effectiveDate?: string; reviewByDate?: string; sourceUrl?: string; permissionNote?: string;
};

export function UploadDialog({ initialFile, busy, error, onUpload, onDismiss }: {
  initialFile: File | null; busy: boolean; error: string | null;
  onUpload: (value: GuidelineUpload) => void; onDismiss: () => void;
}) {
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const [file, setFile] = useState(initialFile);
  const [society, setSociety] = useState("");
  const [title, setTitle] = useState("");
  const [version, setVersion] = useState("");
  const [year, setYear] = useState("");
  const [guidelineStatus, setGuidelineStatus] = useState<"final" | "draft" | "superseded">("draft");
  const [effectiveDate, setEffectiveDate] = useState("");
  const [reviewByDate, setReviewByDate] = useState("");
  const [sourceUrl, setSourceUrl] = useState("");
  const [permissionNote, setPermissionNote] = useState("");
  const [fileError, setFileError] = useState<string | null>(null);
  useEffect(() => { if (!dialog.current?.open) dialog.current?.showModal(); }, []);

  return <dialog ref={dialog} aria-labelledby={`${id}-title`} aria-describedby={`${id}-hint`}
    className={`${styles.dialog} m-auto max-h-[85dvh] w-[min(32rem,90vw)] overflow-auto rounded-xl border bg-white p-6 text-slate-900 shadow-xl`}
    onClose={onDismiss}>
    <h2 id={`${id}-title`} className="text-lg font-semibold">Upload guideline</h2>
    <p id={`${id}-hint`} className="mt-2 text-sm text-slate-600">Upload starts parsing only. You choose pages, review chunks and embed accepted chunks afterward. All metadata fields are required.</p>
    <form method="post" encType="multipart/form-data" aria-labelledby={`${id}-title`} className="mt-5 space-y-4" onSubmit={event => {
      event.preventDefault();
      if (busy) return;
      if (!file || !file.name.toLowerCase().endsWith(".pdf") || file.size === 0 || file.size > 50 * 1024 * 1024) {
        setFileError("Choose a non-empty PDF of at most 50 MB.");
        event.currentTarget.querySelector<HTMLInputElement>('input[type="file"]')?.focus();
        return;
      }
      if (!society.trim()) {
        event.currentTarget.querySelector<HTMLInputElement>('input[name="society"]')?.focus();
        return;
      }
      if (sourceUrl.trim() && !/^https?:\/\//i.test(sourceUrl.trim())) {
        setFileError("Source URL must start with http:// or https://.");
        return;
      }
      onUpload({
        file, society: society.trim(), title: title.trim() || undefined, version: version.trim() || undefined,
        year: Number(year), guidelineStatus,
        effectiveDate: effectiveDate || undefined, reviewByDate: reviewByDate || undefined,
        sourceUrl: sourceUrl.trim() || undefined, permissionNote: permissionNote.trim() || undefined,
      });
    }}>
      <div className="space-y-1">
        <Label htmlFor={`${id}-file`}>PDF (required, up to 50 MB)</Label>
        {file && <p className="break-words text-sm text-slate-600">Selected: {file.name}</p>}
        <Input id={`${id}-file`} name="file" type="file" accept="application/pdf,.pdf" required={!file} disabled={busy}
          aria-invalid={fileError ? true : undefined} aria-describedby={fileError ? `${id}-file-error` : undefined}
          className="min-h-12 text-base" autoFocus={!initialFile} onChange={event => {
            setFile(event.target.files?.[0] ?? null); setFileError(null);
          }} />
        {fileError && <p id={`${id}-file-error`} role="alert" className="text-sm text-rose-700">{fileError}</p>}
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-society`}>Publishing society / organization (required)</Label>
        <Input id={`${id}-society`} name="society" required maxLength={256} pattern={".*\\S.*"} value={society} disabled={busy}
          autoFocus={!!initialFile} onChange={event => setSociety(event.target.value)} className="min-h-12 text-base" />
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-title`}>Guideline title (optional; defaults to filename)</Label>
        <Input id={`${id}-title`} name="title" maxLength={512} value={title} disabled={busy}
          onChange={event => setTitle(event.target.value)} className="min-h-12 text-base" />
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-version`}>Version (optional)</Label>
        <Input id={`${id}-version`} name="version" maxLength={64} value={version} disabled={busy}
          onChange={event => setVersion(event.target.value)} className="min-h-12 text-base" />
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-year`}>Guideline year (required, 1900–2100)</Label>
        <Input id={`${id}-year`} name="year" type="number" required min={1900} max={2100} step={1}
          value={year} disabled={busy} onChange={event => setYear(event.target.value)} className="min-h-12 text-base" />
      </div>
      <fieldset disabled={busy} className="space-y-2">
        <legend className="text-sm font-medium">Guideline status (required)</legend>
        <div className="flex flex-wrap gap-4">
          {(["draft", "final", "superseded"] as const).map(status => <div key={status} className="flex min-h-12 items-center gap-2">
            <input id={`${id}-${status}`} name="guidelineStatus" type="radio" value={status} checked={guidelineStatus === status}
              required className="h-4 w-4 accent-indigo-600" onChange={() => setGuidelineStatus(status)} />
            <Label htmlFor={`${id}-${status}`}>{status.toUpperCase()}</Label>
          </div>)}
        </div>
        <p className="text-xs text-slate-600">Choose FINAL only for a final publication. DRAFT and SUPERSEDED citations retain visible warnings; superseded sources are excluded unless an owner opts in.</p>
      </fieldset>
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <Label htmlFor={`${id}-effective`}>Effective date (optional)</Label>
          <Input id={`${id}-effective`} name="effectiveDate" type="date" value={effectiveDate} disabled={busy}
            onChange={event => setEffectiveDate(event.target.value)} className="min-h-12 text-base" />
        </div>
        <div className="space-y-1">
          <Label htmlFor={`${id}-review-by`}>Review-by date (optional)</Label>
          <Input id={`${id}-review-by`} name="reviewByDate" type="date" value={reviewByDate} disabled={busy}
            onChange={event => setReviewByDate(event.target.value)} className="min-h-12 text-base" />
        </div>
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-source-url`}>Source URL (optional)</Label>
        <Input id={`${id}-source-url`} name="sourceUrl" type="url" maxLength={2048} value={sourceUrl} disabled={busy}
          onChange={event => setSourceUrl(event.target.value)} className="min-h-12 text-base" placeholder="https://..." />
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${id}-permission`}>Permission / licence note (optional)</Label>
        <Input id={`${id}-permission`} name="permissionNote" maxLength={1024} value={permissionNote} disabled={busy}
          onChange={event => setPermissionNote(event.target.value)} className="min-h-12 text-base" />
      </div>
      {error && <p role="alert" className="text-sm text-rose-700">{error}</p>}
      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="outline" className="min-h-12" onClick={() => dialog.current?.close()}>Cancel{busy ? " upload" : ""}</Button>
        <Button type="submit" className="min-h-12" disabled={busy}>{busy ? "Uploading…" : "Upload and parse"}</Button>
      </div>
    </form>
  </dialog>;
}
