import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { ClinicalLimitation, FirstUseNotice } from "@/components/clinical-limitations";

describe("clinical intended-use notices", () => {
  beforeEach(() => window.localStorage.clear());

  it("renders the decision-support limitation statement", () => {
    render(<ClinicalLimitation />);
    expect(screen.getByRole("note").textContent).toContain("Decision support only; requires clinical review.");
    expect(screen.getByRole("note").textContent).toContain("Not covered");
  });

  it("shows the first-use notice until acknowledged and remembers the acknowledgement", async () => {
    const first = render(<FirstUseNotice />);
    await waitFor(() => expect(screen.getByRole("status", { name: "Intended use and limitations" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Acknowledge" }));
    expect(window.localStorage.getItem("clinical-rag-intended-use-acknowledged")).toBe("true");
    first.unmount();
    render(<FirstUseNotice />);
    expect(screen.queryByRole("region", { name: "Intended use and limitations" })).toBeNull();
  });
});
