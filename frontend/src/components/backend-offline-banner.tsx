"use client";

import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { get } from "@/lib/api";
import { getBackendOutage, getServerBackendOutage, subscribeBackendOutage, type BackendDependency } from "@/lib/backend-status";
import { Button } from "@/components/ui/button";

const messages: Record<BackendDependency, string> = {
  backend: "Backend offline or not responding. Check your connection or contact the administrator.",
  database: "Backend database unavailable. Please try again shortly.",
  colab: "Colab model service offline or not ready. Ask an administrator to check Settings.",
  pipeline: "Verified pipeline unavailable. No unverified answer was returned.",
};

export function BackendOfflineBanner() {
  const outage = useSyncExternalStore(subscribeBackendOutage, getBackendOutage, getServerBackendOutage);
  const probe = useRef<AbortController | null>(null);
  const [checking, setChecking] = useState(false);
  useEffect(() => () => { probe.current?.abort(); probe.current = null; }, []);

  async function check() {
    if (probe.current) return;
    const controller = new AbortController(); probe.current = controller;
    setChecking(true);
    try { await get("/ready", { signal: controller.signal, cache: "no-store" }); }
    catch { /* The shared transport updates availability using fixed dependency labels. */ }
    finally {
      if (probe.current === controller) { probe.current = null; setChecking(false); }
    }
  }

  return <aside aria-label="Service availability" className={outage ? "relative z-50 border-b border-amber-500/50 bg-amber-950 p-4 text-amber-50" : undefined}>
    <div role="status" aria-live="polite" aria-atomic="true">{outage && <p>{messages[outage.dependency]} Requests are not automatically resubmitted.</p>}</div>
    {outage && <div className="mt-2 flex flex-wrap items-center gap-3">
      <span className="text-sm">Suggested retry: {outage.retryAfterSeconds} seconds.</span>
      <Button type="button" variant="outline" className="min-h-12" disabled={checking} onClick={() => void check()}>Check connection</Button>
    </div>}
  </aside>;
}
