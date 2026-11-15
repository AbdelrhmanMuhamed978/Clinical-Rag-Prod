/** Process-local availability only. Never store request paths, inputs, credentials or response bodies here. */
export type BackendDependency = "backend" | "database" | "pipeline" | "colab";
export type BackendOutage = Readonly<{ dependency: BackendDependency; retryAfterSeconds: number }>;

let outage: BackendOutage | null = null;
let sequence = 0;
let lastObserved = 0;
const listeners = new Set<() => void>();

export const beginBackendRequest = () => ++sequence;
export const getBackendOutage = () => outage;
export const getServerBackendOutage = () => null;
export function subscribeBackendOutage(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

function update(value: BackendOutage | null) {
  if (outage?.dependency === value?.dependency && outage?.retryAfterSeconds === value?.retryAfterSeconds) return;
  outage = value;
  listeners.forEach(listener => listener());
}

export function backendUnavailable(ticket: number, dependency: BackendDependency = "backend", retryAfterSeconds = 30) {
  if (ticket < lastObserved) return;
  lastObserved = ticket;
  update({ dependency, retryAfterSeconds: Number.isFinite(retryAfterSeconds) ? Math.max(1, Math.min(300, Math.ceil(retryAfterSeconds))) : 30 });
}

export function backendAvailable(ticket: number, recovered?: BackendDependency | "all") {
  if (ticket < lastObserved) return;
  lastObserved = ticket;
  // An unrelated successful request proves the API is reachable, not that a model dependency recovered.
  if (outage?.dependency === "backend" || recovered === "all" || recovered === outage?.dependency) update(null);
}
