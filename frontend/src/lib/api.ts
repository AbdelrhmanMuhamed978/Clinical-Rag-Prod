import { auth } from "./firebase";
import { signOut } from "firebase/auth";
import { readServerSentEvents, type ServerSentEvent } from "./sse";
import { createRequestController } from "./request-scope";
import { backendAvailable, backendUnavailable, beginBackendRequest, type BackendDependency } from "./backend-status";

const BASE = process.env.NEXT_PUBLIC_API_BASE || "http://localhost:5100";

export class ApiError extends Error {
  constructor(public status: number, message: string, public details?: unknown,
    public retryAfterSeconds?: number, public requestId?: string) { super(message); this.name = "ApiError"; }
}

function object(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function dependency(value: unknown): BackendDependency {
  return value === "colab" || value === "pipeline" || value === "database" ? value : "backend";
}

function unavailableDependency(path: string, body: unknown): BackendDependency {
  if (object(body)) {
    if (path === "/ready" && object(body.checks)) {
      for (const name of ["database", "colab", "pipeline"] as const) {
        const check = body.checks[name];
        if (object(check) && check.ready === false) return name;
      }
    }
    return dependency(body.dependency);
  }
  return "backend";
}

function retryAfter(response: Response, body: unknown): number | undefined {
  const header = response.headers.get("Retry-After");
  const value = header ? (/^\d+$/.test(header) ? Number(header) : (Date.parse(header) - Date.now()) / 1000)
    : object(body) && typeof body.retryAfterSeconds === "number" ? body.retryAfterSeconds : undefined;
  return value !== undefined && Number.isFinite(value) ? Math.max(1, Math.min(300, Math.ceil(value))) : undefined;
}

const unavailableMessages: Record<BackendDependency, string> = {
  backend: "The backend is temporarily unavailable. Please try again shortly.",
  database: "The backend database is temporarily unavailable. Please try again shortly.",
  colab: "The Colab model service is offline or not ready. Ask an administrator to check its connection.",
  pipeline: "The verified pipeline service is temporarily unavailable. No unverified answer was returned.",
};

function readableError(status: number, body: unknown, failedDependency: BackendDependency): string {
  if (status >= 500) return unavailableMessages[failedDependency]; // Never reflect upstream exception bodies.
  if (object(body)) {
    for (const field of ["error", "message", "detail", "title"]) {
      if (typeof body[field] === "string" && body[field].trim()) {
        return body[field].replace(/[\u0000-\u001f\u007f-\u009f]/g, " ").slice(0, 512);
      }
    }
    const errors = Array.isArray(body.errors) ? body.errors : object(body.errors) ? Object.values(body.errors).flat() : [];
    const messages = errors.filter((value): value is string => typeof value === "string" && !!value.trim());
    if (messages.length) return messages.slice(0, 3).join(" ").replace(/[\u0000-\u001f\u007f-\u009f]/g, " ").slice(0, 512);
  }
  return status === 403 ? "You do not have permission to perform this action."
    : status === 404 ? "The requested resource is unavailable."
    : status === 409 ? "The data changed. Refresh and try again."
    : status === 429 ? "Too many requests. Please wait and try again."
    : "The request could not be completed. Check the input and try again.";
}

/** Token refresh and body reads share the request's deadline and release their abort listeners. */
async function abortable<T>(pending: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: (() => void) | undefined;
  try {
    return await new Promise<T>((resolve, reject) => {
      abort = () => reject(signal.reason);
      signal.addEventListener("abort", abort, { once: true });
      pending.then(resolve, reject); // Observe a late rejection even after the caller leaves.
      if (signal.aborted) abort();
    });
  } finally {
    if (abort) signal.removeEventListener("abort", abort);
  }
}

async function transport<T>(action: () => Promise<T>, signal: AbortSignal, ticket: number): Promise<T> {
  signal.throwIfAborted();
  try { return await abortable(action(), signal); }
  catch (error) {
    signal.throwIfAborted();
    if (error instanceof TypeError) {
      backendUnavailable(ticket);
      throw new ApiError(0, "The backend could not be reached. Check your connection and try again.");
    }
    throw error;
  }
}

async function discard(response: Response) {
  void response.body?.cancel().catch(() => {});
}

function assertCurrentUser(user: typeof auth.currentUser, signal: AbortSignal) {
  signal.throwIfAborted();
  if (auth.currentUser !== user) throw new DOMException("The signed-in account changed.", "AbortError");
}

async function authenticatedFetch(path: string, init: RequestInit, signal: AbortSignal, ticket: number,
  user: typeof auth.currentUser): Promise<Response> {
  signal.throwIfAborted();
  const headers = new Headers(init.headers);
  const sameUser = () => assertCurrentUser(user, signal);
  if (user) {
    try { headers.set("Authorization", `Bearer ${await abortable(user.getIdToken(), signal)}`); }
    catch { sameUser(); throw new ApiError(401, "Your session could not be verified. Sign in again."); }
  }
  sameUser();
  // FormData, URLSearchParams and binary bodies get their browser-generated content types/boundaries.
  if (typeof init.body === "string" && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const send = () => transport(() => fetch(`${BASE}${path}`, { ...init, headers, signal }).then(response => {
    // Even a non-cooperative fetch may finish after cancellation or an account change.
    try { sameUser(); } catch (error) { void discard(response); throw error; }
    return response;
  }), signal, ticket);
  let response = await send();
  if (response.status === 401) backendAvailable(ticket);
  if (response.status === 401 && user) {
    await discard(response);
    sameUser();
    try { headers.set("Authorization", `Bearer ${await abortable(user.getIdToken(true), signal)}`); }
    catch {
      sameUser();
      await abortable(signOut(auth).catch(() => {}), signal);
      throw new ApiError(401, "Your session expired. Sign in again.");
    }
    sameUser();
    response = await send(); // Only a rejected authentication attempt is replayed, never a 5xx or a started stream.
  }
  if (response.status === 401) {
    await discard(response);
    sameUser();
    if (user) await abortable(signOut(auth).catch(() => {}), signal);
    throw new ApiError(401, "Your session expired. Sign in again.");
  }
  sameUser();
  return response;
}

/** Bound bytes while reading, not after allocating an arbitrary upstream body. */
async function errorJson(response: Response, signal: AbortSignal, ticket: number): Promise<unknown> {
  if (!response.body) return undefined;
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let bytes = 0;
  let text = "";
  try {
    while (true) {
      const chunk = await transport(() => reader.read(), signal, ticket);
      if (chunk.done) break;
      bytes += chunk.value.byteLength;
      if (bytes > 16384) return undefined;
      text += decoder.decode(chunk.value, { stream: true });
    }
    try { return JSON.parse(text + decoder.decode()); } catch { return undefined; }
  } finally {
    void reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

async function responseError(path: string, response: Response, signal: AbortSignal, ticket: number): Promise<ApiError> {
  let body: unknown;
  if (/^application\/(?:[\w.-]+\+)?json(?:\s*;|$)/i.test(response.headers.get("Content-Type") ?? "")) {
    body = await errorJson(response, signal, ticket);
  } else await discard(response);
  const failedDependency = unavailableDependency(path, body);
  const hint = retryAfter(response, body);
  if (response.status >= 500) backendUnavailable(ticket, failedDependency, hint);
  else backendAvailable(ticket);
  const identifier = response.headers.get("X-Request-Id") ?? "";
  return new ApiError(response.status, readableError(response.status, body, failedDependency),
    response.status < 500 ? body : undefined, hint, /^[a-f0-9]{32}$/i.test(identifier) ? identifier.toLowerCase() : undefined);
}

export async function api<T = unknown>(path: string, init?: RequestInit): Promise<T> {
  const request = createRequestController(init?.signal ?? undefined);
  const ticket = beginBackendRequest();
  const user = auth.currentUser;
  try {
    const res = await authenticatedFetch(path, init ?? {}, request.signal, ticket, user);
    if (!res.ok) {
      const error = await responseError(path, res, request.signal, ticket);
      assertCurrentUser(user, request.signal);
      throw error;
    }
    if (res.status === 204) { backendAvailable(ticket); return undefined as T; }
    let body: unknown;
    try { body = await transport(() => res.json(), request.signal, ticket); }
    catch (error) {
      if (error instanceof SyntaxError) throw new ApiError(502, "The backend returned an unreadable response. Please try again.");
      throw error;
    }
    assertCurrentUser(user, request.signal);
    const recovered = path === "/ready" && object(body) && body.ready === true ? "all"
      : path === "/api/operations/colab" && object(body) && object(body.model) && body.model.ready === true ? "colab" : undefined;
    if (path === "/api/operations/colab" && object(body) && object(body.model) && body.model.ready === false)
      backendUnavailable(ticket, "colab");
    else backendAvailable(ticket, recovered);
    return body as T;
  } catch (error) {
    if (request.signal.aborted && !init?.signal?.aborted) {
      backendUnavailable(ticket);
      throw new ApiError(408, "The backend request timed out. Please try again.");
    }
    throw error;
  } finally {
    request.dispose();
  }
}

export const get = <T = unknown>(p: string, init?: Pick<RequestInit, "signal" | "cache">) => api<T>(p, init);
export const post = <T = unknown>(p: string, body?: unknown) => api<T>(p, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });
export const postForm = <T = unknown>(p: string, formData: FormData) => api<T>(p, { method: "POST", body: formData });
export const patch = <T = unknown>(p: string, body?: unknown, init?: Pick<RequestInit, "signal">) => api<T>(p, { ...init, method: "PATCH", body: body === undefined ? undefined : JSON.stringify(body) });
export const del = <T = unknown>(p: string) => api<T>(p, { method: "DELETE" });

export type { ServerSentEvent } from "./sse";

export async function postStream<TBody>(
  path: string,
  body: TBody,
  onEvent: (event: ServerSentEvent) => void | Promise<void>,
  signal?: AbortSignal,
): Promise<void> {
  const request = createRequestController(signal);
  const ticket = beginBackendRequest();
  const user = auth.currentUser;
  try {
    const response = await authenticatedFetch(path, {
      method: "POST",
      headers: { Accept: "text/event-stream" },
      body: JSON.stringify(body),
      cache: "no-store",
    }, request.signal, ticket, user);
    if (!response.ok) {
      const error = await responseError(path, response, request.signal, ticket);
      assertCurrentUser(user, request.signal);
      throw error;
    }
    if (!response.body) throw new ApiError(502, "The verified analysis could not be started.");

    if (!response.headers.get("Content-Type")?.startsWith("text/event-stream")) {
      await discard(response);
      throw new Error("The verified pipeline did not return an analysis stream.");
    }
    backendAvailable(ticket);
    const events = readServerSentEvents(response.body, request.signal);
    let terminal = false;
    try {
      while (true) {
        const next = await transport(() => events.next(), request.signal, ticket);
        assertCurrentUser(user, request.signal);
        if (next.done) break;
        const event = next.value;
        if (event.event === "error" && object(event.data) && dependency(event.data.dependency) !== "backend") {
          backendUnavailable(ticket, dependency(event.data.dependency),
            typeof event.data.retryAfterSeconds === "number" ? event.data.retryAfterSeconds : 30);
        }
        await onEvent(event);
        if (event.event === "result" || event.event === "error") { terminal = true; break; }
      }
    } finally {
      await events.return(undefined);
    }
    if (!terminal) throw new Error("The verified pipeline stream ended without a result.");
  } catch (error) {
    if (request.signal.aborted && !signal?.aborted) {
      backendUnavailable(ticket);
      throw new ApiError(408, "The verified analysis request timed out. No unverified answer was returned.");
    }
    throw error;
  } finally {
    request.dispose();
  }
}
