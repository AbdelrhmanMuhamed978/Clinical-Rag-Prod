import assert from "node:assert/strict";
import test from "node:test";
import { readServerSentEvents } from "../src/lib/sse.ts";

test("SSE handles UTF-8 and CRLF boundaries split across network chunks", async () => {
  const bytes = new TextEncoder().encode('event: progress\r\ndata: {"message":"café"}\r\n\r\nevent: result\r\ndata: {"problems":[]}\r\n\r\n');
  const stream = new ReadableStream({
    start(controller) {
      for (const byte of bytes) controller.enqueue(new Uint8Array([byte]));
      controller.close();
    },
  });
  const events = [];
  for await (const event of readServerSentEvents(stream)) events.push(event);
  assert.deepEqual(events, [
    { event: "progress", data: { message: "café" } },
    { event: "result", data: { problems: [] } },
  ]);
});

test("invalid event JSON fails without echoing the body", async () => {
  const stream = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode('event: result\ndata: PRIVATE_CASE_MARKER\n\n'));
      controller.close();
    },
  });
  await assert.rejects(async () => {
    for await (const event of readServerSentEvents(stream)) void event;
  }, (error) => !error.message.includes("PRIVATE_CASE_MARKER"));
});

test("stopping a consumer cancels the unread response", async () => {
  let cancelled = false;
  const stream = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode('event: progress\ndata: {"message":"Checking"}\n\n'));
    },
    cancel() { cancelled = true; },
  });
  for await (const event of readServerSentEvents(stream)) { void event; break; }
  assert.equal(cancelled, true);
});
