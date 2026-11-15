import assert from "node:assert/strict";
import test from "node:test";
import { createRequestController, createRequestScope } from "../src/lib/request-scope.ts";
import { shouldSubmitOnEnter } from "../src/lib/analysis.ts";

test("clear, cancellation and replacement invalidate late request completions", () => {
  const scope = createRequestScope();
  const first = scope.start();
  assert.equal(first.isCurrent(), true);
  scope.cancel();
  assert.equal(first.signal.aborted, true);
  assert.equal(first.isCurrent(), false);

  const second = scope.start();
  const third = scope.start();
  assert.equal(second.signal.aborted, true);
  second.finish();
  assert.equal(third.isCurrent(), true);
  third.finish();
  assert.equal(scope.active, false);
});

test("separate workspace scopes never share request state", () => {
  const workspaceA = createRequestScope();
  const workspaceB = createRequestScope();
  const requestA = workspaceA.start();
  const requestB = workspaceB.start();
  workspaceA.cancel();
  assert.equal(requestA.isCurrent(), false);
  assert.equal(requestB.isCurrent(), true);
  workspaceB.cancel();
});

test("request deadlines honor external aborts, including pre-aborted signals", () => {
  const external = new AbortController();
  const request = createRequestController(external.signal);
  external.abort();
  assert.equal(request.signal.aborted, true);
  request.dispose();

  const preAborted = createRequestController(external.signal);
  assert.equal(preAborted.signal.aborted, true);
  preAborted.dispose();
});

test("Enter submission ignores IME confirmation and preserves Shift+Enter", () => {
  const event = { key: "Enter", shiftKey: false, isComposing: false, keyCode: 13 };
  assert.equal(shouldSubmitOnEnter(event), true);
  assert.equal(shouldSubmitOnEnter({ ...event, shiftKey: true }), false);
  assert.equal(shouldSubmitOnEnter({ ...event, isComposing: true }), false);
  assert.equal(shouldSubmitOnEnter({ ...event, keyCode: 229 }), false);
});
