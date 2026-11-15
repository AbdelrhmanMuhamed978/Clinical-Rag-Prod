"use client";

import { useEffect, useId, useRef, useState } from "react";
import { Copy, Download } from "lucide-react";
import { formatAnalysisExport, type PipelineResult } from "@/lib/analysis";
import { Button } from "@/components/ui/button";

export function AnalysisActions({ result, protectPatientContext = false }: { result: PipelineResult; protectPatientContext?: boolean }) {
  const id = useId();
  const target = useRef<HTMLDivElement>(null);
  const timers = useRef(new Set<ReturnType<typeof setTimeout>>());
  const urls = useRef(new Set<string>());
  const feedbackTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [feedback, setFeedback] = useState("");
  const lastFeedback = useRef("");
  const copyOwnership = useRef({ attempt: 0 });

  function report(message: string) {
    if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
    // VoiceOver can skip an identical repeated announcement.
    const next = message === lastFeedback.current ? `${message}\u00a0` : message;
    lastFeedback.current = next;
    setFeedback(next);
    feedbackTimer.current = setTimeout(() => setFeedback(""), 4000);
  }

  async function copyResult() {
    const ownership = copyOwnership.current;
    const attempt = ++ownership.attempt;
    try {
      await navigator.clipboard.writeText(formatAnalysisExport(result, protectPatientContext));
      if (attempt === ownership.attempt) report("Result copied. Rejected drafts and patient-specific checks were omitted.");
    } catch {
      if (attempt === ownership.attempt) report("Copy failed. Allow clipboard access or use Export result.");
    }
  }

  useEffect(() => {
    const element = target.current;
    const onCommand = (event: Event) => {
      if ((event as Event & { command?: string }).command === "--copy") void copyResult();
    };
    element?.addEventListener("command", onCommand);
    return () => element?.removeEventListener("command", onCommand);
  });

  useEffect(() => {
    const activeTimers = timers.current;
    const activeUrls = urls.current;
    const ownership = copyOwnership.current;
    return () => {
      ownership.attempt++;
      if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
      activeTimers.forEach(clearTimeout);
      activeUrls.forEach(url => URL.revokeObjectURL(url));
    };
  }, []);

  function exportResult() {
    try {
      const url = URL.createObjectURL(new Blob([formatAnalysisExport(result, protectPatientContext)], { type: "text/plain;charset=utf-8" }));
      urls.current.add(url);
      const link = document.createElement("a");
      link.href = url; link.download = "guideline-result.txt";
      document.body.append(link); link.click(); link.remove();
      const timer = setTimeout(() => {
        URL.revokeObjectURL(url); urls.current.delete(url); timers.current.delete(timer);
      }, 1000);
      timers.current.add(timer);
      report("Export download requested. Rejected drafts and patient-specific checks were omitted.");
    } catch { report("Export failed. Try Copy result instead."); }
  }

  return <div ref={target} id={id} className="space-y-2 border-t border-slate-200 pt-3">
    <div className="flex flex-wrap gap-2">
      <Button type="button" variant="outline" className="min-h-12" {...{ command: "--copy", commandfor: id }} onClick={() => {
        // Exactly one native/fallback path; copy the safe serializer, never innerText.
        if (!("CommandEvent" in window)) void copyResult();
      }}><Copy className="mr-1 h-4 w-4" aria-hidden="true" /> Copy result</Button>
      <Button type="button" variant="outline" className="min-h-12" onClick={exportResult}><Download className="mr-1 h-4 w-4" aria-hidden="true" /> Export result</Button>
    </div>
    <p className="text-xs text-slate-600">Exports include verified items, citations, exact quotes, coverage and cautions — not rejected drafts or patient-specific checks.</p>
    <p role="status" className="text-xs text-slate-600">{feedback}</p>
  </div>;
}
