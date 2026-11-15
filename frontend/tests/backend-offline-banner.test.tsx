import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BackendOfflineBanner } from "@/components/backend-offline-banner";
import { backendAvailable, backendUnavailable, beginBackendRequest } from "@/lib/backend-status";

const mocks = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@/lib/api", () => mocks);
beforeEach(() => { backendAvailable(beginBackendRequest(), "all"); mocks.get.mockReset(); });

describe("global availability banner", () => {
  it("keeps a polite live region mounted and announces only fixed dependency messages", () => {
    render(<BackendOfflineBanner />);
    const region = screen.getByRole("status"); expect(region.getAttribute("aria-live")).toBe("polite");
    expect(screen.queryByRole("button")).toBeNull();
    act(() => backendUnavailable(beginBackendRequest(), "colab", 17));
    expect(region.textContent).toContain("Colab model service offline");
    expect(screen.getByText("Suggested retry: 17 seconds.")).toBeTruthy();
    act(() => backendAvailable(beginBackendRequest(), "all"));
    expect(screen.getByRole("status")).toBe(region); expect(region.textContent).toBe("");
  });
  it("does not probe or resubmit work automatically, and makes the explicit check no-store", async () => {
    backendUnavailable(beginBackendRequest(), "pipeline"); render(<BackendOfflineBanner />);
    expect(mocks.get).not.toHaveBeenCalled();
    mocks.get.mockImplementation(async () => backendAvailable(beginBackendRequest(), "all"));
    await userEvent.setup().click(screen.getByRole("button", { name: "Check connection" }));
    expect(mocks.get).toHaveBeenCalledWith("/ready", expect.objectContaining({ cache: "no-store", signal: expect.any(AbortSignal) }));
    await waitFor(() => expect(screen.queryByRole("button")).toBeNull());
  });
  it("aborts an owned readiness probe when the banner unmounts", async () => {
    backendUnavailable(beginBackendRequest()); mocks.get.mockReturnValue(new Promise(() => {}));
    const view = render(<BackendOfflineBanner />);
    await userEvent.setup().click(screen.getByRole("button", { name: "Check connection" }));
    const signal = mocks.get.mock.calls[0][1].signal as AbortSignal;
    expect(screen.getByRole("button").hasAttribute("disabled")).toBe(true);
    view.unmount(); expect(signal.aborted).toBe(true);
  });
});
