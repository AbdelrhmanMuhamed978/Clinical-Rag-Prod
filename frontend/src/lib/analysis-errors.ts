import { ApiError } from "./api";
import { isRecord } from "./analysis";

const failure = "Verified analysis failed. No unverified answer was displayed.";
const dependencyMessages = {
  colab: "The Colab model service is offline or not ready. Ask an administrator to check its connection.",
  pipeline: "The verified pipeline service is temporarily unavailable. No unverified answer was returned.",
} as const;

/** Never reflect model/proxy error text, even when it arrives inside valid SSE. */
export function pipelineStreamError(value: unknown): Error {
  if (isRecord(value) && (value.dependency === "colab" || value.dependency === "pipeline"))
    return new ApiError(503, dependencyMessages[value.dependency]);
  return new Error(failure);
}

export function analysisActionError(cause: unknown): string {
  if (!(cause instanceof ApiError)) return failure;
  switch (cause.status) {
    case 0: return "The backend could not be reached. Check your connection and retry. No answer was added.";
    case 401: return "Sign in again before running analysis. No answer was added.";
    case 403: return "Editor access to this workspace is required to run analysis.";
    case 404: return "The workspace or analysis endpoint is unavailable. Refresh before retrying. No answer was added.";
    case 408: return "Analysis timed out. Retry when the services are ready. No answer was added.";
    case 429: return "Too many requests. Wait before retrying analysis. No answer was added.";
    case 503: return Object.values(dependencyMessages).some(message => message === cause.message)
      ? `${cause.message} No answer was added.`
      : "An analysis dependency is temporarily unavailable. Ask an administrator to check service readiness before retrying. No answer was added.";
    default: return failure;
  }
}
