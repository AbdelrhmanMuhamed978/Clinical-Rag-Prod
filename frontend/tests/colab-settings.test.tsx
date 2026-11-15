import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ColabSettings } from "@/components/operations/colab-settings";

const mocks = vi.hoisted(() => ({ get: vi.fn(), api: vi.fn(), admin: false }));
vi.mock("@/lib/api", () => ({ ...mocks, ApiError: class extends Error {} }));
vi.mock("@/components/navigation-guard", () => ({ useUnsavedChanges: vi.fn() }));
const clients: QueryClient[] = [];
const setting = { configured: true, baseUrl: "https://synthetic.example.com/", hasApiKey: true, revision: 7,
  manualOverride: false, lastHeartbeatAt: "2026-11-15T10:00:00Z", heartbeatStale: false, updatedAt: "2026-11-15T10:00:00Z" };
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } }); clients.push(client);
  const view = render(<QueryClientProvider client={client}><ColabSettings userId="user-1" /></QueryClientProvider>);
  return { ...view, client, user: userEvent.setup() };
}
beforeEach(() => {
  mocks.admin = false; mocks.api.mockReset().mockResolvedValue({ ...setting, revision: 8, manualOverride: true });
  mocks.get.mockReset().mockImplementation(async (path: string) => {
    if (path === "/api/admin/access") return { isAdmin: mocks.admin };
    if (path === "/api/admin/colab-settings") return setting;
    return { connection: { ...setting, baseUrl: null }, model: { ready: true, model: "synthetic-model", embeddingModel: "synthetic-encoder",
      embeddingDimensions: 2, gpu: { available: true, name: "synthetic-GPU" } } };
  });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));
describe("Colab operational Settings", () => {
  it("shows live model/GPU/heartbeat without requesting privileged settings for a workspace owner", async () => {
    mount(); await screen.findByText("synthetic-model");
    expect(screen.getByText("synthetic-GPU")).toBeTruthy(); expect(screen.getByText(/\(recent\)/)).toBeTruthy();
    expect(screen.queryByLabelText("Colab base URL (required)")).toBeNull();
    expect(mocks.get.mock.calls.some(([path]) => path === "/api/admin/colab-settings")).toBe(false);
  });
  it("allows a provisioned admin to save a revision-bound manual override without caching the replacement key", async () => {
    mocks.admin = true; const view = mount();
    const key = await screen.findByLabelText("Replacement API key (optional)");
    await view.user.type(key, "synthetic-replacement-key");
    await view.user.click(screen.getByLabelText("Manual override"));
    await view.user.click(screen.getByRole("button", { name: "Save connection" }));
    await screen.findByText("Connection saved. Notebook registration is blocked by manual override.");
    const init = mocks.api.mock.calls[0][1];
    expect(init.method).toBe("PUT"); expect(JSON.parse(init.body)).toMatchObject({ expectedRevision: 7, manualOverride: true, apiKey: "synthetic-replacement-key" });
    expect((key as HTMLInputElement).value).toBe("");
    expect(JSON.stringify(view.client.getQueryCache().getAll().map(query => query.state.data))).not.toContain("synthetic-replacement-key");
  });
  it("fails closed when platform-administrator access cannot be verified", async () => {
    mocks.get.mockRejectedValue(new Error("unsafe secret or URL")); mount();
    await screen.findByText("Platform administration could not be verified. Connection editing stays disabled.");
    expect(screen.queryByRole("button", { name: "Save connection" })).toBeNull();
    expect(document.body.textContent).not.toContain("unsafe secret or URL");
  });
  it("cancels an in-flight setting write on unmount and ignores its late confirmation", async () => {
    let resolve!: (value: typeof setting) => void;
    mocks.admin = true; mocks.api.mockReturnValueOnce(new Promise<typeof setting>(done => { resolve = done; })); const view = mount();
    await screen.findByLabelText("Replacement API key (optional)"); await view.user.click(screen.getByLabelText("Manual override"));
    const cacheWrite = vi.spyOn(view.client, "setQueryData");
    await view.user.click(screen.getByRole("button", { name: "Save connection" }));
    await waitFor(() => expect(mocks.api).toHaveBeenCalledOnce());
    const signal = mocks.api.mock.calls[0][1].signal as AbortSignal; view.unmount(); expect(signal.aborted).toBe(true);
    await act(async () => resolve({ ...setting, revision: 8 }));
    expect(cacheWrite).not.toHaveBeenCalled();
  });
});
