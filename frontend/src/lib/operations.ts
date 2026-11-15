export type OperationalConnectionStatus = {
  configured: boolean; baseUrl: string | null; hasApiKey: boolean; revision: number; manualOverride: boolean;
  updatedAt: string | null; lastHeartbeatAt: string | null; heartbeatStale: boolean;
};
export type ColabReadiness = {
  ready: boolean; model: string | null; embeddingModel: string | null; embeddingDimensions: number | null;
  gpu: { available: boolean; name: string | null; device: string; totalMemoryBytes: number | null } | null;
  message: string | null;
};
export type ColabOperationalStatus = { connection: OperationalConnectionStatus; model: ColabReadiness };
export type PlatformAdminAccess = { isAdmin: boolean };

export function operationalSettingError(cause: unknown): string {
  // Only stable instructions reach the form, never a server body, submitted key or tunnel URL.
  if (typeof cause === "object" && cause !== null && "status" in cause) {
    if (cause.status === 401) return "Sign in again before changing the Colab connection.";
    if (cause.status === 403) return "Platform administrator access is required. Connection editing is disabled.";
    if (cause.status === 409) return "The Colab connection changed. Discard your changes to load the latest revision before saving again.";
    if (cause.status === 400) return "Check the public HTTPS base URL and use a 16–4096 character ASCII API key without spaces.";
    if (cause.status === 503 || cause.status === 0 || cause.status === 408) return "The backend is unavailable. Your changes were not confirmed. Refresh status before trying again.";
  }
  return "The Colab connection could not be saved. Refresh status before trying again.";
}
