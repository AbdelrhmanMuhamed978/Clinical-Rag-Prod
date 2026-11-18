// Staging load generator for the deployed clinical RAG stack.
//
// Run one mode and concurrency level per k6 process:
//   MODE=retrieve VUS=3 DURATION=2m ...
//   MODE=analyze  VUS=3 DURATION=2m ...
//
// The Python orchestrator runs modes separately so "3 concurrent users" means
// three users in that measured path, while a chunk-job worker runs alongside.
// No token or case text is ever printed by this script.

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";

const MODE = (__ENV.MODE || "retrieve").toLowerCase();
const VUS = Number(__ENV.VUS || 1);
const DURATION = __ENV.DURATION || "2m";
const BASE_URL = (__ENV.BASE_URL || "").replace(/\/+$/, "");
const WORKSPACE_ID = __ENV.WORKSPACE_ID || "";
const TOKEN = __ENV.EVAL_FIREBASE_TOKEN || "";
const QUERY = __ENV.EVAL_QUERY || "synthetic readiness query";
const QUESTION = __ENV.EVAL_QUESTION || QUERY;
const REQUEST_TIMEOUT = __ENV.REQUEST_TIMEOUT || "300s";
const SLEEP_SECONDS = Number(__ENV.SLEEP_SECONDS || 0);

if (!BASE_URL || !WORKSPACE_ID || !TOKEN) {
  throw new Error("BASE_URL, WORKSPACE_ID and EVAL_FIREBASE_TOKEN are required.");
}
if (!["retrieve", "analyze"].includes(MODE)) {
  throw new Error("MODE must be retrieve or analyze.");
}

const retrieveDuration = new Trend("retrieve_duration", true);
const analyzeDuration = new Trend("analyze_duration", true);
const status429 = new Counter("status_429");
const status503 = new Counter("status_503");
const statusOther = new Counter("status_other_error");
const retrieveChecks = new Counter("retrieve_shape_ok");
const analyzeChecks = new Counter("analyze_shape_ok");

export const options = {
  scenarios: {
    load: {
      executor: "constant-vus",
      vus: VUS,
      duration: DURATION,
      gracefulStop: "30s",
    },
  },
  summaryTrendStats: ["avg", "min", "med", "max", "p(90)", "p(95)", "p(99)"],
  tags: { mode: MODE, vus: String(VUS) },
};

const headers = {
  Authorization: `Bearer ${TOKEN}`,
  "Content-Type": "application/json",
  "Cache-Control": "no-store",
};

function recordStatus(response) {
  if (response.status === 429) status429.add(1);
  if (response.status === 503) status503.add(1);
  if (response.status >= 400 && response.status !== 429 && response.status !== 503) statusOther.add(1);
}

export default function () {
  if (MODE === "retrieve") {
    const url = `${BASE_URL}/api/workspaces/${WORKSPACE_ID}/analyze/retrieve`;
    const response = http.post(url, JSON.stringify({ query: QUERY }), {
      headers,
      timeout: REQUEST_TIMEOUT,
      tags: { endpoint: "retrieve", mode: MODE },
    });
    retrieveDuration.add(response.timings.duration);
    recordStatus(response);
    let body = null;
    try { body = response.json(); } catch (_) { body = null; }
    const ok = check(response, {
      "retrieve HTTP 200": (item) => item.status === 200,
      "retrieve chunks array": () => body !== null && Array.isArray(body.chunks),
    });
    if (ok) retrieveChecks.add(1);
  } else {
    const url = `${BASE_URL}/api/workspaces/${WORKSPACE_ID}/analyze`;
    const response = http.post(url, JSON.stringify({ message: QUESTION }), {
      headers,
      timeout: REQUEST_TIMEOUT,
      tags: { endpoint: "analyze", mode: MODE },
    });
    analyzeDuration.add(response.timings.duration);
    recordStatus(response);
    let body = null;
    try { body = response.json(); } catch (_) { body = null; }
    const ok = check(response, {
      "analyze HTTP 200": (item) => item.status === 200,
      "analyze problems array": () => body !== null && Array.isArray(body.problems),
    });
    if (ok) analyzeChecks.add(1);
  }
  if (SLEEP_SECONDS > 0) sleep(SLEEP_SECONDS);
}

export function handleSummary(data) {
  const path = __ENV.SUMMARY_PATH || "k6-summary.json";
  return {
    [path]: JSON.stringify(data, null, 2),
    stdout: `k6 mode=${MODE} vus=${VUS} duration=${DURATION}\n`,
  };
}
