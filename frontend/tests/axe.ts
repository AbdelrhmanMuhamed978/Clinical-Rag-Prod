import axe from "axe-core";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export type SeriousViolation = {
  id: string;
  impact: string | null;
  help: string;
  targets: string[];
};

/** Structural axe audit for jsdom-rendered pages. Color contrast requires a real
 * layout engine and is covered by the browser/mobile walkthrough, not jsdom. */
export async function seriousViolations(container: HTMLElement, snapshotName?: string): Promise<SeriousViolation[]> {
  const snapshotDir = process.env.A11Y_SNAPSHOT_DIR;
  if (snapshotDir && snapshotName) {
    mkdirSync(snapshotDir, { recursive: true });
    const cssHref = process.env.A11Y_CSS_HREF ?? "";
    writeFileSync(join(snapshotDir, `${snapshotName}.html`), `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Clinical RAG Platform</title>${cssHref ? `<link rel="stylesheet" href="${cssHref}">` : ""}</head><body><main id="content">${container.innerHTML}</main></body></html>`);
  }
  const result = await axe.run(container, {
    runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"] },
    rules: { "color-contrast": { enabled: false } },
  });
  return result.violations
    .filter(violation => violation.impact === "serious" || violation.impact === "critical")
    .map(violation => ({
      id: violation.id,
      impact: violation.impact ?? null,
      help: violation.help,
      targets: violation.nodes.map(node => node.target.join(" ")),
    }));
}
