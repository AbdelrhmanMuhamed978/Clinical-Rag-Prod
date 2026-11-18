"""Run axe-core in a real Chromium layout against jsdom page snapshots.

The frontend accessibility tests can write snapshots with:
  A11Y_SNAPSHOT_DIR=<dir> A11Y_CSS_HREF=file:///.../next-static.css \\
    npx vitest run <page tests>

This script then checks axe serious/critical issues and horizontal overflow at
mobile and desktop widths. It does not use a real authenticated stack or patient
data; snapshots contain only synthetic fixtures.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

from playwright.sync_api import sync_playwright


ROOT = Path(__file__).resolve().parents[1]
PAGES = ("playground", "documents", "chunk-review", "workflow", "rules", "tests")
VIEWPORTS = ((320, 640), (375, 812), (768, 1024), (1440, 900))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshots", type=Path, required=True)
    parser.add_argument("--axe", type=Path, default=ROOT / "frontend/node_modules/axe-core/axe.min.js")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)

    if args.output.exists():
        print(f"Refusing to overwrite existing audit report: {args.output}", file=sys.stderr)
        return 2
    missing = [page for page in PAGES if not (args.snapshots / f"{page}.html").is_file()]
    if missing:
        print(f"Missing snapshots: {', '.join(missing)}", file=sys.stderr)
        return 2

    results: list[dict[str, object]] = []
    failed = False
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        try:
            for name in PAGES:
                path = (args.snapshots / f"{name}.html").resolve()
                for width, height in VIEWPORTS:
                    page = browser.new_page(viewport={"width": width, "height": height})
                    try:
                        page.goto(path.as_uri(), wait_until="load")
                        page.add_script_tag(path=str(args.axe))
                        measured = page.evaluate(
                            """async () => {
                              const result = await axe.run(document, {
                                runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"] }
                              });
                              const counts = {};
                              for (const impact of ["critical", "serious", "moderate", "minor"]) {
                                counts[impact] = result.violations.filter(v => (v.impact || "minor") === impact).length;
                              }
                              return {
                                counts,
                                violations: result.violations
                                  .filter(v => v.impact === "critical" || v.impact === "serious")
                                  .map(v => ({
                                    id: v.id, impact: v.impact, help: v.help,
                                    nodes: v.nodes.map(n => ({
                                      target: n.target, html: n.html, failureSummary: n.failureSummary
                                    }))
                                  })),
                                incomplete: result.incomplete.length,
                                scrollWidth: document.documentElement.scrollWidth,
                                bodyScrollWidth: document.body.scrollWidth,
                                innerWidth: window.innerWidth
                              };
                            }"""
                        )
                        overflow = measured["scrollWidth"] > measured["innerWidth"] + 1
                        serious = measured["counts"]["critical"] + measured["counts"]["serious"]
                        failed = failed or serious > 0 or overflow
                        results.append({
                            "page": name, "width": width, "height": height,
                            **measured, "horizontalOverflow": overflow,
                        })
                    finally:
                        page.close()
        finally:
            browser.close()

    report = {
        "schemaVersion": "a11y-audit/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "axe": "axe-core 4.14.0",
        "snapshots": str(args.snapshots),
        "viewports": [{"width": width, "height": height} for width, height in VIEWPORTS],
        "failed": failed,
        "results": results,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")

    for row in results:
        print(
            f"{row['page']:14} {row['width']:>4}x{row['height']:<4} "
            f"critical={row['counts']['critical']} serious={row['counts']['serious']} "
            f"moderate={row['counts']['moderate']} minor={row['counts']['minor']} "
            f"overflow={row['horizontalOverflow']} scroll={row['scrollWidth']} viewport={row['innerWidth']}"
        )
    if failed:
        print("Accessibility audit FAILED serious/critical or horizontal overflow.", file=sys.stderr)
        return 1
    print("Accessibility audit passed: no serious/critical axe issues and no horizontal overflow.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
