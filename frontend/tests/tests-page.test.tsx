import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import TestsPage from "@/app/(app)/w/[workspaceId]/tests/page";
import type { TestCase } from "@/lib/types";
import { seriousViolations } from "./axe";
import { tabTo } from "./keyboard";

const mocks = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn(), del: vi.fn() }));
vi.mock("@/lib/api", () => ({ get: mocks.get, post: mocks.post, patch: mocks.patch, del: mocks.del }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: "workspace-1" }) }));

const clients: QueryClient[] = [];
let cases: TestCase[];

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
  clients.push(client);
  const view = render(<QueryClientProvider client={client}><TestsPage /></QueryClientProvider>);
  return { ...view, user: userEvent.setup() };
}

beforeEach(() => {
  cases = [{
    id: "case-1", workspaceId: "workspace-1", name: "Synthetic case", inputText: "Synthetic de-identified input",
    expectedJson: "{\"mustHaveProblems\":[]}", lastRunAt: null, lastRunJson: null,
  }];
  mocks.get.mockReset().mockResolvedValue(cases.map(item => ({ ...item })));
  mocks.post.mockReset(); mocks.patch.mockReset(); mocks.del.mockReset();
});
afterEach(() => { clients.splice(0).forEach(client => client.clear()); });

describe("Tests page accessibility", () => {
  it("has no serious or critical structural axe issues", async () => {
    const view = mount();
    await screen.findByText("Synthetic case");
    expect(await seriousViolations(view.container, "tests")).toEqual([]);
  });

  it("selects a case and starts a run from the keyboard alone", async () => {
    mocks.post.mockResolvedValue({ ran: 1, results: [{ caseId: "case-1", caseName: "Synthetic case", status: "passed", diff: null, durationMilliseconds: 5 }] });
    const { user } = mount();
    await screen.findByText("Synthetic case");
    const checkbox = await tabTo(user, element => element.tagName === "INPUT" && element.getAttribute("aria-label") === "Select Synthetic case", "case checkbox") as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    await user.keyboard(" ");
    expect(checkbox.checked).toBe(true);
    expect(screen.getByText("1 of 1 selected")).toBeTruthy();
    const run = await tabTo(user, element => element.tagName === "BUTTON" && element.textContent?.startsWith("Run selected") === true, "Run selected");
    await user.keyboard("{Enter}");
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/tests/run", { caseIds: ["case-1"] }));
    await screen.findByText("Run results");
    expect(run.hasAttribute("disabled")).toBe(false);
  });
});
