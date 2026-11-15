export type ServerSentEvent = { event: string; data: unknown };

export async function* readServerSentEvents(stream: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<ServerSentEvent> {
  const reader = stream.getReader();
  const cancel = () => { void reader.cancel().catch(() => {}); };
  signal?.addEventListener("abort", cancel, { once: true });
  if (signal?.aborted) cancel();
  const decoder = new TextDecoder();
  let buffer = "";
  let event = "message";
  let dataLines: string[] = [];
  let frameLength = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      buffer += decoder.decode(value, { stream: !done });
      while (true) {
        const boundary = buffer.search(/[\r\n]/);
        if (boundary < 0 || (buffer[boundary] === "\r" && boundary === buffer.length - 1 && !done)) break;
        const line = buffer.slice(0, boundary);
        const delimiterLength = buffer[boundary] === "\r" && buffer[boundary + 1] === "\n" ? 2 : 1;
        buffer = buffer.slice(boundary + delimiterLength);
        frameLength += line.length;
        if (frameLength > 2 * 1024 * 1024) throw new Error("The analysis stream event is too large.");
        if (line === "") {
          if (dataLines.length > 0) {
            let data: unknown;
            try { data = JSON.parse(dataLines.join("\n")) as unknown; }
            catch { throw new Error("The analysis stream returned invalid data."); }
            yield { event, data };
          }
          event = "message";
          dataLines = [];
          frameLength = 0;
        } else if (line.startsWith("event:")) {
          event = line.slice(6).replace(/^ /, "");
        } else if (line.startsWith("data:")) {
          dataLines.push(line.slice(5).replace(/^ /, ""));
        }
      }
      if (buffer.length + frameLength > 2 * 1024 * 1024) throw new Error("The analysis stream event is too large.");
      // A frame without a terminating blank line is incomplete, not a result.
      if (done) break;
    }
  } finally {
    cancel(); // A non-cooperative source's cancellation promise must not hold the request deadline open.
    reader.releaseLock();
    signal?.removeEventListener("abort", cancel);
  }
}
