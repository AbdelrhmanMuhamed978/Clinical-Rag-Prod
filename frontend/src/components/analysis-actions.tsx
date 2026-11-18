"use client";

import { useEffect, useId, useRef, useState } from "react";
import { Copy, Download, Printer } from "lucide-react";
import { formatAnalysisExport, formatAnalysisPrintHtml, type AnalysisExportOptions, type PipelineResult } from "@/lib/analysis";
import { Button } from "@/components/ui/button";

export function AnalysisActions({ result, protectPatientContext = false, sourceText = "", configVersion }: {
  result: PipelineResult;
  protectPatientContext?: boolean;
  sourceText?: string;
  configVersion?: number | null;
}) {
  const id = useId();
  const target = useRef<HTMLDivElement>(null);
  const timers = useRef(new Set<ReturnType<typeof setTimeout>>());
  const urls = useRef(new Set<string>());
  const feedbackTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [feedback, setFeedback] = useState("");
  const [includeCaseText, setIncludeCaseText] = useState(false);
  const lastFeedback = useRef("");
  const copyOwnership = useRef({ attempt: 0 });
  const canIncludeCaseText = sourceText.trim().length > 0;

  const exportOptions: AnalysisExportOptions = {
    protectPatientContext,
    sourceText: canIncludeCaseText ? sourceText : null,
    includeCaseText: canIncludeCaseText && includeCaseText,
    configVersion: configVersion ?? result.configVersion,
  };

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
      await navigator.clipboard.writeText(formatAnalysisExport(result, exportOptions));
      if (attempt === ownership.attempt) report(includeCaseText
        ? "Result copied, including the original input you explicitly selected. Rejected drafts and patient-specific checks were omitted."
        : "Result copied. Case input, rejected drafts and patient-specific checks were omitted.");
    } catch {
      if (attempt === ownership.attempt) report("Copy failed. Allow clipboard access or use an export button.");
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

  function download(content: string, type: string, name: string) {
    try {
      const url = URL.createObjectURL(new Blob([content], { type }));
      urls.current.add(url);
      const link = document.createElement("a");
      link.href = url; link.download = name;
      document.body.append(link); link.click(); link.remove();
      const timer = setTimeout(() => {
        URL.revokeObjectURL(url); urls.current.delete(url); timers.current.delete(timer);
      }, 1000);
      timers.current.add(timer);
      report(`Download requested: ${name}.`);
    } catch {
      report("Export failed. Try Copy result instead.");
    }
  }

  function exportMarkdown() {
    download(formatAnalysisExport(result, exportOptions), "text/markdown;charset=utf-8", "guideline-result.md");
  }

  function exportPdf() {
    try {
      const printWindow = window.open("", "_blank");
      if (!printWindow) {
        report("The print window was blocked. Allow pop-ups, or use Export Markdown.");
        return;
      }
      printWindow.opener = null;
      printWindow.document.open();
      printWindow.document.write(formatAnalysisPrintHtml(result, exportOptions));
      printWindow.document.close();
      printWindow.focus();
      printWindow.print();
      report("Print dialog opened. Choose Save as PDF. Rejected drafts and patient-specific checks are omitted.");
    } catch {
      report("The PDF print view could not be opened. Try Export Markdown.");
    }
  }

  return <div ref={target} id={id} className="space-y-3 border-t border-slate-200 pt-3">
    <div className="flex flex-wrap gap-2">
      <Button type="button" variant="outline" className="min-h-12" {...{ command: "--copy", commandfor: id }} onClick={() => {
        // Exactly one native/fallback path; copy the safe serializer, never innerText.
        if (!("CommandEvent" in window)) void copyResult();
      }}><Copy className="mr-1 h-4 w-4" aria-hidden="true" /> Copy result</Button>
      <Button type="button" variant="outline" className="min-h-12" onClick={exportMarkdown}><Download className="mr-1 h-4 w-4" aria-hidden="true" /> Export Markdown</Button>
      <Button type="button" variant="outline" className="min-h-12" onClick={exportPdf}><Printer className="mr-1 h-4 w-4" aria-hidden="true" /> Export PDF</Button>
    </div>
    {canIncludeCaseText && (
      <label className="flex items-start gap-2 rounded-md border border-slate-200 bg-slate-50 p-2 text-xs text-slate-700">
        <input type="checkbox" className="mt-0.5 size-4" checked={includeCaseText}
          onChange={event => setIncludeCaseText(event.target.checked)} />
        <span>Include the original case/question text in copy and export. Off by default; patient-specific checks are always omitted.</span>
      </label>
    )}
    <p className="text-xs text-slate-600">Exports include verified items, citations, exact quotes, DRAFT labels, configuration version, date and intended-use statement. Rejected drafts, retrieved excerpts and patient-specific checks are omitted. PDF uses the browser print dialog; choose Save as PDF.</p>
    <p role="status" className="text-xs text-slate-600">{feedback}</p>
  </div>;
}
