"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useDropzone } from "react-dropzone";
import { FileText, Loader2, RefreshCw, Trash2, Upload } from "lucide-react";
import { toast } from "sonner";
import { api } from "@/lib/api";
import { canRetryParse, documentActionError, documentIsBusy, documentPollInterval, documentStatus, documentsQueryKey, DOCUMENT_POLL_MILLISECONDS } from "@/lib/documents";
import { createRequestScope } from "@/lib/request-scope";
import type { Document } from "@/lib/types";
import { useAuth } from "@/providers/AuthProvider";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { ConfirmDialog, type Confirmation } from "@/components/rules/confirm-action";
import { UploadDialog, type GuidelineUpload } from "@/components/documents/upload-dialog";

export default function DocumentsPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const { user } = useAuth();
  return user ? <Documents key={`${workspaceId}:${user.uid}`} workspaceId={workspaceId} userId={user.uid} /> : null;
}

function Documents({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const qc = useQueryClient();
  const queryKey = documentsQueryKey(workspaceId, userId);
  const [requests] = useState(createRequestScope);
  const [busy, setBusy] = useState<{ action: "upload" | "parse" | "delete"; id?: string } | null>(null);
  const [uploadDialog, setUploadDialog] = useState<{ file: File | null } | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [notice, setNotice] = useState("");
  useEffect(() => () => requests.cancel(), [requests]);
  const { data, isPending, isFetching, error, refetch } = useQuery<Document[]>({
    queryKey,
    queryFn: ({ signal }) => api(`/api/workspaces/${workspaceId}/documents`, { signal, cache: "no-store" }),
    refetchInterval: query => documentPollInterval(query.state.data) || (busy?.action === "parse" ? DOCUMENT_POLL_MILLISECONDS : false),
    retry: false,
  });

  async function uploadGuideline(value: GuidelineUpload) {
    if (requests.active) return;
    const request = requests.start();
    setBusy({ action: "upload" });
    setUploadError(null);
    try {
      const body = new FormData();
      body.append("file", value.file); body.append("society", value.society);
      body.append("year", String(value.year)); body.append("guidelineStatus", value.guidelineStatus);
      const doc = await api<Document>(`/api/workspaces/${workspaceId}/documents`, { method: "POST", body, signal: request.signal });
      if (!request.isCurrent()) return;
      qc.setQueryData<Document[]>(queryKey, previous => [doc, ...(previous ?? []).filter(d => d.id !== doc.id)]);
      setUploadDialog(null);
      // The persisted upload is the server's automatic-parse queue. Closing this
      // tab or losing the upload response cannot strand it before a second POST.
      setNotice("PDF uploaded. Server parsing is queued; this page will refresh its progress.");
      toast.success("PDF uploaded. Parsing will start automatically.");
    } catch (cause) {
      if (!request.isCurrent()) return;
      const message = documentActionError(cause, "The upload could not be confirmed. Refresh to check whether the PDF was saved before trying again.");
      setUploadError(message);
      setNotice(message); toast.error(message);
    } finally {
      if (request.isCurrent()) { request.finish(); setBusy(null); void qc.invalidateQueries({ queryKey }); }
    }
  }

  async function retryParse(doc: Document) {
    if (requests.active || !canRetryParse(doc)) return;
    const request = requests.start();
    setBusy({ action: "parse", id: doc.id });
    setNotice("Retry parsing requested.");
    try {
      await api(`/api/workspaces/${workspaceId}/documents/${doc.id}/parse`, { method: "POST", body: "{}", signal: request.signal });
      if (request.isCurrent()) { setNotice("PDF parsed. Choose pages next."); toast.success("PDF parsed."); }
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, "Parsing did not complete. Refresh to check the document status.");
        setNotice(message); toast.error(message);
      }
    } finally {
      if (request.isCurrent()) { request.finish(); setBusy(null); void qc.invalidateQueries({ queryKey }); }
    }
  }

  async function deleteDocument(doc: Document) {
    if (requests.active || documentIsBusy(doc)) return;
    const request = requests.start();
    setBusy({ action: "delete", id: doc.id });
    try {
      const result = await api<{ warning?: string | null }>(`/api/workspaces/${workspaceId}/documents/${doc.id}`, { method: "DELETE", signal: request.signal });
      if (!request.isCurrent()) return;
      qc.setQueryData<Document[]>(queryKey, previous => previous?.filter(d => d.id !== doc.id));
      void qc.removeQueries({ queryKey: ["document", workspaceId, doc.id] });
      void qc.removeQueries({ queryKey: ["chunks", workspaceId, doc.id] });
      setNotice(result.warning ?? "Document and all its chunks deleted.");
      if (result.warning) toast.warning(result.warning); else toast.success("Document and chunks deleted.");
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, "Could not delete the document. Refresh before trying again.");
        setNotice(message); toast.error(message);
      }
    } finally {
      if (request.isCurrent()) { request.finish(); setBusy(null); void qc.invalidateQueries({ queryKey }); }
    }
  }

  const { getRootProps, isDragActive } = useDropzone({
    accept: { "application/pdf": [".pdf"] }, multiple: false, maxSize: 50 * 1024 * 1024,
    noClick: true, noKeyboard: true, disabled: busy !== null,
    onDropAccepted: files => { if (files[0]) { setUploadError(null); setUploadDialog({ file: files[0] }); } },
    onDropRejected: () => toast.error("Choose one PDF of at most 50 MB."),
  });

  return <div className="space-y-6">
    <header className="flex flex-wrap items-center justify-between gap-3">
      <div><h1 className="text-2xl font-bold text-slate-900">Documents</h1>
        <p className="mt-1 text-sm text-slate-600">Upload -&gt; Parse -&gt; choose pages -&gt; Review -&gt; Embed</p></div>
      <Button type="button" onClick={() => void refetch()} variant="outline" className="min-h-12" disabled={isFetching}>
        <RefreshCw className="mr-1 h-4 w-4" aria-hidden="true" /> Refresh
      </Button>
    </header>
    <div {...getRootProps({ role: undefined, tabIndex: -1 })} className={`rounded-xl border-2 border-dashed p-8 text-center ${isDragActive ? "border-indigo-400 bg-indigo-50" : "border-slate-300"}`}>
      <Upload className="mx-auto mb-2 h-8 w-8 text-slate-500" aria-hidden="true" />
      <p className="mb-3 text-sm text-slate-600">{isDragActive ? "Drop one PDF here…" : "Drop one PDF here, or upload a guideline."}</p>
      <Button type="button" className="min-h-12" disabled={busy !== null} onClick={() => { setUploadError(null); setUploadDialog({ file: null }); }}>Upload guideline</Button>
      <p className="mt-3 text-xs text-slate-600">Parsing starts after upload. Chunking starts only when you choose pages and request it.</p>
    </div>
    <p role="status" className="text-sm text-slate-600">{notice}</p>
    {isPending && <p className="text-sm text-slate-600">Loading documents…</p>}
    {error && <p role="alert" className="text-sm text-rose-700">Could not refresh documents. Displayed statuses may be out of date; try Refresh.</p>}
    {data?.length === 0 && <p className="text-sm text-slate-600">No documents yet.</p>}
    <div className="grid gap-4">
      {data?.map(doc => {
        const status = documentStatus(doc);
        const processing = documentIsBusy(doc);
        const requested = busy?.id === doc.id && busy.action === "parse";
        const parsedLength = doc.parsedTextLength ?? doc.parsedText?.length ?? 0;
        const canReview = parsedLength > 0 || ["parsed", "review", "indexed", "chunking"].includes(doc.status);
        return <Card key={doc.id} className="border-slate-200 shadow-sm" role="group" aria-label={doc.fileName}>
          <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-3 p-4">
            <div className="flex min-w-0 items-center gap-3"><FileText className="h-5 w-5 shrink-0 text-slate-500" aria-hidden="true" />
              <h2 className="break-words font-medium text-slate-900">{doc.fileName}</h2></div>
            <Badge className={`px-2.5 py-1 text-xs ${status.color}`}>{status.label}</Badge>
          </CardHeader>
          <CardContent className="space-y-4 p-4">
            <div className="flex flex-wrap items-center gap-2 text-sm text-slate-600">
              <span>{doc.society || "Publishing society not recorded"}</span><span>· {doc.year ?? "Year not recorded"}</span>
              <Badge className={doc.guidelineStatus === "final" ? "bg-slate-100 text-slate-700" : "bg-amber-100 text-amber-800"}>{doc.guidelineStatus === "final" ? "FINAL" : "DRAFT"}</Badge>
              {doc.guidelineStatus !== "final" && doc.guidelineStatus !== "draft" && <span className="text-xs">Unknown publication status is treated as DRAFT.</span>}
            </div>
            <p className="text-sm text-slate-600">{doc.pageCount ?? "—"} pages · {parsedLength.toLocaleString()} parsed characters · {doc.chunkedPagesCount ?? 0} pages processed for chunking</p>
            {doc.chunkCounts ? <dl className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
              {([["Total chunks", "total"], ["Pending review", "pending"], ["Accepted (awaiting embedding)", "accepted"], ["Rejected", "rejected"], ["Embedded", "embedded"]] as const).map(([label, key]) =>
                <div key={key}><dt className="text-xs text-slate-600">{label}</dt><dd className="font-semibold text-slate-800">{doc.chunkCounts![key]}</dd></div>)}
              {doc.chunkCounts.other > 0 && <div><dt className="text-xs text-rose-700">Unknown chunk status</dt><dd>{doc.chunkCounts.other}</dd></div>}
            </dl> : <p className="text-xs text-slate-600">Chunk counts unavailable. Refresh before assuming this document is indexed.</p>}
            {doc.status === "failed" && !processing && <p className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-700">{doc.failureReason || "Processing failed; no reason was recorded. Retry the failed step or upload the PDF again."}</p>}
            {doc.status === "uploaded" && !processing && <p className="text-sm text-slate-600">Queued for automatic parsing on the server.</p>}
            {(processing || requested) && <p className="flex items-center gap-2 text-sm text-slate-600"><Loader2 className="h-4 w-4 animate-spin motion-reduce:animate-none" aria-hidden="true" />{processing ? `${doc.processingOperation === "embedding" ? "Embedding" : status.label} in progress…` : "Parsing requested…"}</p>}
            <div className="flex flex-wrap gap-2">
              {canRetryParse(doc) && <Button type="button" variant="outline" className="min-h-12" disabled={busy !== null} onClick={() => void retryParse(doc)}>Retry Parse</Button>}
              {canReview && <Link href={`/w/${workspaceId}/documents/${doc.id}/chunks`} className={buttonVariants({ variant: "outline", className: "min-h-12" })}>Choose pages / Review / Embed</Link>}
              <Button type="button" variant="destructive" className="min-h-12" disabled={busy !== null || processing} onClick={() => setConfirmation({
                title: "Delete this document?", description: `Delete ${doc.fileName}, its parsed text, and all pending, accepted, rejected and embedded chunks. This removes its chunks from retrieval and cannot be undone.`,
                confirmLabel: "Delete document", onConfirm: () => void deleteDocument(doc),
              })}><Trash2 className="mr-1 h-4 w-4" aria-hidden="true" /> Delete</Button>
            </div>
          </CardContent>
        </Card>;
      })}
    </div>
    {uploadDialog && <UploadDialog initialFile={uploadDialog.file} busy={busy?.action === "upload"} error={uploadError}
      onUpload={value => void uploadGuideline(value)} onDismiss={() => {
        if (busy?.action === "upload") { requests.cancel(); setBusy(null); setNotice("Upload cancelled. Refresh to check whether the PDF was saved."); void qc.invalidateQueries({ queryKey }); }
        setUploadDialog(null);
      }} />}
    <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} />
  </div>;
}
