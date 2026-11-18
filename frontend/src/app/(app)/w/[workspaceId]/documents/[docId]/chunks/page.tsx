"use client";
import { useParams } from "next/navigation";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, useEffect, useMemo, useId, useCallback, useRef } from "react";
import { api, get } from "@/lib/api";
import type { Chunk, ChunkJob, ChunkProcessingState, Document, RuleAccess } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Loader2, CheckCircle, X, FileText, Brain, Zap, ShieldCheck, Play, RefreshCw, Clock, Wand2, Filter, LayoutList, Trash2, Pause, RotateCcw } from "lucide-react";
import { Label } from "@/components/ui/label";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Input } from "@/components/ui/input";
import { toast } from "sonner";
import { documentActionError, documentIsBusy, documentStatus } from "@/lib/documents";
import { createRequestScope } from "@/lib/request-scope";
import { useAuth } from "@/providers/AuthProvider";
import { ConfirmDialog, type Confirmation } from "@/components/rules/confirm-action";

interface ChunkingProgress {
  totalPages: number;
  chunkedPages: number;
  chunkedPageNumbers: number[];
  chunkingStatus: string | null;
  progressPercent: number;
}

type ReviewStatus = "pending" | "accepted" | "rejected";
type Action = "review" | "bulk-accept" | "reject-front-matter" | "embedding" | "chunking" | "job-control" | "reparse" | "reset-pages" | "reset-embeddings";
type Request = ReturnType<ReturnType<typeof createRequestScope>["start"]>;
type Feedback = { message: string; warning?: boolean };

function mutationCount(value: unknown, name: string): number {
  if (typeof value !== "object" || value === null || !(name in value)) throw new Error("Missing processing evidence.");
  const count = (value as Record<string, unknown>)[name];
  if (typeof count !== "number" || !Number.isSafeInteger(count) || count < 0) throw new Error("Invalid processing evidence.");
  return count;
}

function failedChunkPages(value: unknown, requested: readonly number[]): number[] {
  if (typeof value !== "object" || value === null || !("failedPages" in value) || !Array.isArray(value.failedPages)
    || value.failedPages.some(page => !Number.isSafeInteger(page) || !requested.includes(page)))
    throw new Error("Invalid page processing evidence.");
  return [...new Set(value.failedPages as number[])];
}

function readProgress(value: unknown): ChunkingProgress {
  const totalPages = mutationCount(value, "totalPages");
  const chunkedPages = mutationCount(value, "chunkedPages");
  if (typeof value !== "object" || value === null || !("chunkedPageNumbers" in value)
    || !Array.isArray(value.chunkedPageNumbers) || value.chunkedPageNumbers.some(page => !Number.isSafeInteger(page) || page < 1)
    || new Set(value.chunkedPageNumbers).size !== chunkedPages || value.chunkedPageNumbers.length !== chunkedPages
    || chunkedPages > totalPages || !("chunkingStatus" in value)
    || (value.chunkingStatus !== null && !["not_started", "in_progress", "partial", "completed", "failed"].includes(String(value.chunkingStatus))))
    throw new Error("Invalid processing evidence.");
  return { totalPages, chunkedPages, chunkedPageNumbers: value.chunkedPageNumbers as number[],
    chunkingStatus: value.chunkingStatus as string | null,
    progressPercent: totalPages ? Math.round(chunkedPages / totalPages * 1000) / 10 : 0 };
}

function readChunks(value: unknown, workspaceId: string, docId: string): Chunk[] {
  if (!Array.isArray(value) || value.some(chunk => typeof chunk !== "object" || chunk === null
    || chunk.workspaceId !== workspaceId || chunk.documentId !== docId || typeof chunk.id !== "string"
    || typeof chunk.text !== "string" || typeof chunk.status !== "string" || !Number.isSafeInteger(chunk.page) || chunk.page < 1)
    || new Set(value.map(chunk => chunk.id)).size !== value.length)
    throw new Error("Invalid chunk review evidence.");
  return value as Chunk[];
}

function getPaged<T>(items: readonly T[], page: number, size: number): T[] {
  return items.slice((page - 1) * size, page * size);
}

const STATUS_COLORS: Record<string, string> = {
  accepted: "bg-emerald-50 border-emerald-100 text-emerald-700",
  rejected: "bg-rose-50 border-rose-100 text-rose-700",
  pending: "bg-amber-50 border-amber-100 text-amber-700",
  embedded: "bg-purple-50 border-purple-100 text-purple-700",
};

const JOB_ACTIVE_STATUSES = ["queued", "running", "pausing", "paused", "stopping", "interrupted", "blocked"] as const;
const JOB_TERMINAL_STATUSES = ["completed", "partial", "failed", "cancelled"] as const;
function isActiveJob(job: ChunkJob | null): boolean {
  return !!job && (JOB_ACTIVE_STATUSES as readonly string[]).includes(job.status);
}
function jobStatusLabel(status: ChunkJob["status"]): string {
  return status === "queued" ? "Queued"
    : status === "running" ? "Running"
    : status === "pausing" ? "Pausing after batch"
    : status === "paused" ? "Paused"
    : status === "stopping" ? "Stopping after batch"
    : status === "cancelled" ? "Cancelled"
    : status === "completed" ? "Completed"
    : status === "partial" ? "Partially completed"
    : status === "blocked" ? "Blocked"
    : status === "interrupted" ? "Interrupted"
    : "Failed";
}
const POLL_INTERVAL_OPTIONS = [2000, 5000, 10000, 30000] as const;
const DEFAULT_POLL_INTERVAL = POLL_INTERVAL_OPTIONS[0];
const POLLING_STORAGE_PREFIX = "clinical-rag:chunk-review-polling";

function pollingStorageKey(workspaceId: string, docId: string, userId: string): string {
  return `${POLLING_STORAGE_PREFIX}:${userId}:${workspaceId}:${docId}`;
}

function readPollingPreference(key: string): { autoPoll: boolean; interval: number } {
  if (typeof window === "undefined") return { autoPoll: true, interval: DEFAULT_POLL_INTERVAL };
  try {
    const parsed = JSON.parse(window.localStorage.getItem(key) ?? "null") as
      { autoPoll?: unknown; interval?: unknown } | null;
    const interval = typeof parsed?.interval === "number"
      && (POLL_INTERVAL_OPTIONS as readonly number[]).includes(parsed.interval)
      ? parsed.interval : DEFAULT_POLL_INTERVAL;
    return { autoPoll: typeof parsed?.autoPoll === "boolean" ? parsed.autoPoll : true, interval };
  } catch {
    return { autoPoll: true, interval: DEFAULT_POLL_INTERVAL };
  }
}

function savePollingPreference(key: string, autoPoll: boolean, interval: number): void {
  if (typeof window === "undefined") return;
  try { window.localStorage.setItem(key, JSON.stringify({ autoPoll, interval })); } catch { /* Private mode may refuse storage. */ }
}

function readProcessingState(value: unknown, workspaceId: string, docId: string): ChunkProcessingState {
  if (typeof value !== "object" || value === null) throw new Error("Invalid processing state.");
  const state = value as Record<string, unknown>;
  const chunkedPagesCount = state.chunkedPagesCount as number;
  if (state.documentId !== docId || state.workspaceId !== workspaceId || typeof state.status !== "string"
    || !Number.isSafeInteger(state.revision) || !Number.isSafeInteger(chunkedPagesCount)
    || chunkedPagesCount < 0 || !Array.isArray(state.chunkedPageNumbers)
    || state.chunkedPageNumbers.some(page => !Number.isSafeInteger(page) || page < 1)
    || new Set(state.chunkedPageNumbers).size !== state.chunkedPageNumbers.length
    || state.chunkedPageNumbers.length !== chunkedPagesCount)
    throw new Error("Invalid processing state.");
  const counts = state.chunkCounts;
  if (typeof counts !== "object" || counts === null || !["total", "pending", "accepted", "rejected", "embedded", "other"]
    .every(name => Number.isSafeInteger((counts as Record<string, unknown>)[name])
      && ((counts as Record<string, number>)[name] >= 0)))
    throw new Error("Invalid processing state counts.");
  return value as ChunkProcessingState;
}

function processingStateSignature(state: ChunkProcessingState): string {
  const job = state.job;
  return JSON.stringify({
    revision: state.revision,
    status: state.status,
    operation: state.processingOperation,
    failure: state.failureReason,
    chunked: state.chunkedPageNumbers,
    counts: state.chunkCounts,
    job: job && {
      id: job.id, status: job.status, completed: job.completedPages, failed: job.failedPages,
      skipped: job.skippedPages, error: job.errorCode, completedBatches: job.completedBatches,
      currentStart: job.currentBatchStart, currentEnd: job.currentBatchEnd,
    },
  });
}

function jobFailureLabel(code?: string | null): string {
  return code === "colab_unavailable" ? "Colab model service unavailable"
    : code === "batch_timeout" ? "A batch timed out"
    : code === "invalid_batch_result" ? "The chunk service returned an invalid batch"
    : code === "empty_batch_result" ? "The chunk service returned no chunk or skip for a page"
    : code === "worker_error" ? "The server interrupted the job"
    : code ? code.replaceAll("_", " ") : "No error code recorded";
}

// Parse "--- Page N ---" markers from document text
function parsePages(text: string): { page: number; text: string }[] {
  const pages: { page: number; text: string }[] = [];
  const marker = /^\s*---\s*Page\s+(\d+)\s*---\s*$/m;
  const parts = text.split(marker);
  if (parts.length === 1) {
    pages.push({ page: 1, text: text.trim() });
    return pages;
  }
  if (parts[0]?.trim()) {
    pages.push({ page: 1, text: parts[0].trim() });
  }
  for (let i = 1; i < parts.length; i += 2) {
    const num = parseInt(parts[i], 10);
    const body = i + 1 < parts.length ? parts[i + 1] : "";
    pages.push({ page: num, text: body.trim() });
  }
  return pages;
}

// Format confidence score as percentage
function formatScore(confidence?: number | null): string {
  if (!validScore(confidence)) return "N/A";
  return `${(confidence * 100).toFixed(0)}%`;
}

// Score color badge
function scoreColor(confidence?: number | null): string {
  if (!validScore(confidence)) return "text-slate-400";
  if (confidence >= 0.85) return "text-emerald-600";
  if (confidence >= 0.60) return "text-amber-600";
  return "text-rose-600";
}

function validScore(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1;
}

export default function ChunkReviewPage() {
  const { workspaceId, docId } = useParams<{ workspaceId: string; docId: string }>();
  const { user } = useAuth();
  return user ? <ChunkReview key={`${workspaceId}:${docId}:${user.uid}`} workspaceId={workspaceId} docId={docId} userId={user.uid} /> : null;
}

function ChunkReview({ workspaceId, docId, userId }: { workspaceId: string; docId: string; userId: string }) {
  const qc = useQueryClient();
  const id = useId();
  const [requests] = useState(createRequestScope);
  const [busy, setBusy] = useState<Action | "cancelling" | null>(null);
  const [notice, setNotice] = useState("");
  useEffect(() => () => requests.cancel(), [requests]);
  const [guidanceDraft, setGuidance] = useState<string | null>(null);
  const [resetSelection, setResetSelection] = useState<number[]>([]);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [threshold, setThreshold] = useState(0.85);
  // null means the initial default. An explicit empty selection must stay empty,
  // including after polling or when the last selected page is deselected.
  const [pageSelection, setPageSelection] = useState<number[] | null>(null);
  const [groupBy, setGroupBy] = useState<"none" | "page" | "section">("none");
  const [filterScore, setFilterScore] = useState<number | null>(null);
  const [filterType, setFilterType] = useState<string | null>(null);
  const [filterPage, setFilterPage] = useState<number | null>(null);
  const [expandedChunks, setExpandedChunks] = useState<string[]>([]);
  const [job, setJob] = useState<ChunkJob | null>(null);
  const [jobError, setJobError] = useState("");
  const [processingState, setProcessingState] = useState<ChunkProcessingState | null>(null);
  const [pollingError, setPollingError] = useState("");
  const [autoPoll, setAutoPoll] = useState(true);
  const [pollInterval, setPollInterval] = useState<number>(DEFAULT_POLL_INTERVAL);
  const [pollingReady, setPollingReady] = useState(false);
  const [pollingBusy, setPollingBusy] = useState(false);
  const [pageVisible, setPageVisible] = useState(true);
  const [lastPolledAt, setLastPolledAt] = useState<Date | null>(null);
  const pollInFlight = useRef(false);
  const lastStateSignature = useRef<string | null>(null);
  const [jobSettings, setJobSettings] = useState({
    batchSize: 2, maxTokens: 400, batchTimeoutSeconds: 300, maxRetries: 1,
    failureMode: "continue" as "continue" | "stop",
  });

  const documentKey = ["document", workspaceId, docId, userId];
  const chunksKey = ["chunks", workspaceId, docId, userId];
  const progressKey = ["chunking-progress", workspaceId, docId, userId];
  const guidanceKey = ["chunker-guidance", workspaceId, docId, userId];
  const accessKey = ["rules", workspaceId, userId, "access"];
  const accessQuery = useQuery<RuleAccess>({ queryKey: accessKey, retry: false,
    queryFn: ({ signal }) => get(`/api/workspaces/${workspaceId}/rules/access`, { signal, cache: "no-store" }),
    refetchInterval: pollingReady && autoPoll && pageVisible && busy ? pollInterval : false,
    refetchOnWindowFocus: false, refetchOnReconnect: false });
  const canEdit = accessQuery.data?.canEdit === true && ["editor", "owner"].includes(accessQuery.data.role);
  const editingUnavailable = accessQuery.isError || (!!accessQuery.data && !canEdit);
  useEffect(() => { if (editingUnavailable) requests.cancel(); }, [editingUnavailable, requests]);
  if (editingUnavailable && busy) setBusy(null);
  if (editingUnavailable && confirmation) setConfirmation(null);
  const guidanceQuery = useQuery({ queryKey: guidanceKey, retry: false,
    queryFn: async ({ signal }) => {
      const saved = await get<{ guidance: string }>(`/api/workspaces/${workspaceId}/documents/${docId}/chunker-guidance`, { signal, cache: "no-store" });
      if (typeof saved?.guidance !== "string" || saved.guidance.length > 4000) throw new Error("Invalid saved guidance.");
      return saved;
    },
    refetchOnWindowFocus: false, refetchOnReconnect: false });
  // An untouched field follows saved guidance. Polling/refetch never clobbers a local edit.
  const guidance = guidanceDraft ?? guidanceQuery.data?.guidance ?? "keep each recommendation with its strength grade; never split tables";
  const documentQuery = useQuery({
    queryKey: documentKey,
    queryFn: ({ signal }) => get<Document>(`/api/workspaces/${workspaceId}/documents/${docId}`, { signal, cache: "no-store" }),
    enabled: !!docId,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const document = documentQuery.data?.id === docId && documentQuery.data.workspaceId === workspaceId ? documentQuery.data : undefined;

  const chunksQuery = useQuery<Chunk[]>({
    queryKey: chunksKey,
    queryFn: async ({ signal }) => readChunks(await get(`/api/workspaces/${workspaceId}/chunks?documentId=${docId}&includeEmbedded=true`, { signal, cache: "no-store" }), workspaceId, docId),
    enabled: !!docId,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });

  const progressQuery = useQuery({
    queryKey: progressKey,
    queryFn: async ({ signal }) => readProgress(await get(`/api/workspaces/${workspaceId}/documents/${docId}/chunking-progress`, { signal, cache: "no-store" })),
    enabled: !!docId,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const progress = progressQuery.data;
  const readFailed = documentQuery.isError || chunksQuery.isError || progressQuery.isError || guidanceQuery.isError || accessQuery.isError
    || (!!documentQuery.data && !document);
  const jobIsActive = isActiveJob(job);
  const stateIsProcessing = !!processingState && (processingState.status === "uploaded"
    || processingState.status === "parsing" || processingState.status === "chunking"
    || !!processingState.processingOperation);
  const processing = busy !== null || jobIsActive || stateIsProcessing
    || (!!document && (documentIsBusy(document) || document.status === "uploaded"));
  const mutationsBlocked = processing || readFailed || !canEdit || !document || !chunksQuery.data || !progress || !guidanceQuery.data;

  const preferenceKey = pollingStorageKey(workspaceId, docId, userId);
  useEffect(() => {
    const timer = window.setTimeout(() => {
      const preference = readPollingPreference(preferenceKey);
      setAutoPoll(preference.autoPoll);
      setPollInterval(preference.interval);
      setPollingReady(true);
    }, 0);
    return () => window.clearTimeout(timer);
  }, [preferenceKey]);
  useEffect(() => {
    if (pollingReady) savePollingPreference(preferenceKey, autoPoll, pollInterval);
  }, [pollingReady, preferenceKey, autoPoll, pollInterval]);

  const refresh = useCallback(async () => {
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["document", workspaceId, docId, userId] }),
      qc.invalidateQueries({ queryKey: ["chunks", workspaceId, docId, userId] }),
      qc.invalidateQueries({ queryKey: ["chunking-progress", workspaceId, docId, userId] }),
      qc.invalidateQueries({ queryKey: ["documents", workspaceId] }),
      qc.invalidateQueries({ queryKey: ["chunker-guidance", workspaceId, docId, userId] }),
      qc.invalidateQueries({ queryKey: ["rules", workspaceId, userId, "access"] }),
    ]);
  }, [qc, workspaceId, docId, userId]);

  const pollNow = useCallback(async (full = true) => {
    if (pollInFlight.current) return;
    pollInFlight.current = true;
    setPollingBusy(true);
    try {
      const state = readProcessingState(await get<ChunkProcessingState>(
        `/api/workspaces/${workspaceId}/documents/${docId}/processing-state`, { cache: "no-store" }), workspaceId, docId);
      setProcessingState(state);
      setJob(state.job);
      setPollingError("");
      const signature = processingStateSignature(state);
      const changed = lastStateSignature.current !== null && lastStateSignature.current !== signature;
      lastStateSignature.current = signature;
      if (full || changed) await refresh();
    } catch {
      setPollingError("Unable to refresh processing state. The background job is not cancelled; poll again.");
      if (full) await refresh();
    } finally {
      setLastPolledAt(new Date());
      pollInFlight.current = false;
      setPollingBusy(false);
    }
  }, [workspaceId, docId, refresh]);

  useEffect(() => {
    const timer = window.setTimeout(() => { void pollNow(false); }, 0);
    return () => window.clearTimeout(timer);
  }, [pollNow]);

  useEffect(() => {
    const onVisibility = () => setPageVisible(window.document.visibilityState === "visible");
    onVisibility();
    window.document.addEventListener("visibilitychange", onVisibility);
    return () => window.document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  useEffect(() => {
    if (!pollingReady || !autoPoll || !pageVisible || !processing) return;
    const delay = pollingError ? Math.min(pollInterval * 2, 30000) : pollInterval;
    const timer = setTimeout(() => { void pollNow(false); }, delay);
    return () => { clearTimeout(timer); };
  }, [pollingReady, autoPoll, pageVisible, processing, pollInterval, pollingError, lastPolledAt, pollNow]);

  useEffect(() => {
    if (!job || !(JOB_TERMINAL_STATUSES as readonly string[]).includes(job.status)) return;
    void refresh();
  }, [job, refresh]);

  // Parse pages from document text
  const parsedText = document?.parsedText;
  const pages = useMemo(() => parsedText ? parsePages(parsedText) : [], [parsedText]);
  const chunkedSet = new Set(progress?.chunkedPageNumbers ?? []);
  const unchunkedPages = progress ? pages.filter(p => !chunkedSet.has(p.page)) : [];
  const unchunkedNumbers = unchunkedPages.map(p => p.page);
  const selectedPages = (pageSelection ?? unchunkedNumbers).filter(page => unchunkedNumbers.includes(page));

  // Toggle page selection
  const togglePage = (page: number) => {
    if (mutationsBlocked || !unchunkedNumbers.includes(page)) return;
    setPageSelection(selectedPages.includes(page) ? selectedPages.filter(p => p !== page) : [...selectedPages, page].sort((a, b) => a - b));
  };

  async function cancelRequest() {
    if (!requests.active || busy === "cancelling") return;
    requests.cancel();
    const cancellation = requests.start();
    setBusy("cancelling");
    const message = "Request cancelled. Completed work may have been kept; check server processing before retrying.";
    setNotice(message); toast.warning(message);
    await refresh();
    if (cancellation.isCurrent()) { cancellation.finish(); setBusy(null); }
  }

  async function runAction(action: Action, fallback: string, work: (request: Request) => Promise<Feedback>) {
    // The in-memory owner blocks double clicks before React has rendered busy.
    if (mutationsBlocked || requests.active) return;
    const request = requests.start();
    setBusy(action); setNotice("");
    try {
      const feedback = await work(request);
      if (!request.isCurrent()) return;
      setNotice(feedback.message);
      if (feedback.warning) toast.warning(feedback.message); else toast.success(feedback.message);
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, fallback);
        setNotice(message); toast.error(message);
      }
    } finally {
      // Even partial multi-chunk review failures must refresh committed changes.
      if (request.isCurrent()) {
        await refresh();
        if (request.isCurrent()) { request.finish(); setBusy(null); }
      }
    }
  }

  async function updateChunk(chunkId: string, status: ReviewStatus, request: Request) {
    const result = await api<Chunk>(`/api/workspaces/${workspaceId}/chunks/${chunkId}`, {
      method: "PATCH", body: JSON.stringify({ status }), signal: request.signal,
    });
    if (result?.id !== chunkId || result.documentId !== docId || result.workspaceId !== workspaceId || result.status !== status)
      throw new Error("Chunk review was not confirmed.");
  }

  function reviewChunk(chunkId: string, status: ReviewStatus) {
    return runAction("review", "Chunk review was not confirmed. Refresh before trying again.", async request => {
      await updateChunk(chunkId, status, request);
      return { message: `Chunk ${status === "pending" ? "reset to pending" : status}.` };
    });
  }

  function bulkAccept() {
    if (!validScore(threshold) || pendingWithScore.length === 0) return;
    const submittedThreshold = threshold;
    return runAction("bulk-accept", "Bulk acceptance was not confirmed. Refresh before trying again.", async request => {
      const result = await api(`/api/workspaces/${workspaceId}/chunks/bulk-accept`, {
        method: "POST", body: JSON.stringify({ documentId: docId, minConfidence: submittedThreshold }), signal: request.signal,
      });
      return { message: `Accepted ${mutationCount(result, "accepted")} chunks (≥ ${submittedThreshold.toFixed(2)}).` };
    });
  }

  function bulkRejectFrontMatter() {
    const candidates = frontMatterChunks.filter(c => c.status === "pending");
    if (!candidates.length) return;
    return runAction("reject-front-matter", "Front-matter review stopped. Some changes may have completed; refresh before retrying.", async request => {
      for (const chunk of candidates) {
        request.signal.throwIfAborted();
        await updateChunk(chunk.id, "rejected", request);
      }
      return { message: `Rejected ${candidates.length} front-matter chunks.` };
    });
  }

  function generateEmbeddings() {
    if (!acceptedChunks.length) return;
    return runAction("embedding", "Embedding failed. No completion was confirmed. Refresh before retrying Embed.", async request => {
      const result = await api(`/api/workspaces/${workspaceId}/documents/${docId}/generate-embeddings`, {
        method: "POST", body: "{}", signal: request.signal,
      });
      return { message: `Generated embeddings for ${mutationCount(result, "embedded")} chunks.` };
    });
  }

  async function startBackgroundJob(requested: number[]) {
    if (!requested.length || requests.active || jobIsActive) return;
    if (requested.some(page => !unchunkedNumbers.includes(page))) return;
    const request = requests.start();
    setBusy("chunking"); setJobError(""); setPollingError(""); setNotice("");
    try {
      const response = await api<{ jobId: string }>(`/api/workspaces/${workspaceId}/documents/${docId}/chunk-jobs`, {
        method: "POST",
        body: JSON.stringify({ pages: requested, guidance, ...jobSettings }),
        signal: request.signal,
      });
      if (!response?.jobId) throw new Error("The chunk job was not accepted.");
      const next = await get<ChunkJob>(`/api/workspaces/${workspaceId}/chunk-jobs/${response.jobId}`, { cache: "no-store" });
      if (!request.isCurrent()) return;
      setJob(next);
      setNotice(`Chunk job started for ${requested.length} page(s). It continues if you leave this page.`);
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, "The background chunk job could not be started.");
        setJobError(message); setNotice(message); toast.error(message);
      }
    } finally {
      if (request.isCurrent()) { await pollNow(false); if (request.isCurrent()) { request.finish(); setBusy(null); } }
    }
  }

  async function runJobAction(action: "pause" | "resume" | "cancel" | "retry") {
    if (!job || requests.active) return;
    const request = requests.start();
    setBusy("job-control"); setJobError(""); setPollingError("");
    try {
      if (action === "retry") {
        const response = await api<{ jobId: string }>(`/api/workspaces/${workspaceId}/chunk-jobs/${job.id}/retry`, {
          method: "POST", body: JSON.stringify({ scope: "failed" }), signal: request.signal,
        });
        if (!response?.jobId) throw new Error("The retry job was not accepted.");
        const next = await get<ChunkJob>(`/api/workspaces/${workspaceId}/chunk-jobs/${response.jobId}`, { cache: "no-store" });
        if (!request.isCurrent()) return;
        setJob(next);
        setNotice("Retry job started for the failed pages.");
      } else {
        const next = await api<ChunkJob | { jobId: string; status: string; pollAfterMs?: number }>(`/api/workspaces/${workspaceId}/chunk-jobs/${job.id}/${action}`, {
          method: "POST", body: "{}", signal: request.signal,
        });
        if (!request.isCurrent()) return;
        if (action !== "resume") setJob(next as ChunkJob);
        setNotice(action === "pause" ? "Pause requested after the current batch."
          : action === "resume" ? "Chunk job resumed." : "Chunk job cancellation requested.");
      }
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, `The chunk job ${action} request was not accepted.`);
        setJobError(message); setNotice(message); toast.error(message);
      }
    } finally {
      if (request.isCurrent()) { await pollNow(false); if (request.isCurrent()) { request.finish(); setBusy(null); } }
    }
  }

  function cancelJob() {
    if (!job) return;
    setConfirmation({
      title: "Cancel this chunk job?",
      confirmLabel: "Cancel job",
      description: "The worker will stop after the current batch. Completed pages are kept. You can retry the failed or remaining pages later.",
      onConfirm: () => void runJobAction("cancel"),
    });
  }

  async function retryRemaining() {
    if (!job || requests.active) return;
    const request = requests.start();
    setBusy("job-control"); setJobError(""); setPollingError("");
    try {
      const response = await api<{ jobId: string }>(`/api/workspaces/${workspaceId}/chunk-jobs/${job.id}/retry`, {
        method: "POST", body: JSON.stringify({ scope: "remaining" }), signal: request.signal,
      });
      if (!response?.jobId) throw new Error("The retry job was not accepted.");
      const next = await get<ChunkJob>(`/api/workspaces/${workspaceId}/chunk-jobs/${response.jobId}`, { cache: "no-store" });
      if (!request.isCurrent()) return;
      setJob(next);
      setNotice("Retry job started for the remaining pages.");
    } catch (cause) {
      if (request.isCurrent()) {
        const message = documentActionError(cause, "The remaining-pages retry could not be started.");
        setJobError(message); setNotice(message); toast.error(message);
      }
    } finally {
      if (request.isCurrent()) { await pollNow(false); if (request.isCurrent()) { request.finish(); setBusy(null); } }
    }
  }

  function chunkPages(requested: number[]) {
    if (!requested.length || requested.some(page => !unchunkedNumbers.includes(page))) return;
    return runAction("chunking", "Chunking did not complete. Previously completed pages were kept; refresh before retrying.", async request => {
      // The server owns the batch loop and commits its page checkpoints. Leaving
      // this screen cancels the one request, rather than starting more batches.
      const result = await api(`/api/workspaces/${workspaceId}/documents/${docId}/chunk-portions`, {
        method: "POST", body: JSON.stringify({ pages: requested, guidance, maxTokens: 400, batchSize: 2 }), signal: request.signal,
      });
      const created = mutationCount(result, "chunksCreated");
      const chunked = mutationCount(result, "pagesChunked");
      const skipped = mutationCount(result, "pagesSkipped");
      const failed = failedChunkPages(result, requested);
      if (chunked + skipped + failed.length > requested.length || created < chunked || (chunked === 0 && created > 0))
        throw new Error("Contradictory processing evidence.");
      if (request.isCurrent()) setPageSelection(failed);
      return { message: `${chunked} page(s) chunked; ${created} chunks created; ${skipped} page(s) skipped.`
        + (failed.length ? ` Failed pages: p.${failed.join(", p.")}. Completed pages were kept; retry the failed selection.` : " Review chunks before embedding."),
        warning: failed.length > 0 };
    });
  }

  function resetPages(requested: number[]) {
    if (!requested.length) return;
    setConfirmation({ title: "Reset selected source pages?", confirmLabel: "Reset pages",
      description: "Delete all chunks and embeddings overlapping these pages, including accepted chunks. If a chunk spans other pages, those pages are reset too. The PDF and saved guidance are kept. You must explicitly chunk the reset pages again.",
      onConfirm: () => void runAction("reset-pages", "Page reset was not confirmed. Refresh before trying again.", async request => {
        const result = await api<{ chunksDeleted: number; resetPages: number[] }>(`/api/workspaces/${workspaceId}/documents/${docId}/reset-pages`, {
          method: "POST", body: JSON.stringify({ pages: requested }), signal: request.signal });
        const removed = mutationCount(result, "chunksDeleted");
        if (!Array.isArray(result.resetPages) || result.resetPages.some(page => !pages.some(source => source.page === page))
          || requested.some(page => !result.resetPages.includes(page))) throw new Error("Invalid reset evidence.");
        if (request.isCurrent()) { setResetSelection([]); setPageSelection(result.resetPages); }
        return { message: `Deleted ${removed} chunks. Reset pages: ${result.resetPages.join(", ")}. Use Chunk Selected to re-chunk them.` };
      }) });
  }

  function resetEmbeddings() {
    setConfirmation({ title: "Reset document embeddings?", confirmLabel: "Reset embeddings",
      description: "Remove all vectors for this document from retrieval. Embedded chunks return to accepted; text, review attribution, pages and guidance are kept. Use Embed to index accepted chunks again.",
      onConfirm: () => void runAction("reset-embeddings", "Embedding reset was not confirmed. Refresh before trying again.", async request => {
        const result = await api(`/api/workspaces/${workspaceId}/documents/${docId}/reset-embeddings`, { method: "POST", body: "{}", signal: request.signal });
        return { message: `Reset embeddings for ${mutationCount(result, "embeddingsReset")} chunks. Accepted chunks must be embedded again before retrieval.` };
      }) });
  }

  function reparseDocument() {
    setConfirmation({
      title: "Re-parse this source PDF?",
      confirmLabel: "Re-parse source",
      description: "Replace the stored parsed text with a fresh parse. Chunks on pages whose text changed are kept but marked for re-review, and their vectors are cleared. Chunks on unchanged pages remain reviewed.",
      onConfirm: () => void runAction("reparse", "The source PDF could not be re-parsed. Existing text and chunks were kept.", async request => {
        const result = await api<{ changedPages: number; chunksMarked: number; footnoteMarkerCount?: number }>(
          `/api/workspaces/${workspaceId}/documents/${docId}/reparse`, { method: "POST", body: "{}", signal: request.signal });
        const changed = mutationCount(result, "changedPages");
        const marked = mutationCount(result, "chunksMarked");
        return { message: changed === 0
          ? "Source re-parsed. No page text changed; existing reviews are unchanged."
          : `Source re-parsed. ${changed} page(s) changed; ${marked} chunk(s) marked for re-review.`,
          warning: marked > 0 };
      })
    });
  }

  // --- Computed collections ---
  const allChunks = useMemo(() => (chunksQuery.data ?? []).filter(c => c.documentId === docId && c.workspaceId === workspaceId), [chunksQuery.data, docId, workspaceId]);
  const pendingChunks = useMemo(() => allChunks.filter(c => c.reReviewRequired || !["accepted", "rejected", "embedded"].includes(c.status)), [allChunks]);
  const acceptedChunks = allChunks.filter(c => c.status === "accepted" && !c.reReviewRequired);
  const rejectedChunks = allChunks.filter(c => c.status === "rejected" && !c.reReviewRequired);
  const embeddedChunks = allChunks.filter(c => c.status === "embedded" && !c.reReviewRequired);
  const acceptedAndEmbedded = allChunks.filter(c => (c.status === "accepted" || c.status === "embedded") && !c.reReviewRequired);
  const frontMatterChunks = allChunks.filter(c => c.type === "front_matter");

  // Apply filters
  const filteredPending = useMemo(() => {
    return pendingChunks.filter(c => {
      if (filterScore != null && (c.confidence == null || c.confidence < filterScore)) return false;
      if (filterType && c.type !== filterType) return false;
      if (filterPage && c.page !== filterPage) return false;
      return true;
    });
  }, [pendingChunks, filterScore, filterType, filterPage]);

  // Group chunks by page or section
  const groupedPending = useMemo(() => {
    if (groupBy === "none") return null;
    const groups = new Map<string, Chunk[]>();
    for (const c of filteredPending) {
      const key = groupBy === "page" ? `p.${c.page}` : (c.sectionHierarchy || c.section || "Uncategorized");
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(c);
    }
    return Array.from(groups.entries()).sort((a, b) => {
      // Sort pages numerically, sections alphabetically
      if (groupBy === "page") {
        const pa = parseInt(a[0].replace("p.", ""));
        const pb = parseInt(b[0].replace("p.", ""));
        return pa - pb;
      }
      return a[0].localeCompare(b[0]);
    });
  }, [filteredPending, groupBy]);

  const accepted = acceptedChunks.length;
  const rejected = rejectedChunks.length;
  const progressPercent = progress?.progressPercent;

  // Count only chunks that have a real numeric confidence score ≥ threshold
  const pendingWithScore = pendingChunks.filter(c => c.status === "pending" && validScore(c.confidence) && c.confidence >= threshold);

  // Pagination
  const [docPage, setDocPage] = useState(1);
  const [chunkPage, setChunkPage] = useState(1);
  const [reviewedPage, setReviewedPage] = useState(1);
  const PAGE_SIZE = 5;
  const DOC_PAGE_SIZE = 10; // pages per view in document text tab

  const currentChunkPage = Math.min(chunkPage, Math.max(1, Math.ceil(filteredPending.length / PAGE_SIZE)));
  const currentDocPage = Math.min(docPage, Math.max(1, Math.ceil(pages.length / DOC_PAGE_SIZE)));
  const currentReviewedPage = Math.min(reviewedPage, Math.max(1, Math.ceil(acceptedAndEmbedded.length / PAGE_SIZE)));
  const pagedPending = getPaged(filteredPending, currentChunkPage, PAGE_SIZE);

  // Unique values for filters
  const uniquePages = useMemo(() => {
    return [...new Set(allChunks.map(c => c.page))].sort((a, b) => a - b);
  }, [allChunks]);

  const uniqueTypes = useMemo(() => {
    return [...new Set(allChunks.filter(c => c.type).map(c => c.type))].sort();
  }, [allChunks]);

  if (!document || !chunksQuery.data || !progress) return <div className="space-y-4">
    <h1 className="text-3xl font-bold">Chunk Review</h1>
    {readFailed ? <p role="alert">Could not load document, chunks or processing evidence. Refresh before reviewing or processing this document.</p>
      : <p>Loading document, chunks and processing evidence…</p>}
    <Button type="button" variant="outline" onClick={() => void pollNow(true)} disabled={pollingBusy}>
      <RefreshCw aria-hidden="true" className="w-4 h-4 mr-2" /> Refresh
    </Button>
  </div>;

  const status = documentStatus(document);
  const chunkingLabel = ({ not_started: "Not started", in_progress: "In progress", partial: "Partial", completed: "Completed", failed: "Failed" } as Record<string, string>)[progress.chunkingStatus ?? "not_started"] ?? "Unknown";
  const noParsedText = document.status === "uploaded" ? "Queued for automatic parsing on the server."
    : document.status === "parsing" ? "Parsing is in progress. Pages appear when parsing finishes."
    : document.failureStage === "parsing" ? "Parsing failed. Return to Documents to Retry Parse."
    : "No parsed pages are available. Return to Documents to check parsing.";

  const jobCompleted = job?.completedPages.length ?? 0;
  const jobSkipped = job?.skippedPages.length ?? 0;
  const jobFailed = job?.failedPages.length ?? 0;
  const jobRemaining = job
    ? job.pages.filter(page => !job.completedPages.includes(page) && !job.failedPages.includes(page) && !job.skippedPages.includes(page))
    : [];
  const jobPercent = job?.pages.length ? Math.round(((jobCompleted + jobSkipped) / job.pages.length) * 1000) / 10 : 0;
  const jobRunning = isActiveJob(job);
  const pollDelay = pollingError ? Math.min(pollInterval * 2, 30000) : pollInterval;
  const nextPollLabel = !autoPoll ? "manual"
    : !pageVisible ? "paused (tab hidden)"
    : !processing ? "idle"
    : lastPolledAt ? new Date(lastPolledAt.getTime() + pollDelay).toLocaleTimeString()
    : "pending";

  return (
    <div className="space-y-6">
      <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-slate-900 tracking-tight">Chunk Review</h1>
          <p className="text-slate-500 mt-1">Choose pages, review passages, then embed accepted chunks.</p>
          <p className="mt-2 text-sm text-slate-600">{document.fileName} · {document.society ?? "Society not recorded"} · {document.year ?? "Year not recorded"}</p>
          <div className="mt-2 flex gap-2">
            <Badge variant="outline" className={status.color}>{status.label}</Badge>
            {document.guidelineStatus === "draft" && <Badge variant="outline">DRAFT</Badge>}
          </div>
          {document.needsOcrPages && document.needsOcrPages.length > 0 && <p role="status" className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-sm text-amber-800">
            Needs OCR: pages {document.needsOcrPages.join(", ")} were image-only and are not available for review or embedding.
          </p>}
        </div>
        <div className="flex gap-3 flex-wrap">
          <Button type="button" onClick={() => void chunkPages(unchunkedNumbers)}
            disabled={mutationsBlocked || unchunkedPages.length === 0} className="min-h-11 bg-slate-900 hover:bg-slate-800 text-white shadow-sm">
            <Play aria-hidden="true" className="w-4 h-4 mr-2" /> Chunk All Portions ({unchunkedPages.length})
          </Button>
          <Button type="button" onClick={() => void chunkPages(selectedPages)}
            disabled={mutationsBlocked || selectedPages.length === 0} className="min-h-11 bg-indigo-600 hover:bg-indigo-700 text-white shadow-sm">
            <Zap aria-hidden="true" className="w-4 h-4 mr-2" /> Chunk Selected ({selectedPages.length})
          </Button>
          <Button type="button" onClick={() => void bulkAccept()}
            disabled={mutationsBlocked || pendingWithScore.length === 0} className="min-h-11 bg-emerald-700 hover:bg-emerald-800 text-white shadow-sm">
            Accept all ≥ {threshold.toFixed(2)} ({pendingWithScore.length})
          </Button>
          <Button type="button" onClick={() => void bulkRejectFrontMatter()}
            disabled={mutationsBlocked || frontMatterChunks.filter(c => c.status === "pending").length === 0} className="min-h-11 bg-rose-600 hover:bg-rose-700 text-white shadow-sm">
            <Trash2 aria-hidden="true" className="w-4 h-4 mr-2" /> Reject Front Matter ({frontMatterChunks.filter(c => c.status === "pending").length})
          </Button>
          <Button
            type="button"
            onClick={() => void generateEmbeddings()}
            disabled={mutationsBlocked || accepted === 0}
            size="sm"
            variant="outline"
            className="min-h-11 text-xs px-2 py-1"
          >
            {busy === "embedding" ? <Loader2 aria-hidden="true" className="w-3 h-3 mr-1 animate-spin" /> : <Wand2 aria-hidden="true" className="w-3 h-3 mr-1" />} Embed ({accepted})
          </Button>
          <Button type="button" variant="outline" className="min-h-11" onClick={() => void pollNow(true)} disabled={pollingBusy}>
            <RefreshCw aria-hidden="true" className="w-4 h-4 mr-2" /> Refresh
          </Button>
          {busy && <Button type="button" variant="outline" className="min-h-11" disabled={busy === "cancelling"} onClick={() => void cancelRequest()}>Cancel request</Button>}
        </div>
      </div>

      <Card className="border-indigo-200 bg-indigo-50/20">
        <CardHeader className="px-5 py-4 border-b border-indigo-100">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <h2 className="text-sm font-semibold text-slate-900">Background chunk job</h2>
              <p className="mt-1 text-xs text-slate-600">Runs on the server. Closing this page or stopping polling does not cancel the job.</p>
            </div>
            <Badge variant="outline">{job ? jobStatusLabel(job.status) : "No active job"}</Badge>
          </div>
        </CardHeader>
        <CardContent className="space-y-4 p-5">
          {job && <div className="space-y-2 rounded-lg border border-indigo-100 bg-white p-3">
            <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-600">
              <span role="status">{jobStatusLabel(job.status)} · {jobCompleted + jobSkipped}/{job.pages.length} pages</span>
              <span>{job.currentBatchStart != null ? `Current batch: p.${job.currentBatchStart}${job.currentBatchEnd !== job.currentBatchStart ? `–${job.currentBatchEnd}` : ""}` : "No batch in flight"}</span>
            </div>
            <progress className="w-full accent-indigo-600" max={100} value={jobPercent} aria-label="Background chunk job progress" />
            <div className="flex flex-wrap gap-3 text-xs text-slate-600">
              <span>Completed: {jobCompleted}</span>
              <span>Failed: {jobFailed}</span>
              <span>Skipped: {jobSkipped}</span>
              <span>Remaining: {jobRemaining.length}</span>
              <span>Batches: {job.completedBatches}/{job.totalBatches}</span>
            </div>
            {job.currentBatchStart != null && <p className="text-xs text-indigo-800">The current batch finishes before pause or cancel takes effect.</p>}
            {job.errorCode && <p role="alert" className="text-xs text-rose-700">{jobFailureLabel(job.errorCode)}.</p>}
            {(job.status === "failed" || job.status === "partial" || job.status === "blocked" || job.status === "interrupted") &&
              <p className="text-xs text-slate-600">Completed pages are retained. Use Retry failed or Retry remaining; blocked jobs can be resumed when the model is ready.</p>}
            <div className="flex flex-wrap gap-2">
              {job.canPause && <Button type="button" size="sm" variant="outline" onClick={() => void runJobAction("pause")}><Pause className="mr-1 h-4 w-4" aria-hidden="true" /> {job.status === "queued" ? "Pause job" : "Pause after current batch"}</Button>}
              {job.canResume && <Button type="button" size="sm" onClick={() => void runJobAction("resume")}>Resume job</Button>}
              {job.canCancel && <Button type="button" size="sm" variant="outline" onClick={cancelJob}>Cancel job</Button>}
              {job.canRetry && <Button type="button" size="sm" variant="outline" disabled={jobFailed === 0} onClick={() => void runJobAction("retry")}><RotateCcw className="mr-1 h-4 w-4" aria-hidden="true" /> Retry failed ({jobFailed})</Button>}
              {job.canRetry && <Button type="button" size="sm" variant="outline" disabled={jobRemaining.length === 0} onClick={() => void retryRemaining()}>Retry remaining ({jobRemaining.length})</Button>}
            </div>
          </div>}

          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
            <div className="space-y-1">
              <Label htmlFor={`${id}-job-batch-size`} className="text-xs">Batch size (1–10)</Label>
              <Input id={`${id}-job-batch-size`} type="number" min={1} max={10} value={jobSettings.batchSize}
                disabled={jobRunning} onChange={event => setJobSettings(value => ({ ...value, batchSize: Math.max(1, Math.min(10, Number(event.target.value) || 1)) }))} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={`${id}-job-timeout`} className="text-xs">Batch timeout (60–600 s)</Label>
              <Input id={`${id}-job-timeout`} type="number" min={60} max={600} value={jobSettings.batchTimeoutSeconds}
                disabled={jobRunning} onChange={event => setJobSettings(value => ({ ...value, batchTimeoutSeconds: Math.max(60, Math.min(600, Number(event.target.value) || 60)) }))} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={`${id}-job-retries`} className="text-xs">Max retries (0–3)</Label>
              <Input id={`${id}-job-retries`} type="number" min={0} max={3} value={jobSettings.maxRetries}
                disabled={jobRunning} onChange={event => setJobSettings(value => ({ ...value, maxRetries: Math.max(0, Math.min(3, Number(event.target.value) || 0)) }))} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={`${id}-job-failure-mode`} className="text-xs">On batch failure</Label>
              <select id={`${id}-job-failure-mode`} value={jobSettings.failureMode} disabled={jobRunning}
                onChange={event => setJobSettings(value => ({ ...value, failureMode: event.target.value as "continue" | "stop" }))}
                className="min-h-11 w-full rounded-md border border-slate-300 bg-white px-2 text-sm">
                <option value="continue">Continue to next batch</option>
                <option value="stop">Stop job</option>
              </select>
            </div>
            <div className="space-y-1">
              <Label htmlFor={`${id}-job-max-tokens`} className="text-xs">Max tokens (100–2000)</Label>
              <Input id={`${id}-job-max-tokens`} type="number" min={100} max={2000} value={jobSettings.maxTokens}
                disabled={jobRunning} onChange={event => setJobSettings(value => ({ ...value, maxTokens: Math.max(100, Math.min(2000, Number(event.target.value) || 100)) }))} />
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" onClick={() => void startBackgroundJob(unchunkedNumbers)}
              disabled={mutationsBlocked || jobRunning || unchunkedNumbers.length === 0} className="min-h-11 bg-indigo-600 text-white hover:bg-indigo-700">
              <Play className="mr-1 h-4 w-4" aria-hidden="true" /> Start job: all remaining ({unchunkedNumbers.length})
            </Button>
            <Button type="button" variant="outline" onClick={() => void startBackgroundJob(selectedPages)}
              disabled={mutationsBlocked || jobRunning || selectedPages.length === 0} className="min-h-11">
              Start job: selected ({selectedPages.length})
            </Button>
          </div>

          <div className="flex flex-wrap items-center gap-3 rounded-md border border-slate-200 bg-white p-3 text-xs text-slate-600">
            <label className="inline-flex min-h-9 items-center gap-2">
              <input type="checkbox" checked={autoPoll} disabled={!pollingReady}
                onChange={event => setAutoPoll(event.target.checked)} /> Auto-poll all page reads
            </label>
            <label htmlFor={`${id}-poll-interval`} className="inline-flex items-center gap-2">Interval
              <select id={`${id}-poll-interval`} value={pollInterval} disabled={!pollingReady}
                onChange={event => setPollInterval(Number(event.target.value))}
                className="min-h-9 rounded border border-slate-300 bg-white px-1">
                {POLL_INTERVAL_OPTIONS.map(interval => <option key={interval} value={interval}>{interval / 1000}s</option>)}
              </select>
            </label>
            <Button type="button" size="sm" variant="outline" onClick={() => void pollNow(true)} disabled={!pollingReady || pollingBusy}>
              {pollingBusy ? <Loader2 aria-hidden="true" className="mr-1 h-4 w-4 animate-spin" /> : null} Poll now
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={() => setAutoPoll(false)} disabled={!autoPoll}>Stop polling</Button>
            <span>Last update: {lastPolledAt ? lastPolledAt.toLocaleTimeString() : "not yet"}</span>
            <span>Next poll: {nextPollLabel}</span>
          </div>
          <p className="text-xs text-slate-600">
            These controls apply to the whole page: document status, chunks, chunking progress and the chunk job.
            Stop polling stops all periodic page reads but does not cancel the worker.
            {!autoPoll ? " Manual updates only: use Poll now or Refresh." : ""}
          </p>
          {(jobError || pollingError) && <p role="alert" className="text-xs text-rose-700">{jobError || pollingError}</p>}
          {!job && <p className="text-xs text-slate-600">No active background job. Start one above; the legacy synchronous buttons remain available for compatibility.</p>}
        </CardContent>
      </Card>

      {document?.failureReason && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-700">{document.failureReason}</p>}
      {readFailed && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-700">Could not refresh processing evidence. Displayed statuses and counts may be out of date; mutation controls stay disabled until refresh succeeds.</p>}
      {processing && <p className="text-sm text-slate-600">{busy === "chunking" ? "Chunking requested pages. The server checkpoints completed batches; progress below reflects saved pages only."
        : busy === "embedding" ? "Embedding accepted chunks. Completion has not yet been confirmed."
        : busy === "cancelling" ? "Checking retained work after cancellation…"
        : "Document processing is active. Review and processing controls are temporarily unavailable."}</p>}
      <p role="status" className="text-sm text-slate-600">{notice}</p>
      {!canEdit && <p className="text-sm text-slate-600">Editor or owner access must be verified before reviewing, resetting or processing this document.</p>}
      <p className="text-sm text-slate-600">{embeddedChunks.length} embedded chunks. A document is indexed only when accepted chunks are embedded and pending review is finished. Embedding a reviewed subset does not imply all document pages were selected.</p>
      <details className="rounded-lg border p-4">
        <summary className="min-h-11 cursor-pointer font-semibold">Document reset controls</summary>
        <p className="my-3 text-sm">Resetting pages removes their chunks, including review and embeddings. Re-chunking starts only when you explicitly choose Chunk Selected afterwards.</p>
        <fieldset disabled={mutationsBlocked} className="flex flex-wrap gap-3"><legend className="mb-2 font-medium">Pages to reset for re-chunking</legend>
          {pages.map(page => <label key={page.page} className="inline-flex min-h-12 items-center gap-2">
            <input type="checkbox" name="resetPages" checked={resetSelection.includes(page.page)} onChange={event => setResetSelection(current => event.target.checked
              ? [...current, page.page].sort((a, b) => a - b) : current.filter(value => value !== page.page))} /> Reset page {page.page}
          </label>)}
        </fieldset>
        <div className="mt-3 flex flex-wrap gap-3">
          <Button type="button" variant="outline" disabled={mutationsBlocked || !resetSelection.length || resetSelection.length > 1000} onClick={() => resetPages([...resetSelection])}>Reset selected pages</Button>
          <Button type="button" variant="outline" disabled={mutationsBlocked || !pages.length || pages.length > 1000} onClick={() => resetPages(pages.map(page => page.page))}>Reset all pages</Button>
          <Button type="button" variant="outline" disabled={mutationsBlocked || !embeddedChunks.length} onClick={resetEmbeddings}>Reset embeddings</Button>
          <Button type="button" variant="outline" disabled={mutationsBlocked} onClick={reparseDocument}>Re-parse source PDF</Button>
        </div>
        {pages.length > 1000 && <p className="mt-2 text-sm">Reset at most 1000 selected pages per request.</p>}
      </details>
      <ConfirmDialog request={confirmation} onDismiss={() => setConfirmation(null)} />

      {/* Progress bar */}
      <div className="lg:col-span-12">
        <Card className="border-slate-200 shadow-sm">
          <CardHeader className="px-5 py-4 border-b border-slate-100">
            <div className="flex items-center justify-between">
               <h2 className="text-sm font-semibold text-slate-900">Chunking Progress</h2>
              <span className="text-xs font-mono text-slate-500">
                {progress.chunkedPages}/{progress.totalPages} pages ({progressPercent}%)
              </span>
            </div>
          </CardHeader>
          <CardContent className="p-5">
            <progress className="w-full accent-indigo-600" max={100} value={progress ? progressPercent : undefined} aria-label="Pages processed for chunking" />
            <div className="flex justify-between text-xs text-slate-500 mt-2">
              <span>Chunking status: {chunkingLabel}</span>
              <span>{progress.totalPages} total pages</span>
            </div>
            <p className="mt-2 text-xs text-slate-500">Processed pages include explicitly skipped pages; they do not all contain chunks.</p>
          </CardContent>
        </Card>
      </div>

      {/* Tabbed interface */}
      <div className="space-y-4">
        <Tabs defaultValue="document">
          <TabsList aria-label="Document review views" className="grid w-full grid-cols-1 h-auto! min-h-11 sm:grid-cols-3">
            <TabsTrigger value="document" className="flex-1">
              <FileText aria-hidden="true" className="w-4 h-4 mr-2" /> Document Text
            </TabsTrigger>
            <TabsTrigger value="chunks" className="flex-1">
              <LayoutList aria-hidden="true" className="w-4 h-4 mr-2" /> Pending Chunks ({filteredPending.length})
            </TabsTrigger>
            <TabsTrigger value="reviewed" className="flex-1">
              <Clock aria-hidden="true" className="w-4 h-4 mr-2" /> Reviewed ({acceptedAndEmbedded.length + rejectedChunks.length})
            </TabsTrigger>
          </TabsList>

          {/* --- Document Text Tab --- */}
          <TabsContent value="document" className="space-y-4">
            <div className="grid grid-cols-1 lg:grid-cols-4 gap-6">
              <div className="lg:col-span-3">
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <h2 className="text-sm font-semibold text-slate-900">Document Text (Parsed)</h2>
                  </CardHeader>
                  <CardContent className="p-0">
                    <div className="h-[500px] overflow-y-auto p-5 font-mono text-sm text-slate-600 whitespace-pre-wrap border-b border-slate-100">
                      {pages.length > 0 ? (
                        <div>
                          {getPaged(pages, currentDocPage, DOC_PAGE_SIZE).map(p => (
                            <div key={p.page} className="mb-4 pb-4 border-b border-slate-100 last:border-b-0">
                              <div className="flex items-center gap-2 mb-2">
                                <input
                                  id={`${id}-text-page-${p.page}`}
                                  name="pages"
                                  type="checkbox"
                                  checked={selectedPages.includes(p.page)}
                                  onChange={() => togglePage(p.page)}
                                  disabled={mutationsBlocked || chunkedSet.has(p.page)}
                                  className="w-4 h-4 rounded accent-indigo-600 disabled:opacity-50"
                                />
                                <label htmlFor={`${id}-text-page-${p.page}`} className="inline-flex min-h-11 items-center text-xs font-bold text-slate-500 uppercase">Page {p.page}</label>
                                {chunkedSet.has(p.page) && (
                                  <Badge variant="secondary" className="px-2 py-0.5 text-xs">Processed</Badge>
                                )}
                              </div>
                              <p className="text-sm text-slate-700 whitespace-pre-wrap">{p.text}</p>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <p className="text-slate-500">{noParsedText}</p>
                      )}
                    </div>
                    {pages.length > DOC_PAGE_SIZE && (
                      <div className="flex items-center justify-between p-4 border-t border-slate-100">
                        <Button type="button" size="sm" variant="outline" onClick={() => setDocPage(Math.max(1, currentDocPage - 1))} disabled={currentDocPage === 1}>
                          Previous
                        </Button>
                        <span className="text-xs text-slate-500">Pages {((currentDocPage - 1) * DOC_PAGE_SIZE) + 1}-{Math.min(currentDocPage * DOC_PAGE_SIZE, pages.length)} of {pages.length}</span>
                        <Button type="button" size="sm" variant="outline" onClick={() => setDocPage(Math.min(Math.ceil(pages.length / DOC_PAGE_SIZE), currentDocPage + 1))} disabled={currentDocPage === Math.ceil(pages.length / DOC_PAGE_SIZE)}>
                          Next
                        </Button>
                      </div>
                    )}
                  </CardContent>
                </Card>
              </div>
              <div className="lg:col-span-1 space-y-4">
                <Card className="border-slate-200 shadow-sm bg-slate-50">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="flex items-center gap-2">
                        <Brain aria-hidden="true" className="w-5 h-5 text-slate-500" />
                        <h2 className="text-sm font-semibold text-slate-900">Chunker Guidance</h2>
                      </div>
                      <div className="flex flex-wrap gap-1">
                        <Button type="button" size="sm" onClick={() => setPageSelection(unchunkedNumbers)} disabled={mutationsBlocked || unchunkedPages.length === 0}>Select unchunked</Button>
                        <Button type="button" size="sm" variant="outline" onClick={() => setPageSelection([])} disabled={mutationsBlocked || selectedPages.length === 0}>Clear</Button>
                      </div>
                    </div>
                  </CardHeader>
                  <CardContent className="p-5">
                    <Label htmlFor={`${id}-guidance`}>Chunker guidance</Label>
                    <Textarea id={`${id}-guidance`} name="guidance" value={guidance} onChange={e => setGuidance(e.target.value)} disabled={mutationsBlocked} maxLength={4000} rows={4} className="mt-2 text-base! text-slate-600" />
                    <div className="flex gap-2 mt-4">
                      <Label htmlFor={`${id}-threshold`} className="text-xs text-slate-500">Min confidence:</Label>
                      <input
                        id={`${id}-threshold`}
                        name="threshold"
                        type="range"
                        min="0"
                        max="1"
                        step="0.05"
                        value={threshold}
                        disabled={mutationsBlocked}
                        onChange={e => setThreshold(parseFloat(e.target.value))}
                        className="w-24 accent-indigo-600"
                      />
                      <span className="text-xs text-slate-500">{threshold.toFixed(2)}</span>
                    </div>
                  </CardContent>
                </Card>

                {/* Page selection list */}
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <h2 className="text-sm font-semibold text-slate-900">Pages to Chunk</h2>
                  </CardHeader>
                  <CardContent className="p-4">
                    <fieldset className="space-y-2 max-h-[400px] overflow-y-auto pr-2">
                      <legend className="sr-only">Pages to chunk</legend>
                      {pages.map(p => {
                        const isChunked = chunkedSet.has(p.page);
                        return (
                          <div
                            key={p.page}
                            className={`flex items-center justify-between p-2 rounded border ${isChunked ? "border-indigo-200 bg-indigo-50" : "border-transparent hover:bg-slate-50"}`}
                          >
                            <div className="flex w-full items-center gap-2">
                              <input
                                id={`${id}-select-page-${p.page}`}
                                name="pages"
                                type="checkbox"
                                checked={selectedPages.includes(p.page)}
                                onChange={() => togglePage(p.page)}
                                disabled={mutationsBlocked || isChunked}
                                className="w-4 h-4 rounded accent-indigo-600 disabled:opacity-50"
                              />
                              <label htmlFor={`${id}-select-page-${p.page}`} className="inline-flex min-h-11 flex-1 items-center text-xs font-mono font-bold text-slate-600">Page {p.page}</label>
                              {isChunked && (
                                <Badge variant="secondary" className="px-2 py-0.5 text-xs">Processed</Badge>
                              )}
                            </div>
                          </div>
                        );
                      })}
                      {pages.length === 0 && (
                        <p className="text-xs text-slate-400 text-center py-4">No pages detected.</p>
                      )}
                    </fieldset>
                  </CardContent>
                </Card>
              </div>
            </div>
          </TabsContent>

          {/* --- Pending Chunks Tab (with filters & grouping) --- */}
          <TabsContent value="chunks" className="space-y-4">
            {/* Filter controls */}
            <Card className="border-slate-200 shadow-sm">
              <CardHeader className="px-5 py-3 border-b border-slate-100">
                <h2 className="text-sm font-semibold text-slate-900">Filters & Grouping</h2>
              </CardHeader>
              <CardContent className="p-4">
                <search>
                <form method="get" action={`/api/workspaces/${workspaceId}/chunks`} onSubmit={event => event.preventDefault()} className="grid grid-cols-1 md:grid-cols-4 gap-4">
                  <div className="flex items-center gap-2">
                    <Filter aria-hidden="true" className="w-4 h-4 text-slate-500" />
                    <Label htmlFor={`${id}-score-filter`} className="text-xs text-slate-500">Min score:</Label>
                    <Input
                      id={`${id}-score-filter`}
                      name="minScore"
                      type="number"
                      min="0"
                      max="1"
                      step="0.05"
                      value={filterScore ?? ""}
                      onChange={e => { setFilterScore(e.target.value === "" ? null : e.target.valueAsNumber); setChunkPage(1); }}
                      placeholder="0.00"
                      className="w-20"
                    />
                  </div>
                  <div className="flex items-center gap-2">
                    <Label htmlFor={`${id}-type-filter`} className="text-xs text-slate-500">Type:</Label>
                    <select
                      id={`${id}-type-filter`}
                      name="type"
                      value={filterType ?? ""}
                      onChange={e => { setFilterType(e.target.value === "" ? null : e.target.value); setChunkPage(1); }}
                      className="border border-slate-300 rounded px-2 py-1 text-xs bg-white"
                    >
                      <option value="">All types</option>
                      {uniqueTypes.map(t => (
                        <option key={t} value={t}>{t}</option>
                      ))}
                    </select>
                  </div>
                  <div className="flex items-center gap-2">
                    <Label htmlFor={`${id}-page-filter`} className="text-xs text-slate-500">Page:</Label>
                    <select
                      id={`${id}-page-filter`}
                      name="page"
                      value={filterPage ?? ""}
                      onChange={e => { setFilterPage(e.target.value === "" ? null : parseInt(e.target.value)); setChunkPage(1); }}
                      className="border border-slate-300 rounded px-2 py-1 text-xs bg-white"
                    >
                      <option value="">All pages</option>
                      {uniquePages.map(p => (
                        <option key={p} value={p}>p.{p}</option>
                      ))}
                    </select>
                  </div>
                  <div className="flex items-center gap-2">
                    <Label htmlFor={`${id}-group-filter`} className="text-xs text-slate-500">Group by:</Label>
                    <select
                      id={`${id}-group-filter`}
                      name="groupBy"
                      value={groupBy}
                      onChange={e => setGroupBy(e.target.value as "none" | "page" | "section")}
                      className="border border-slate-300 rounded px-2 py-1 text-xs bg-white"
                    >
                      <option value="none">None</option>
                      <option value="page">By Page</option>
                      <option value="section">By Section</option>
                    </select>
                  </div>
                </form>
                </search>
              </CardContent>
            </Card>

            {/* Chunks display */}
            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
              <div className="lg:col-span-2">
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <div className="flex items-center justify-between">
                      <h2 className="text-sm font-semibold text-slate-900">Pending Chunks</h2>
                      <span className="text-xs font-mono text-slate-500">{filteredPending.length} chunks</span>
                    </div>
                  </CardHeader>
                  <CardContent className="p-0">
                    <div className="max-h-[600px] overflow-y-auto pr-2">
                      {groupedPending ? (
                        // Grouped view
                        groupedPending.map(([groupName, groupChunks]) => (
                          <div key={groupName} className="mb-4 border border-slate-200 rounded-xl bg-white shadow-sm">
                            <div className="px-4 py-3 border-b border-slate-100 bg-slate-50">
                              <div className="flex items-center gap-2">
                                <h3 className="text-xs font-bold text-slate-900 uppercase">{groupName}</h3>
                                <span className="text-xs text-slate-500">{groupChunks.length} chunk(s)</span>
                              </div>
                            </div>
                            <div className="p-4 space-y-3">
                              {groupChunks.map(c => renderChunkCard(c))}
                            </div>
                          </div>
                        ))
                      ) : (
                        // Ungrouped list
                        <div className="space-y-3">
                          {pagedPending.map(c => renderChunkCard(c))}
                        </div>
                      )}
                      {filteredPending.length === 0 && (
                        <div className="flex flex-col items-center justify-center py-12 text-center">
                          <ShieldCheck aria-hidden="true" className="w-12 h-12 text-slate-300 mx-auto mb-4" />
                          <p className="text-sm text-slate-500">No pending chunks to review.</p>
                          <p className="text-xs text-slate-400 mt-2">Try adjusting your filters or chunk more pages.</p>
                        </div>
                      )}
                    </div>
                    {/* Pagination controls (only for ungrouped view) */}
                    {groupBy === "none" && filteredPending.length > PAGE_SIZE && (
                      <div className="flex items-center justify-between p-4 border-t border-slate-100">
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          onClick={() => setChunkPage(Math.max(1, currentChunkPage - 1))}
                          disabled={currentChunkPage === 1}
                        >
                          Previous
                        </Button>
                        <span className="text-xs text-slate-500">Page {currentChunkPage} of {Math.ceil(filteredPending.length / PAGE_SIZE)}</span>
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          onClick={() => setChunkPage(Math.min(Math.ceil(filteredPending.length / PAGE_SIZE), currentChunkPage + 1))}
                          disabled={currentChunkPage === Math.ceil(filteredPending.length / PAGE_SIZE)}
                        >
                          Next
                        </Button>
                      </div>
                    )}
                  </CardContent>
                </Card>
              </div>
              <div className="lg:col-span-1">
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <h2 className="text-sm font-semibold text-slate-900">Score Legend</h2>
                  </CardHeader>
                  <CardContent className="p-4">
                    <div className="space-y-3 text-sm text-slate-600">
                      <div className="flex items-center gap-2">
                        <span className="text-indigo-600 font-bold">0.85 - 1.0</span>
                        <span>High confidence - auto-accept candidate</span>
                      </div>
                      <div className="flex items-center gap-2">
                        <span className="text-amber-600 font-bold">0.60 - 0.84</span>
                        <span>Medium confidence - review carefully</span>
                      </div>
                      <div className="flex items-center gap-2">
                        <span className="text-rose-600 font-bold">0.00 - 0.59</span>
                        <span>Low confidence - verify against source</span>
                      </div>
                    </div>
                    <div className="pt-3 border-t border-slate-100">
                      <p className="text-xs text-slate-500">Scores are reported by the chunker and may be inferred from recommendation strength. They are not proof of clinical correctness or a substitute for source review.</p>
                    </div>
                  </CardContent>
                </Card>
              </div>
            </div>
          </TabsContent>

          {/* --- Reviewed Chunks Tab --- */}
          <TabsContent value="reviewed" className="space-y-4">
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
              {/* Accepted chunks */}
              <div>
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <div className="flex items-center justify-between">
                      <h2 className="text-sm font-semibold text-emerald-700">Accepted &amp; Embedded Chunks</h2>
                      <div className="flex items-center gap-2">
                        <span className="text-xs font-mono text-emerald-600">{accepted} awaiting embedding · {embeddedChunks.length} embedded</span>
                        <Button
                          type="button"
                          aria-label="Embed accepted chunks"
                          onClick={() => void generateEmbeddings()}
                          disabled={mutationsBlocked || accepted === 0}
                          size="sm"
                          variant="outline"
                          className="min-h-11 text-xs px-2 py-1"
                        >
                          {busy === "embedding" ? <Loader2 aria-hidden="true" className="w-3 h-3 mr-1 animate-spin" /> : <Wand2 aria-hidden="true" className="w-3 h-3 mr-1" />} Embed
                        </Button>
                      </div>
                    </div>
                  </CardHeader>
                  <CardContent className="p-0">
                    <div className="max-h-[600px] overflow-y-auto pr-2 space-y-3">
                      {getPaged(acceptedAndEmbedded, currentReviewedPage, PAGE_SIZE).map(c => renderReviewedCard(c, c.status === "embedded" ? "embedded" : "accepted"))}
                      {acceptedAndEmbedded.length === 0 && (
                        <div className="flex flex-col items-center justify-center py-8 text-center">
                          <p className="text-sm text-slate-500">No accepted or embedded chunks.</p>
                        </div>
                      )}
                    </div>
                    {acceptedAndEmbedded.length > PAGE_SIZE && (
                      <div className="flex items-center justify-between p-4 border-t border-slate-100">
                        <Button type="button" size="sm" variant="outline" onClick={() => setReviewedPage(Math.max(1, currentReviewedPage - 1))} disabled={currentReviewedPage === 1}>
                          Previous
                        </Button>
                        <span className="text-xs text-slate-500">Page {currentReviewedPage} of {Math.ceil(acceptedAndEmbedded.length / PAGE_SIZE)}</span>
                        <Button type="button" size="sm" variant="outline" onClick={() => setReviewedPage(Math.min(Math.ceil(acceptedAndEmbedded.length / PAGE_SIZE), currentReviewedPage + 1))} disabled={currentReviewedPage === Math.ceil(acceptedAndEmbedded.length / PAGE_SIZE)}>
                          Next
                        </Button>
                      </div>
                    )}
                  </CardContent>
                </Card>
              </div>

              {/* Rejected chunks */}
              <div>
                <Card className="border-slate-200 shadow-sm">
                  <CardHeader className="px-5 py-4 border-b border-slate-100">
                    <div className="flex items-center justify-between">
                      <h2 className="text-sm font-semibold text-rose-700">Rejected Chunks</h2>
                      <span className="text-xs font-mono text-rose-500">{rejected}</span>
                    </div>
                  </CardHeader>
                  <CardContent className="p-0">
                    <div className="max-h-[600px] overflow-y-auto pr-2 space-y-3">
                      {rejectedChunks.map(c => renderReviewedCard(c, "rejected"))}
                      {rejectedChunks.length === 0 && (
                        <div className="flex flex-col items-center justify-center py-8 text-center">
                          <p className="text-sm text-slate-500">No rejected chunks.</p>
                        </div>
                      )}
                    </div>
                  </CardContent>
                </Card>
              </div>
            </div>
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );

  function chunkName(c: Chunk) {
    return `chunk ${allChunks.findIndex(chunk => chunk.id === c.id) + 1} on page ${c.page}`;
  }

  function toggleChunkText(chunkId: string) {
    setExpandedChunks(previous => previous.includes(chunkId) ? previous.filter(id => id !== chunkId) : [...previous, chunkId]);
  }

  function renderVerbatimBadge(c: Chunk) {
    const color = c.verbatim === true ? "text-emerald-600 border-emerald-200 bg-emerald-50"
      : c.verbatim === false ? "text-rose-600 border-rose-200 bg-rose-50" : "text-slate-600 border-slate-200 bg-slate-50";
    return <Badge variant="outline" className={`px-2 py-0.5 text-xs ${color}`}>
      {c.verbatim === true ? "Verbatim ✓" : c.verbatim === false ? "Not Verbatim ✗" : "Verbatim check unknown"}
    </Badge>;
  }

  // --- Helper: render a single pending chunk card ---
  function renderChunkCard(c: Chunk) {
    const showFullText = expandedChunks.includes(c.id);
    return (
      <article key={c.id} aria-label={chunkName(c)} className="border border-slate-200 rounded-xl p-4 bg-white shadow-sm hover:shadow-md transition-all duration-200 group">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <Badge variant="secondary" className={`px-2.5 py-1 text-xs ${STATUS_COLORS[c.status] ?? ""}`}>{STATUS_COLORS[c.status] ? c.status : "Unknown review status"}</Badge>
            <span className="text-xs font-medium text-slate-600">p.{c.page}</span>
            {c.sectionHierarchy && <span className="text-xs text-slate-400">· {c.sectionHierarchy}</span>}
            {c.section && !c.sectionHierarchy && <span className="text-xs text-slate-400">· {c.section}</span>}
            {c.year && <span className="text-xs text-slate-400">· {c.year}</span>}
            {c.strength && <span className="text-xs text-slate-400">· {c.strength}</span>}
            {c.certainty && <span className="text-xs text-slate-400">· {c.certainty}</span>}
            {c.type === "front_matter" && <Badge variant="outline" className="px-2 py-0.5 text-xs text-rose-600 border-rose-200 bg-rose-50">Front Matter</Badge>}
            {c.reReviewRequired && <Badge variant="outline" className="px-2 py-0.5 text-xs text-amber-700 border-amber-300 bg-amber-50">Needs re-review</Badge>}
          </div>
          <div className="flex items-center gap-1">
            <span className={`text-xs font-mono font-bold ${scoreColor(c.confidence)}`}>{formatScore(c.confidence)}</span>
            {renderVerbatimBadge(c)}
          </div>
        </div>
        {c.reReviewRequired && c.validationError && (
          <div className="mb-3 p-3 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
            <span className="font-semibold">Source changed:</span> {c.validationError}
          </div>
        )}
        {c.verbatim === false && c.validationError && (
          <div className="mb-3 p-3 bg-rose-50 border border-rose-100 rounded text-xs text-rose-700">
            <span className="font-semibold">Validation warning:</span> {c.validationError}
          </div>
        )}
        <div className="mb-3">
          <p id={`${id}-chunk-text-${c.id}`} className={`text-sm text-slate-700 leading-relaxed whitespace-pre-wrap font-mono ${showFullText ? "" : "line-clamp-6"}`}>
            {c.text}
          </p>
          {c.text.length > 200 && (
            <Button type="button" size="sm" variant="ghost" aria-label={`${showFullText ? "Show less" : "Show more"} of ${chunkName(c)}`} aria-expanded={showFullText} aria-controls={`${id}-chunk-text-${c.id}`} onClick={() => toggleChunkText(c.id)} className="text-xs text-indigo-600 hover:bg-indigo-50 px-2 py-1">
              {showFullText ? "Show less" : "Show more"}
            </Button>
          )}
        </div>
        <div className="flex gap-2">
          <Button type="button" size="sm" aria-label={`Accept ${chunkName(c)}`} disabled={mutationsBlocked} onClick={() => void reviewChunk(c.id, "accepted")} className="bg-emerald-700 hover:bg-emerald-800 text-white px-3 py-1.5 text-xs font-medium shadow-sm">
            <CheckCircle aria-hidden="true" className="w-3 h-3 mr-1" /> Accept
          </Button>
          <Button type="button" size="sm" variant="outline" aria-label={`Reject ${chunkName(c)}`} disabled={mutationsBlocked} onClick={() => void reviewChunk(c.id, "rejected")} className="border-rose-200 text-rose-600 hover:bg-rose-50 px-3 py-1.5 text-xs font-medium shadow-sm">
            <X aria-hidden="true" className="w-3 h-3 mr-1" /> Reject
          </Button>
          <Button type="button" size="sm" variant="ghost" aria-label={`Reset ${chunkName(c)}`} disabled={mutationsBlocked} onClick={() => void reviewChunk(c.id, "pending")} className="text-slate-500 hover:bg-slate-100 px-3 py-1.5 text-xs font-medium shadow-sm">
            Reset
          </Button>
        </div>
      </article>
    );
  }

  // --- Helper: render a reviewed chunk card ---
  function renderReviewedCard(c: Chunk, status: "accepted" | "rejected" | "embedded") {
    const showFullText = expandedChunks.includes(c.id);
    const statusColor = STATUS_COLORS[status];
    return (
      <article key={c.id} aria-label={chunkName(c)} className={`border rounded-xl p-4 bg-white shadow-sm ${status === "rejected" ? "border-rose-200 bg-rose-50" : "border-emerald-200 bg-emerald-50"}`}>
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <Badge className={`px-2.5 py-1 text-xs ${statusColor}`}>{status}</Badge>
            <span className="text-xs font-medium text-slate-600">p.{c.page}</span>
            {c.sectionHierarchy && <span className="text-xs text-slate-400">· {c.sectionHierarchy}</span>}
            {c.section && !c.sectionHierarchy && <span className="text-xs text-slate-400">· {c.section}</span>}
            {c.year && <span className="text-xs text-slate-400">· {c.year}</span>}
            {c.strength && <span className="text-xs text-slate-400">· {c.strength}</span>}
            {c.certainty && <span className="text-xs text-slate-400">· {c.certainty}</span>}
            {c.type === "front_matter" && <Badge variant="outline" className="px-2 py-0.5 text-xs text-rose-600 border-rose-200 bg-rose-50">Front Matter</Badge>}
            {c.reReviewRequired && <Badge variant="outline" className="px-2 py-0.5 text-xs text-amber-700 border-amber-300 bg-amber-50">Needs re-review</Badge>}
          </div>
          <span className={`text-xs font-mono font-bold ${scoreColor(c.confidence)}`}>{formatScore(c.confidence)}</span>
        </div>
        <div className="mb-3">
          <p id={`${id}-chunk-text-${c.id}`} className={`text-sm text-slate-700 leading-relaxed whitespace-pre-wrap font-mono ${showFullText ? "" : "line-clamp-6"}`}>
            {c.text}
          </p>
          {c.text.length > 200 && (
            <Button type="button" size="sm" variant="ghost" aria-label={`${showFullText ? "Show less" : "Show more"} of ${chunkName(c)}`} aria-expanded={showFullText} aria-controls={`${id}-chunk-text-${c.id}`} onClick={() => toggleChunkText(c.id)} className="text-xs text-indigo-600 hover:bg-indigo-50 px-2 py-1">
              {showFullText ? "Show less" : "Show more"}
            </Button>
          )}
        </div>
        {renderVerbatimBadge(c)}
        {status === "embedded" && <p className="mt-2 text-xs text-slate-600">Resetting removes this chunk from the index until it is accepted and embedded again.</p>}
        <Button type="button" size="sm" variant="ghost" aria-label={`Reset to Pending ${chunkName(c)}`} disabled={mutationsBlocked} onClick={() => void reviewChunk(c.id, "pending")} className="text-slate-500 hover:bg-slate-100 px-3 py-1.5 text-xs font-medium">
          Reset to Pending
        </Button>
      </article>
    );
  }
}
