import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, del, get, post, postForm, postStream } from "@/lib/api";
import { backendAvailable, backendUnavailable, beginBackendRequest, getBackendOutage } from "@/lib/backend-status";

const mocks = vi.hoisted(() => {
  const user = { getIdToken: vi.fn() };
  return { user, auth: { currentUser: user as typeof user | null }, signOut: vi.fn() };
});
vi.mock("@/lib/firebase", () => ({ auth: mocks.auth }));
vi.mock("firebase/auth", () => ({ signOut: mocks.signOut }));

const fetchMock = vi.fn<typeof fetch>();
function json(body: unknown, status = 200, headers?: HeadersInit) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
beforeEach(() => {
  vi.useFakeTimers();
  mocks.auth.currentUser = mocks.user;
  mocks.user.getIdToken.mockReset().mockResolvedValue("old-token");
  mocks.signOut.mockReset().mockResolvedValue(undefined);
  fetchMock.mockReset().mockImplementation(async () => json({ ok: true }));
  vi.stubGlobal("fetch", fetchMock);
  backendAvailable(beginBackendRequest(), "all");
});
afterEach(() => {
  expect(vi.getTimerCount()).toBe(0);
  vi.useRealTimers(); vi.unstubAllGlobals();
});

describe("request headers and cleanup", () => {
  it.each([get, del, post])("does not force a content type on a bodyless request", async request => {
    await request("/resource");
    const headers = new Headers(fetchMock.mock.calls[0][1]?.headers);
    expect(headers.has("Content-Type")).toBe(false);
    expect(headers.get("Authorization")).toBe("Bearer old-token");
  });
  it("sets JSON type only for a string body and preserves explicitly supplied types", async () => {
    await post("/resource", { synthetic: true });
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get("Content-Type")).toBe("application/json");
    await api("/resource", { method: "POST", body: "plain", headers: { "Content-Type": "text/plain" } });
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).get("Content-Type")).toBe("text/plain");
  });
  it("keeps browser-owned multipart boundaries", async () => {
    const form = new FormData(); form.set("file", "synthetic");
    await postForm("/resource", form);
    expect(fetchMock.mock.calls[0][1]?.body).toBe(form);
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).has("Content-Type")).toBe(false);
  });
  it("handles no-content responses without parsing JSON", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    expect(await del("/resource")).toBeUndefined();
  });
  it("releases external abort listeners on success and failure", async () => {
    const controller = new AbortController();
    const remove = vi.spyOn(controller.signal, "removeEventListener");
    await get("/resource", { signal: controller.signal });
    fetchMock.mockRejectedValueOnce(new TypeError("unsafe network details"));
    await expect(get("/resource", { signal: controller.signal })).rejects.toMatchObject({ status: 0 });
    expect(remove).toHaveBeenCalledTimes(2);
    expect(getBackendOutage()?.dependency).toBe("backend");
  });
  it("does not send pre-cancelled requests or report caller cancellation as an outage", async () => {
    const controller = new AbortController(); controller.abort();
    await expect(get("/resource", { signal: controller.signal })).rejects.toMatchObject({ name: "AbortError" });
    expect(fetchMock).not.toHaveBeenCalled(); expect(getBackendOutage()).toBeNull();
  });
  it("bounds a non-cooperative fetch and cancels a response arriving after the deadline", async () => {
    const pending = deferred<Response>(); const cancel = vi.fn();
    fetchMock.mockReturnValueOnce(pending.promise);
    const result = expect(get("/resource")).rejects.toMatchObject({ status: 408 });
    await vi.advanceTimersByTimeAsync(300000); await result;
    pending.resolve(new Response(new ReadableStream({ cancel })));
    await vi.advanceTimersByTimeAsync(0);
    expect(cancel).toHaveBeenCalledOnce();
  });
  it("keeps the deadline active through a stalled successful response body", async () => {
    const cancel = vi.fn();
    fetchMock.mockResolvedValueOnce(new Response(new ReadableStream({ cancel }), { headers: { "Content-Type": "application/json" } }));
    const result = expect(get("/resource")).rejects.toMatchObject({ status: 408 });
    await vi.advanceTimersByTimeAsync(300000); await result;
  });
});

describe("authentication recovery and account ownership", () => {
  it("refreshes once on 401, replays only the rejected request and consumes the first response", async () => {
    const tokens: (string | null)[] = [];
    const first = json({ error: "rejected" }, 401); const cancel = vi.spyOn(first.body!, "cancel");
    mocks.user.getIdToken.mockResolvedValueOnce("old-token").mockResolvedValueOnce("new-token");
    fetchMock.mockImplementation(async (_, init) => {
      tokens.push(new Headers(init?.headers).get("Authorization"));
      return tokens.length === 1 ? first : json({ ok: true });
    });
    expect(await post("/resource", { synthetic: true })).toEqual({ ok: true });
    expect(tokens).toEqual(["Bearer old-token", "Bearer new-token"]);
    expect(mocks.user.getIdToken.mock.calls).toEqual([[], [true]]);
    expect(cancel).toHaveBeenCalledOnce(); expect(mocks.signOut).not.toHaveBeenCalled();
  });
  it("signs out after the one refreshed request is rejected", async () => {
    fetchMock.mockImplementation(async () => json({ error: "unsafe auth details" }, 401));
    await expect(get("/resource")).rejects.toMatchObject({ status: 401, message: "Your session expired. Sign in again." });
    expect(fetchMock).toHaveBeenCalledTimes(2); expect(mocks.signOut).toHaveBeenCalledWith(mocks.auth);
  });
  it("signs out after forced token refresh fails without a second fetch", async () => {
    mocks.user.getIdToken.mockResolvedValueOnce("old-token").mockRejectedValueOnce(new Error("unsafe SDK error"));
    fetchMock.mockResolvedValueOnce(json({}, 401));
    await expect(get("/resource")).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledOnce(); expect(mocks.signOut).toHaveBeenCalledOnce();
  });
  it("rejects anonymous 401 without a refresh or sign-out", async () => {
    mocks.auth.currentUser = null; fetchMock.mockResolvedValueOnce(json({}, 401));
    await expect(get("/resource")).rejects.toMatchObject({ status: 401 });
    expect(mocks.user.getIdToken).not.toHaveBeenCalled(); expect(mocks.signOut).not.toHaveBeenCalled();
  });
  it("bounds stalled sign-out with the same request deadline", async () => {
    fetchMock.mockImplementation(async () => json({}, 401));
    mocks.signOut.mockReturnValueOnce(new Promise(() => {}));
    const result = expect(get("/resource")).rejects.toMatchObject({ status: 408 });
    await vi.advanceTimersByTimeAsync(300000); await result;
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
  it("bounds stalled forced token refresh without replaying or signing out after cancellation", async () => {
    mocks.user.getIdToken.mockResolvedValueOnce("old-token").mockReturnValueOnce(new Promise(() => {}));
    fetchMock.mockResolvedValueOnce(json({}, 401));
    const result = expect(get("/resource")).rejects.toMatchObject({ status: 408 });
    await vi.advanceTimersByTimeAsync(300000); await result;
    expect(fetchMock).toHaveBeenCalledOnce(); expect(mocks.signOut).not.toHaveBeenCalled();
  });
  it("does not sign out a replacement account when the old refresh fails late", async () => {
    const pending = deferred<string>();
    mocks.user.getIdToken.mockResolvedValueOnce("old-token").mockReturnValueOnce(pending.promise);
    fetchMock.mockResolvedValueOnce(json({}, 401));
    const result = expect(get("/resource")).rejects.toMatchObject({ name: "AbortError" });
    await vi.advanceTimersByTimeAsync(0);
    mocks.auth.currentUser = { getIdToken: vi.fn() }; pending.reject(new Error("old failure"));
    await result; expect(mocks.signOut).not.toHaveBeenCalled(); expect(fetchMock).toHaveBeenCalledOnce();
  });
  it("discards a response received after the signed-in account changes", async () => {
    const pending = deferred<Response>(); const cancel = vi.fn();
    fetchMock.mockReturnValueOnce(pending.promise);
    const result = expect(get("/resource")).rejects.toMatchObject({ name: "AbortError" });
    await vi.advanceTimersByTimeAsync(0); mocks.auth.currentUser = null;
    pending.resolve(new Response(new ReadableStream({ cancel }))); await result;
    expect(cancel).toHaveBeenCalledOnce(); expect(getBackendOutage()).toBeNull();
  });
  it("does not return previous-account data when the account changes during the JSON body read", async () => {
    const pending = deferred<unknown>(); const response = json({});
    vi.spyOn(response, "json").mockReturnValueOnce(pending.promise); fetchMock.mockResolvedValueOnce(response);
    const result = expect(get("/resource")).rejects.toMatchObject({ name: "AbortError" });
    await vi.advanceTimersByTimeAsync(0); mocks.auth.currentUser = null; pending.resolve({ previousAccount: true });
    await result;
  });
});

describe("readable bounded failures and dependency status", () => {
  it.each(["error", "message", "detail", "title"])("parses the %s JSON field into a bounded readable error", async field => {
    fetchMock.mockResolvedValueOnce(json({ [field]: "Invalid\u0000 input" }, 400));
    await expect(get("/resource")).rejects.toMatchObject({ status: 400, message: "Invalid  input" });
  });
  it("sanitizes validation messages", async () => {
    fetchMock.mockResolvedValueOnce(json({ errors: { field: ["Required\u0000 value"] } }, 422));
    await expect(get("/resource")).rejects.toMatchObject({ message: "Required  value" });
  });
  it.each(["text/html", "text/plain", "application/json"])("never exposes raw or malformed %s errors", async type => {
    fetchMock.mockResolvedValueOnce(new Response("unsafe prompt or credential", { status: 400, headers: { "Content-Type": type } }));
    await expect(get("/resource")).rejects.toMatchObject({ message: "The request could not be completed. Check the input and try again." });
  });
  it("stops reading an oversized streamed JSON error and cancels the remaining body", async () => {
    const cancel = vi.fn();
    const body = new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(16385)); }, cancel });
    fetchMock.mockResolvedValueOnce(new Response(body, { status: 400, headers: { "Content-Type": "application/problem+json" } }));
    await expect(get("/resource")).rejects.toMatchObject({ status: 400, details: undefined });
    expect(cancel).toHaveBeenCalledOnce();
  });
  it("sanitizes 503s, keeps correlation/retry hints and never retries a mutation", async () => {
    fetchMock.mockResolvedValueOnce(json({ dependency: "colab", error: "unsafe upstream case and key" }, 503,
      { "Retry-After": "17", "X-Request-Id": "DEADBEEFDEADBEEFDEADBEEFDEADBEEF" }));
    await expect(post("/resource", {})).rejects.toMatchObject({ status: 503, details: undefined, retryAfterSeconds: 17,
      requestId: "deadbeefdeadbeefdeadbeefdeadbeef", message: expect.stringContaining("Colab model service") });
    expect(getBackendOutage()).toEqual({ dependency: "colab", retryAfterSeconds: 17 }); expect(fetchMock).toHaveBeenCalledOnce();
  });
  it("identifies a failing readiness dependency and requires actual recovery evidence", async () => {
    fetchMock.mockResolvedValueOnce(json({ ready: false, checks: { database: { ready: true }, colab: { ready: false } } }, 503));
    await expect(get("/ready")).rejects.toBeInstanceOf(ApiError);
    await get("/unrelated"); expect(getBackendOutage()?.dependency).toBe("colab");
    fetchMock.mockResolvedValueOnce(json({ ready: true })); await get("/ready"); expect(getBackendOutage()).toBeNull();
  });
  it("reports an offline model from the status endpoint even though the status request succeeded", async () => {
    fetchMock.mockResolvedValueOnce(json({ model: { ready: false } })); await get("/api/operations/colab");
    expect(getBackendOutage()?.dependency).toBe("colab");
    fetchMock.mockResolvedValueOnce(json({ model: { ready: true } })); await get("/api/operations/colab");
    expect(getBackendOutage()).toBeNull();
  });
  it("does not allow an older success to clear a newer outage", async () => {
    const old = beginBackendRequest(); backendUnavailable(beginBackendRequest(), "pipeline"); backendAvailable(old, "all");
    expect(getBackendOutage()?.dependency).toBe("pipeline");
  });
});

describe("stream cleanup", () => {
  it("delivers a terminal event once and releases the response stream", async () => {
    const cancel = vi.fn(); const onEvent = vi.fn();
    fetchMock.mockResolvedValueOnce(new Response(new ReadableStream({ start(controller) {
      controller.enqueue(new TextEncoder().encode('event: result\ndata: {"verified":true}\n\n'));
    }, cancel }), { headers: { "Content-Type": "text/event-stream" } }));
    await postStream("/stream", {}, onEvent);
    expect(onEvent).toHaveBeenCalledWith({ event: "result", data: { verified: true } });
    expect(cancel).toHaveBeenCalledOnce(); expect(fetchMock).toHaveBeenCalledOnce();
  });
  it("cancels an unexpected stream content type without displaying its body", async () => {
    const cancel = vi.fn();
    fetchMock.mockResolvedValueOnce(new Response(new ReadableStream({ cancel }), { headers: { "Content-Type": "text/html" } }));
    await expect(postStream("/stream", {}, vi.fn())).rejects.toThrow("did not return an analysis stream");
    expect(cancel).toHaveBeenCalledOnce();
  });
  it("never delivers an event belonging to the previous account", async () => {
    let controller!: ReadableStreamDefaultController<Uint8Array>; const cancel = vi.fn(); const onEvent = vi.fn();
    fetchMock.mockResolvedValueOnce(new Response(new ReadableStream({ start(value) { controller = value; }, cancel }),
      { headers: { "Content-Type": "text/event-stream" } }));
    const result = expect(postStream("/stream", {}, onEvent)).rejects.toMatchObject({ name: "AbortError" });
    await vi.advanceTimersByTimeAsync(0); mocks.auth.currentUser = null;
    controller.enqueue(new TextEncoder().encode('event: result\ndata: {}\n\n')); await result;
    expect(onEvent).not.toHaveBeenCalled(); expect(cancel).toHaveBeenCalledOnce();
  });
});
