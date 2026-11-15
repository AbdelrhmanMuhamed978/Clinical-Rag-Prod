"use client";

import { useId } from "react";
import { AlertCircle, CheckCircle2 } from "lucide-react";
import { analysisMode, analysisModeLabel, notCoveredTitles, verifiedItemCount, type PatientNote, type PipelineProblem, type PipelineProgress, type PipelineResult, type PipelineSource } from "@/lib/analysis";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { AnalysisActions } from "@/components/analysis-actions";
import { ProcessingSteps } from "@/components/processing-steps";

function Sources({ sources }: { sources: PipelineSource[] }) {
  if (sources.length === 0) return null;
  return (
    <details className="rounded-lg border border-slate-200 bg-white">
      <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-slate-700">
        Retrieved sources — not additional answers ({sources.length})
      </summary>
      <ul role="list" className="space-y-2 border-t border-slate-100 p-3">
        {sources.map((source) => (
          <li key={source.id} className="rounded-md bg-slate-50 p-3 text-xs text-slate-600">
            <div className="mb-1 flex flex-wrap items-center gap-2 font-medium text-slate-800">
              <Badge variant="secondary">{source.id}</Badge>
              <span>{source.label}</span>
              <span>p.{source.page}</span>
              {source.draft && <Badge className="bg-amber-100 text-amber-800">DRAFT</Badge>}
            </div>
            <p className="whitespace-pre-wrap">{source.excerpt}</p>
          </li>
        ))}
      </ul>
    </details>
  );
}

function PatientChecks({ notes }: { notes: PatientNote[] }) {
  const headingId = useId();
  return (
    <aside aria-labelledby={headingId} className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-amber-900">
      <h4 id={headingId} className="flex items-center gap-2 text-sm font-semibold">
        <AlertCircle className="h-4 w-4" aria-hidden="true" /> Check against this patient
      </h4>
      <p className="mt-1 text-xs">Deterministic rule checks, not guideline evidence. These patient-specific checks are not saved or added to verified memory.</p>
      <ul className="mt-2 list-disc space-y-2 pl-5 text-xs">
        {notes.map((note, index) => (
          <li key={`${note.id}-${index}`}>
            <strong>{note.title}</strong>{note.level ? ` — ${note.level}` : ""}
            <ul className="mt-1 list-disc space-y-1 pl-4">
              {note.reasons.map((reason, reasonIndex) => <li key={reasonIndex}>{reason}</li>)}
            </ul>
          </li>
        ))}
      </ul>
    </aside>
  );
}

function VerifiedProblem({ problem, showPatientChecks }: { problem: PipelineProblem; showPatientChecks: boolean }) {
  const headingId = useId();
  return (
    <section className="space-y-3" aria-labelledby={headingId}>
      <h3 id={headingId} className="text-base font-semibold text-slate-900">{problem.title}</h3>
      {problem.items.map((item, index) => (
        <Card key={`${item.citation.id}-${index}`} className="border-emerald-200 bg-emerald-50/30">
          <CardHeader className="flex-row items-center gap-2 border-b border-emerald-100 px-4 py-3">
            <CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden="true" />
            <span className="text-sm font-semibold text-emerald-800">Verified recommendation</span>
          </CardHeader>
          <CardContent className="space-y-3 p-4">
            <p className="text-sm leading-relaxed text-slate-900">{item.recommendation}</p>
            <blockquote className="whitespace-pre-wrap break-words border-l-4 border-indigo-300 pl-3 text-sm italic text-slate-700">“{item.quote}”</blockquote>
            <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
              <Badge variant="secondary">{item.citation.id}</Badge>
              <Badge variant="secondary">{item.citation.label}</Badge>
              <Badge variant="secondary">{item.citation.section || "Section not recorded"}</Badge>
              <Badge variant="secondary">p.{item.citation.page || "not recorded"}</Badge>
              <Badge variant="secondary">{item.citation.strength || "Strength not recorded"}</Badge>
              {item.citation.draft && <Badge className="bg-amber-100 text-amber-800">DRAFT</Badge>}
              {item.repaired && <span>· {item.repaired}</span>}
            </div>
            {showPatientChecks && item.patient_notes && item.patient_notes.length > 0 && <PatientChecks notes={item.patient_notes} />}
          </CardContent>
        </Card>
      ))}
      {problem.items.length === 0 && (
        <p className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
          {problem.status === "error" ? "This problem could not be analyzed. Guideline coverage is unknown; retry this problem. No recommendation was verified."
            : <>{problem.status === "unverified" && "No statement passed verification. "}Not covered by the loaded guidelines with a verifiable statement. This does not mean the topic is unimportant or safe.</>}
        </p>
      )}
      {problem.detail && <p className="text-sm text-slate-600">Pipeline diagnostic: {problem.detail}</p>}
      {problem.unverified.length > 0 && (
        <details className="rounded-lg border border-rose-200 bg-rose-50/40">
          <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-rose-800">
            Unverified (not shown as answers) ({problem.unverified.length})
          </summary>
          <p className="border-t border-rose-100 px-3 pt-3 text-xs font-medium text-rose-900">Rejected drafts are diagnostics only. Do not use them as answers; they are excluded from copy/export and verified conversation memory.</p>
          <ul className="space-y-3 p-3 text-xs text-rose-900">
            {problem.unverified.map((item, itemIndex) => <li key={itemIndex} className="space-y-2 rounded-md border border-rose-200 p-3">
              <p className="whitespace-pre-wrap break-words"><strong>Rejected statement (not an answer):</strong> {item.recommendation}</p>
              {item.quote && <p className="whitespace-pre-wrap break-words"><strong>Draft quote (not verified evidence):</strong> {item.quote}</p>}
              <ul className="list-disc space-y-1 pl-5" aria-label="Verification issues">
                {item.issues.map((issue, issueIndex) => <li key={issueIndex}>{issue}</li>)}
              </ul>
            </li>)}
          </ul>
        </details>
      )}
      <Sources sources={problem.sources} />
    </section>
  );
}

export function AnalysisResult({ result, processSteps = [], progress = [], showPatientChecks = true, protectPatientContext = false }: {
  result: PipelineResult;
  processSteps?: string[];
  progress?: PipelineProgress[];
  showPatientChecks?: boolean;
  protectPatientContext?: boolean;
}) {
  const notCovered = notCoveredTitles(result);
  const failed = result.problems.filter(problem => problem.status === "error");
  return (
    <div className="space-y-4">
      <h2 className="sr-only">{verifiedItemCount(result) > 0 ? "Verified guideline analysis" : "Guideline pipeline result"}</h2>
      <Badge variant="secondary">{analysisModeLabel(analysisMode(result))}</Badge>
      {result.chat ? <p className="text-sm text-slate-800">{result.chat}</p> : result.problems.map((problem, index) => (
        <VerifiedProblem key={index} problem={problem} showPatientChecks={showPatientChecks} />
      ))}
      {failed.length > 0 && (
        <section className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800" aria-label="Analysis incomplete">
          <h3 className="font-semibold">Analysis incomplete</h3>
          <p>Some problems failed. Their guideline coverage is unknown; retry them before applying this result.</p>
          <ul className="mt-1 list-disc pl-5">{failed.map((problem, index) => <li key={index}>{problem.title}</li>)}</ul>
        </section>
      )}
      {notCovered.length > 0 && (
        <section className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800" aria-label="Not covered">
          <h3 className="font-semibold">Not covered</h3>
          <ul className="mt-1 list-disc pl-5">{notCovered.map((title, index) => <li key={index}>{title}</li>)}</ul>
        </section>
      )}
      {result.warnings.length > 0 && (
        <aside aria-label="Warnings" className="space-y-1 rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-700">
          <h3 className="font-semibold">Warnings</h3>
          <ul role="list" className="space-y-1">{result.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul>
        </aside>
      )}
      <p className="text-xs text-slate-500">{result.disclaimer}</p>
      <ProcessingSteps events={progress.length ? progress : processSteps.map(message => ({ message, state: "reported" }))} />
      <AnalysisActions result={result} protectPatientContext={protectPatientContext} />
    </div>
  );
}
