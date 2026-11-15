import { ApiError } from "./api";
import type { QueryClient } from "@tanstack/react-query";

export async function clearWorkspaceQueries(client: QueryClient, workspaceId: string): Promise<void> {
  const filter = { predicate: (query: { queryKey: readonly unknown[] }) => query.queryKey.includes(workspaceId) };
  // Cancel reads first so a late response cannot repopulate removed workspace data.
  await client.cancelQueries(filter);
  client.removeQueries(filter);
  void client.invalidateQueries({ queryKey: ["workspaces"] });
}

const safeErrors = new Set([
  "The last workspace owner cannot be removed or demoted. Assign another owner first.",
  "Workspace or member access changed. Refresh before trying again.",
  "This email already belongs to a workspace member.",
  "A pending invitation already exists. Cancel it before changing its access.",
  "A document is being processed. Wait for processing to finish before deleting this workspace.",
  "Enter a workspace name of 1–128 characters without control characters.",
  "Enter a valid email and choose viewer, editor, reviewer or owner access.",
]);

export function workspaceActionError(cause: unknown): string {
  if (cause instanceof ApiError) {
    if (cause.status === 401) return "Sign in again before changing workspace settings.";
    if (cause.status === 403) return "Owner access is required for workspace administration.";
    if (cause.status === 404) return "Workspace or member access is no longer available. Refresh your workspaces.";
    const details = cause.details;
    if (typeof details === "object" && details !== null && "error" in details && typeof details.error === "string" && safeErrors.has(details.error)) return details.error;
    if (cause.status === 409 || cause.status === 428) return "Workspace or member access changed. Refresh before trying again.";
  }
  return "Workspace action was not confirmed. Refresh and check access before trying again.";
}
