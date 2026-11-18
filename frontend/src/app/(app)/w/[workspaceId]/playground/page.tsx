"use client";

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useParams } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import { AlertCircle, Bot, Send, Square, Trash2, User } from "lucide-react";
import { toast } from "sonner";

import { postStream } from "@/lib/api";
import { analysisActionError, pipelineStreamError } from "@/lib/analysis-errors";
import { analysesQueryKey, analysisMode, analysisModeLabel, parsePipelineProgress, parsePipelineResult, shouldSubmitOnEnter, verifiedItemCount, withVerifiedTurn, type AnalysisMode, type ConversationTurn, type PipelineProgress, type PipelineResult } from "@/lib/analysis";
import { createRequestScope } from "@/lib/request-scope";
import { useAuth } from "@/providers/AuthProvider";
import { AnalysisResult } from "@/components/analysis-result";
import { MyAnalyses } from "@/components/my-analyses";
import { ProcessingSteps } from "@/components/processing-steps";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

type ChatMessage =
  | { id: string; role: "user"; content: string }
  | { id: string; role: "assistant"; kind: "result"; result: PipelineResult; progress: PipelineProgress[]; protectPatientContext: boolean; sourceText: string }
  | { id: string; role: "assistant"; kind: "attempt"; detail: string; outcome: "failed" | "cancelled"; progress: PipelineProgress[]; mode: AnalysisMode | null };

function newId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
}

export default function PlaygroundPage() {
  const { workspaceId } = useParams<{ workspaceId: string }>();
  const { user } = useAuth();
  // Remount on either identity change; no case, conversation or late response
  // from the previous workspace/user can appear in the new one.
  return user ? <Playground key={`${workspaceId}:${user.uid}`} workspaceId={workspaceId} userId={user.uid} /> : null;
}

function Playground({ workspaceId, userId }: { workspaceId: string; userId: string }) {
  const [question, setQuestion] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [turns, setTurns] = useState<ConversationTurn[]>([]);
  const [progressSteps, setProgressSteps] = useState<PipelineProgress[]>([]);
  const [mode, setMode] = useState<AnalysisMode | null>(null);
  const [notice, setNotice] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const conversationId = useRef<string | null>(null);
  const activeAttempt = useRef<{ progress: PipelineProgress[]; mode: AnalysisMode | null } | null>(null);
  const [requests] = useState(createRequestScope);
  const queryClient = useQueryClient();
  useEffect(() => () => requests.cancel(), [requests]);

  async function run() {
    const currentQuestion = question.trim();
    if (!currentQuestion || requests.active) return;
    const request = requests.start();
    if (!conversationId.current) conversationId.current = newId();

    setMessages((previous) => [...previous, { id: newId(), role: "user", content: currentQuestion }]);
    setProgressSteps([]);
    setMode(null);
    setNotice("Analysis requested. Waiting for pipeline progress.");
    setLoading(true);
    setError(null);
    setQuestion("");

    const received: { result?: PipelineResult } = {};
    const attempt: { progress: PipelineProgress[]; mode: AnalysisMode | null } = { progress: [], mode: null };
    const protectPatientContext = currentQuestion.split(/\s+/).length >= 40 || turns.some(turn => turn.question.split(/\s+/).length >= 40);
    activeAttempt.current = attempt;
    try {
      await postStream(
        `/api/workspaces/${workspaceId}/analyze/stream`,
        {
          message: currentQuestion,
          conversationId: conversationId.current,
          history: turns.slice(-6),
        },
        ({ event, data }) => {
          if (!request.isCurrent()) return;
          if (received.result) throw new Error("Pipeline sent events after its terminal result.");
          if (event === "progress") {
            const progress = parsePipelineProgress(data);
            if (progress.mode) {
              if (attempt.mode && progress.mode !== attempt.mode) throw new Error("Pipeline mode changed within an attempt.");
              attempt.mode = progress.mode; setMode(progress.mode);
            }
            attempt.progress = [...attempt.progress, progress].slice(-200);
            setProgressSteps(attempt.progress);
            setNotice(`${analysisModeLabel(attempt.mode)}. ${progress.state === "reported" ? "Reported" : progress.state}: ${progress.message}`);
          } else if (event === "result") {
            received.result = parsePipelineResult(data);
            const resultMode = analysisMode(received.result);
            if (attempt.mode && resultMode && attempt.mode !== resultMode) throw new Error("Pipeline result mode is inconsistent.");
          }
          else if (event === "error") throw pipelineStreamError(data);
          else throw new Error("The verified pipeline returned an invalid stream event.");
        },
        request.signal,
      );
      if (!request.isCurrent()) return;
      const result = received.result;
      if (!result) throw new Error("The verified pipeline returned no result.");

      setMessages((previous) => [
        ...previous,
        { id: newId(), role: "assistant", kind: "result", result, progress: attempt.progress, protectPatientContext, sourceText: currentQuestion },
      ]);
      setTurns((previous) => withVerifiedTurn(previous, currentQuestion, result));
      const verifiedCount = verifiedItemCount(result);
      const incomplete = result.problems.some(problem => problem.status === "error");
      setNotice(incomplete ? "Analysis incomplete. Some problems failed; their guideline coverage is unknown. Review the result and retry those problems."
        : verifiedCount > 0 ? `${verifiedCount} verified item(s) returned. Read the quotes and cautions.`
        : result.chat ? "Greeting returned. No guideline analysis was performed." : "Analysis returned no verified recommendation.");
      if (verifiedCount > 0) toast.success(`${verifiedCount} verified recommendation${verifiedCount === 1 ? "" : "s"}`);
      else if (incomplete) toast.info("Analysis incomplete. Retry the failed problems; their guideline coverage is unknown.");
      else if (!result.chat) toast.info("No verified recommendation was found in the loaded guidelines.");
    } catch (cause) {
      if (!request.isCurrent()) return;
      const message = analysisActionError(cause);
      setError(message);
      setNotice("");
      setMessages(previous => [...previous, { id: newId(), role: "assistant", kind: "attempt", detail: message,
        outcome: "failed", progress: attempt.progress, mode: attempt.mode }]);
      toast.error(message);
    } finally {
      if (request.isCurrent()) {
        request.finish();
        activeAttempt.current = null;
        setLoading(false);
        void queryClient.invalidateQueries({ queryKey: analysesQueryKey(workspaceId, userId) });
      }
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (shouldSubmitOnEnter(event.nativeEvent)) {
      event.preventDefault();
      event.currentTarget.form?.requestSubmit();
    }
  }

  function clearHistory() {
    requests.cancel();
    setLoading(false);
    setQuestion("");
    setMessages([]);
    setTurns([]);
    setProgressSteps([]);
    setMode(null);
    setNotice("Conversation cleared. Saved analyses are unchanged.");
    activeAttempt.current = null;
    setError(null);
    conversationId.current = null;
  }

  function cancelRun() {
    if (!requests.active) return;
    const attempt = activeAttempt.current;
    requests.cancel();
    setLoading(false);
    setProgressSteps([]);
    setError(null);
    const detail = "Analysis cancelled. No answer was added; started steps are not proof of completion.";
    if (attempt) setMessages(previous => [...previous, { id: newId(), role: "assistant", kind: "attempt", detail,
      outcome: "cancelled", progress: attempt.progress, mode: attempt.mode }]);
    activeAttempt.current = null;
    setNotice(detail);
    void queryClient.invalidateQueries({ queryKey: analysesQueryKey(workspaceId, userId) });
    toast.info("Analysis cancelled. No answer was added.");
  }

  const latest = messages.at(-1);
  const hasCurrentVerifiedItems = !loading && latest?.role === "assistant" && latest.kind === "result" && verifiedItemCount(latest.result) > 0;

  return (
    <div className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900">Guideline Playground</h1>
          {hasCurrentVerifiedItems && <p className="mt-1 text-slate-500">This response contains items that passed quote and scope verification. Read the citations and cautions.</p>}
        </div>
        {messages.length > 0 && (
            <Button type="button" onClick={clearHistory} variant="outline" className="min-h-12">
            <Trash2 className="mr-1 h-3 w-3" aria-hidden="true" /> Clear history
          </Button>
        )}
      </header>

      <MyAnalyses workspaceId={workspaceId} userId={userId} />
      <p role="status" className="text-sm text-slate-600">{notice}</p>

      <div className="max-h-[60dvh] min-h-64 space-y-4 overflow-y-auto px-1">
        {messages.length === 0 && (
          <div className="py-16 text-center text-sm text-slate-500">
            Ask a clinical guideline question or paste a case to begin.
          </div>
        )}

        {messages.map((message) => (
          <div key={message.id} className={`flex gap-3 ${message.role === "user" ? "justify-end" : "justify-start"}`}>
            <div className={`flex max-w-[92%] gap-3 ${message.role === "user" ? "flex-row-reverse" : ""}`}>
              <div className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full ${message.role === "user" ? "bg-indigo-600 text-white" : "bg-slate-200 text-slate-700"}`}>
                {message.role === "user"
                  ? <User className="h-4 w-4" aria-hidden="true" />
                  : <Bot className="h-4 w-4" aria-hidden="true" />}
              </div>

              {message.role === "user" ? (
                <div className="whitespace-pre-wrap break-words rounded-2xl bg-indigo-600 px-4 py-3 text-sm text-white">{message.content}</div>
              ) : (
                <div className="min-w-0 space-y-4 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                  {message.kind === "result" ? <AnalysisResult result={message.result} progress={message.progress} protectPatientContext={message.protectPatientContext} sourceText={message.sourceText} /> : <>
                    <Badge variant="secondary">{analysisModeLabel(message.mode)}</Badge>
                    <p className="text-sm text-slate-700">{message.detail}</p>
                    <ProcessingSteps events={message.progress} outcome={message.outcome} />
                  </>}
                </div>
              )}
            </div>
          </div>
        ))}

        {loading && (
          <div className="space-y-3 rounded-lg border border-slate-200 bg-white p-4">
            <div className="flex flex-wrap items-center gap-3">
              <progress className="w-28 accent-indigo-600" aria-label="Analysis in progress" />
              <Badge variant="secondary">{analysisModeLabel(mode)}</Badge>
              <p className="text-sm text-slate-600">{progressSteps.at(-1)?.message ?? "Waiting for pipeline progress…"}</p>
            </div>
            <ProcessingSteps events={progressSteps} outcome="running" />
          </div>
        )}
      </div>

      <div role="alert" className={error ? "flex items-center gap-2 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700" : undefined}>
        {error && <><AlertCircle className="h-4 w-4" aria-hidden="true" /> {error}</>}
      </div>

      <form method="post" className="border-t border-slate-200 pt-4" onSubmit={(event) => { event.preventDefault(); void run(); }}>
        <Label htmlFor="clinical-question" className="mb-2 block">Clinical question or case</Label>
        <p id="clinical-question-hint" className="mb-2 text-xs text-slate-500">Use de-identified cases only. Enter sends; Shift+Enter adds a line. Memory keeps only the last six question/verified-summary turns ({turns.length}/6), in this tab until cleared.</p>
        <div className="flex flex-wrap gap-3">
          <Textarea
            id="clinical-question"
            name="message"
            aria-describedby="clinical-question-hint"
            autoComplete="off"
            maxLength={100000}
            required
            value={question}
            onChange={(event) => setQuestion(event.target.value)}
            onKeyDown={handleKeyDown}
            rows={3}
            placeholder="Ask a clinical guideline question…"
            className="min-w-0 flex-1 basis-full resize-y text-base sm:basis-0"
          />
          <Button
            type="submit"
            disabled={loading || !question.trim()}
            className="min-h-12 self-end bg-indigo-600 px-4 text-white hover:bg-indigo-700"
          >
            <Send className="mr-2 h-4 w-4" aria-hidden="true" /> Send
          </Button>
          {loading && <Button type="button" variant="outline" onClick={cancelRun} className="min-h-12 self-end"><Square className="mr-1 h-4 w-4" aria-hidden="true" /> Cancel analysis</Button>}
        </div>
      </form>
    </div>
  );
}
