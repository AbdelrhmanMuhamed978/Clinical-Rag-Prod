/** In-memory request ownership. Clearing or leaving a workspace invalidates
 * late callbacks as well as aborting the underlying transport. */
export function createRequestScope() {
  let current: AbortController | null = null;
  return {
    get active() { return current !== null; },
    start() {
      current?.abort();
      const controller = new AbortController();
      current = controller;
      return {
        signal: controller.signal,
        isCurrent: () => current === controller && !controller.signal.aborted,
        finish() { if (current === controller) current = null; },
      };
    },
    cancel() {
      const controller = current;
      current = null;
      controller?.abort();
    },
  };
}

/** Keep the deadline active until the response body has been consumed. */
export function createRequestController(signal?: AbortSignal, timeoutMilliseconds = 300000) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (signal?.aborted) abort();
  signal?.addEventListener("abort", abort, { once: true });
  const timeout = setTimeout(abort, timeoutMilliseconds);
  return {
    signal: controller.signal,
    dispose() {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", abort);
      controller.abort();
    },
  };
}
