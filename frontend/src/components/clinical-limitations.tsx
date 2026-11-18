"use client";

import { useEffect, useState } from "react";
import { INTENDED_USE } from "@/lib/analysis";

export const CLINICAL_LIMITATION = INTENDED_USE;

const FIRST_USE_KEY = "clinical-rag-intended-use-acknowledged";

export function ClinicalLimitation({ className = "" }: { className?: string }) {
  return <p role="note" className={`text-xs text-slate-600 ${className}`}>{CLINICAL_LIMITATION}</p>;
}

export function FirstUseNotice() {
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => {
      try { setVisible(window.localStorage.getItem(FIRST_USE_KEY) !== "true"); }
      catch { setVisible(true); }
    }, 0);
    return () => window.clearTimeout(timer);
  }, []);
  if (!visible) return null;
  return (
    <section role="status" aria-label="Intended use and limitations" className="mb-4 rounded-lg border border-indigo-200 bg-indigo-50 p-3 text-sm text-indigo-950">
      <h2 className="font-semibold">Intended use and limitations</h2>
      <p className="mt-1">{CLINICAL_LIMITATION}</p>
      <button type="button" className="mt-2 min-h-11 rounded border border-indigo-300 bg-white px-3 py-2 text-sm font-medium"
        onClick={() => {
          try { window.localStorage.setItem(FIRST_USE_KEY, "true"); } catch { /* A blocked storage API must not hide the notice. */ }
          setVisible(false);
        }}>
        Acknowledge
      </button>
    </section>
  );
}
