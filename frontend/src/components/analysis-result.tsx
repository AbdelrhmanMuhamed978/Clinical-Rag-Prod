"use client";

import { useId } from "react";
import { AlertCircle, CheckCircle2 } from "lucide-react";
import { analysisMode, analysisModeLabel, COVERAGE_LABELS, notCoveredEntries, notCoveredReasonLabel, verifiedItemCount, type CoverageEntry, type PatientNote, type PipelineProblem, type PipelineProgress, type PipelineResult, type PipelineSource, type VerifiedItem } from "@/lib/analysis";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { AnalysisActions } from "@/components/analysis-actions";
import { ProcessingSteps } from "@/components/processing-steps";
import { ClinicalLimitation } from "@/components/clinical-limitations";
import { FlagAnswer } from "@/components/flag-answer";

function governanceNotice(citation: PipelineSource | VerifiedItem["citation"]): string | null {
  if (citation.superseded) return "SUPERSEDED guideline: do not use this statement as current guidance.";
  if (citation.review_by && !Number.isNaN(Date.parse(citation.review_by)) && Date.parse(citation.review_by) < Date.now())
    return `Review date passed (${citation.review_by}). This guideline may be out of date.`;
  return null;
}

function GovernanceBanner({ citation }: { citation: PipelineSource | VerifiedItem["citation"] }) {
  const notice = governanceNotice(citation);
  if (!notice) return null;
  return <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 p-2 text-xs text-rose-800">{notice}</p>;
}

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
              {source.superseded && <Badge className="bg-rose-100 text-rose-800">SUPERSEDED</Badge>}
            </div>
            <GovernanceBanner citation={source} />
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
    <aside aria-labelledby={headingId} className="rounded-lg border-2 border-amber-300 bg-amber-50 p-3 text-amber-950">
      <h4 id={headingId} className="flex items-center gap-2 text-sm font-semibold">
        <AlertCircle className="h-4 w-4" aria-hidden="true" /> Live patient checks — not evidence
      </h4>
      <p className="mt-1 text-xs">These are deterministic checks against this patient, not guideline evidence. They are kept separate from the verified statements and are not saved or added to verified memory.</p>
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

function ExactQuote({ item }: { item: VerifiedItem }) {
  if (item.table) {
    return (
      <div className="overflow-x-auto border-t border-indigo-100">
        <table className="w-full text-left text-sm text-slate-700">
          <caption className="sr-only">Exact table evidence</caption>
          <thead>
            <tr>{item.table.headers.map((header, index) => <th key={index} scope="col" className="border-b border-indigo-100 px-3 py-2 font-semibold">{header}</th>)}</tr>
          </thead>
          <tbody>
            <tr>{item.table.row.map((cell, index) => <td key={index} className="px-3 py-2 align-top">{cell}</td>)}</tr>
          </tbody>
        </table>
      </div>
    );
  }
  return <blockquote className="whitespace-pre-wrap break-words border-t border-indigo-100 px-3 py-2 text-sm italic text-slate-700">“{item.quote}”</blockquote>;
}

function VerifiedItemCard({ item, related = false }: { item: VerifiedItem; related?: boolean }) {
  return (
    <Card className={related ? "border-amber-200 bg-amber-50/20" : "border-emerald-200 bg-emerald-50/30"}>
      <CardHeader className={`flex-row items-center gap-2 border-b px-4 py-3 ${related ? "border-amber-100" : "border-emerald-100"}`}>
        <CheckCircle2 className={`h-4 w-4 ${related ? "text-amber-600" : "text-emerald-600"}`} aria-hidden="true" />
        <span className={`text-sm font-semibold ${related ? "text-amber-800" : "text-emerald-800"}`}>
          {related ? "Related verified statement" : "Verified recommendation"}
        </span>
      </CardHeader>
      <CardContent className="space-y-3 p-4">
        <p className="text-sm leading-relaxed text-slate-900">{item.recommendation}</p>
        <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
          <Badge variant="secondary">{item.citation.id}</Badge>
          <Badge variant="secondary">{item.citation.label}</Badge>
          <Badge variant="secondary">{item.citation.section || "Section not stated"}</Badge>
          <Badge variant="secondary">p.{item.citation.page || "not recorded"}</Badge>
          <Badge variant="secondary">{item.citation.strength || "Strength not stated"}</Badge>
          <Badge variant="secondary">{item.citation.certainty ? `${item.citation.certainty} certainty` : "Certainty not stated"}</Badge>
          {item.citation.draft && <Badge className="bg-amber-100 text-amber-800">DRAFT</Badge>}
          {item.citation.superseded && <Badge className="bg-rose-100 text-rose-800">SUPERSEDED</Badge>}
          {item.repaired && <span>· {item.repaired}</span>}
        </div>
        <GovernanceBanner citation={item.citation} />
        <details className="rounded-md border border-indigo-100 bg-indigo-50/50">
          <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-indigo-900">Exact quote</summary>
          <ExactQuote item={item} />
        </details>
      </CardContent>
    </Card>
  );
}

function CoverageReport({ coverage }: { coverage?: CoverageEntry[] }) {
  if (!coverage?.length) return null;
  return (
    <section aria-label="Question coverage" className="rounded-lg border border-slate-200 bg-white p-4">
      <h3 className="text-sm font-semibold text-slate-900">Question coverage</h3>
      <ul className="mt-2 space-y-1 text-sm text-slate-700">
        {coverage.map((row, index) => (
          <li key={index} className="flex flex-wrap items-baseline gap-2">
            <span>{row.part}</span>
            <Badge variant="secondary" className={row.status === "answered" ? "bg-emerald-50 text-emerald-800" : row.status === "related_only" ? "bg-amber-50 text-amber-800" : "bg-slate-100 text-slate-700"}>
              {COVERAGE_LABELS[row.status]}
            </Badge>
          </li>
        ))}
      </ul>
    </section>
  );
}

function VerifiedProblem({ problem, showPatientChecks }: { problem: PipelineProblem; showPatientChecks: boolean }) {
  const headingId = useId();
  const related = problem.related ?? [];
  const patientChecks = problem.items.flatMap(item => item.patient_notes ?? []);
  return (
    <section className="space-y-3" aria-labelledby={headingId}>
      <h3 id={headingId} className="text-base font-semibold text-slate-900">{problem.title}</h3>
      {problem.items.map((item, index) => <VerifiedItemCard key={`${item.citation.id}-${index}`} item={item} />)}
      {problem.items.length === 0 && related.length > 0 && (
        <p className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
          No statement directly answered your question. Related evidence is shown separately and is not the lead answer.
        </p>
      )}
      {related.length > 0 && (
        <details className="rounded-lg border border-amber-200 bg-amber-50/40" open={problem.items.length === 0}>
          <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-amber-900">
            Related, may not answer your question ({related.length})
          </summary>
          <p className="border-t border-amber-100 px-3 pt-3 text-xs text-amber-900">
            These statements passed source verification but did not match your question closely enough to be the lead answer.
          </p>
          <div className="space-y-2 p-3">{related.map((item, index) => <VerifiedItemCard key={`${item.citation.id}-related-${index}`} item={item} related />)}</div>
        </details>
      )}
      {problem.items.length === 0 && related.length === 0 && (
        <p className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
          {problem.status === "error" ? "This problem could not be analyzed. Guideline coverage is unknown; retry this problem. No recommendation was verified."
            : <>{problem.status === "unverified" && "No statement passed verification. "}Not covered by the loaded guidelines with a verifiable statement.
              <span className="mt-1 block">{notCoveredReasonLabel(problem.reason)}</span>
              This does not mean the topic is unimportant or safe.</>}
        </p>
      )}
      {showPatientChecks && patientChecks.length > 0 && <PatientChecks notes={patientChecks} />}
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

export function AnalysisResult({ result, processSteps = [], progress = [], showPatientChecks = true, protectPatientContext = false, sourceText = "", configVersion }: {
  result: PipelineResult;
  processSteps?: string[];
  progress?: PipelineProgress[];
  showPatientChecks?: boolean;
  protectPatientContext?: boolean;
  sourceText?: string;
  configVersion?: number | null;
}) {
  const notCovered = notCoveredEntries(result);
  const failed = result.problems.filter(problem => problem.status === "error");
  return (
    <div className="space-y-4">
      <h2 className="sr-only">{verifiedItemCount(result) > 0 ? "Verified guideline analysis" : "Guideline pipeline result"}</h2>
      <Badge variant="secondary">{analysisModeLabel(analysisMode(result))}</Badge>
      {result.conflicts && result.conflicts.length > 0 && (
        <section className="space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3" aria-label="Guideline disagreement">
          <h3 className="font-semibold text-amber-900">Guideline disagreement — both statements shown</h3>
          <p className="text-xs text-amber-800">These verified statements disagree. They are not merged or silently ranked; review both sources with a clinician.</p>
          {result.conflicts.map((conflict, conflictIndex) => (
            <div key={conflictIndex} className="space-y-2 rounded-md border border-amber-200 bg-white p-3">
              <p className="text-sm font-medium text-amber-900">{conflict.point}</p>
              {conflict.items.map((item, itemIndex) => (
                <div key={itemIndex} className="text-sm text-slate-800">
                  <p>{item.recommendation}</p>
                  <p className="text-xs text-slate-500">{item.citation.label} · p.{item.citation.page} · {item.citation.section}</p>
                  <GovernanceBanner citation={item.citation} />
                </div>
              ))}
            </div>
          ))}
        </section>
      )}
      {result.chat ? <p className="text-sm text-slate-800">{result.chat}</p> : (
        <>
          <CoverageReport coverage={result.coverage} />
          {result.problems.map((problem, index) => (
            <VerifiedProblem key={index} problem={problem} showPatientChecks={showPatientChecks} />
          ))}
        </>
      )}
      {failed.length > 0 && (
        <section className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800" aria-label="Analysis incomplete">
          <h3 className="font-semibold">Analysis incomplete</h3>
          <p>Some problems failed. Their guideline coverage is unknown; retry them before applying this result.</p>
          <ul className="mt-1 list-disc pl-5">{failed.map((problem, index) => <li key={index}>{problem.title}</li>)}</ul>
        </section>
      )}
      {notCovered.length > 0 && !result.coverage?.length && (
        <section className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800" aria-label="Not covered">
          <h3 className="font-semibold">Not covered</h3>
          <p className="mt-1 text-xs">These are safe reason categories. “Not covered” does not mean the topic or patient is safe.</p>
          <ul className="mt-2 list-disc space-y-2 pl-5">{notCovered.map((entry, index) => (
            <li key={index}><strong>{entry.title}</strong><span className="block text-xs">{notCoveredReasonLabel(entry.reason)}</span></li>
          ))}</ul>
        </section>
      )}
      {result.warnings.length > 0 && (
        <aside aria-label="Warnings" className="space-y-1 rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-700">
          <h3 className="font-semibold">Warnings</h3>
          <ul role="list" className="space-y-1">{result.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul>
        </aside>
      )}
      <p className="text-xs text-slate-500">{result.disclaimer}</p>
      <ClinicalLimitation />
      {result.runId && <FlagAnswer runId={result.runId} configVersion={configVersion ?? result.configVersion} />}
      <ProcessingSteps events={progress.length ? progress : processSteps.map(message => ({ message, state: "reported" }))} />
      <AnalysisActions result={result} protectPatientContext={protectPatientContext} sourceText={sourceText} configVersion={configVersion} />
    </div>
  );
}
