import type { PipelineProgress } from "@/lib/analysis";

export type ProcessingOutcome = "running" | "completed" | "failed" | "cancelled";

export function ProcessingSteps({ events, outcome = "completed" }: { events: PipelineProgress[]; outcome?: ProcessingOutcome }) {
  if (events.length === 0) return null;
  return <details className="rounded-lg border border-slate-200 px-3 py-2 text-xs text-slate-600">
    <summary className="cursor-pointer font-medium">Processing steps ({events.length} reported events)</summary>
    <p className="mt-2">Pipeline events, not a checklist. A started or legacy reported step is not proof of completion.{outcome === "failed" ? " This attempt failed." : outcome === "cancelled" ? " This attempt was cancelled." : ""}</p>
    <ol className="mt-2 list-decimal space-y-2 pl-5">
      {events.map((event, index) => <li key={index}>
        <strong className={event.state === "failed" ? "text-rose-700" : "text-slate-700"}>
          {event.state === "reported" ? "Reported — completion unknown" : event.state === "started" ? "Started" : event.state === "completed" ? "Completed" : "Failed"}
        </strong>{": "}{event.message}
      </li>)}
    </ol>
  </details>;
}
