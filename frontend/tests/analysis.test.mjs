import assert from "node:assert/strict";
import test from "node:test";
import { parsePipelineResult, verifiedSummary, withVerifiedTurn } from "../src/lib/analysis.ts";

function result() {
  return {
    plan_source: "single_question", version: "3.2", disclaimer: "Decision support only.",
    not_covered: [], warnings: [],
    problems: [{
      title: "Septic shock", status: "ok", queries: [], sources: [],
      items: [{
        recommendation: "Use norepinephrine.", quote: "we recommend norepinephrine",
        citation: { id: "S1", label: "SCCM 2024", section: "Shock", page: "12", strength: "strong", draft: false },
        patient_notes: [{ id: "case-check", title: "PRIVATE_FACT_MARKER", level: "caution", reasons: ["PRIVATE_FACT_MARKER"] }],
      }],
      unverified: [{ recommendation: "UNVERIFIED_MEMORY_MARKER", quote: "unverified", issues: ["not supported"] }],
    }],
  };
}

test("free-text and malformed answers are never accepted as verified results", () => {
  assert.throws(() => parsePipelineResult({ answer: "UNVERIFIED_ANSWER" }));
  const missingQuote = result();
  missingQuote.problems[0].items[0].quote = "";
  assert.throws(() => parsePipelineResult(missingQuote));
  const withIssues = result();
  withIssues.problems[0].items[0].issues = ["scope mismatch"];
  assert.throws(() => parsePipelineResult(withIssues));
});

test("conversation summaries contain only verified recommendations", () => {
  const summary = verifiedSummary(parsePipelineResult(result()));
  assert.match(summary, /Use norepinephrine/);
  assert.doesNotMatch(summary, /UNVERIFIED_MEMORY_MARKER|PRIVATE_FACT_MARKER/);
});

test("conversation history keeps the last six verified turns and ignores uncovered answers", () => {
  let history = [];
  for (let index = 0; index < 8; index++) history = withVerifiedTurn(history, `Question ${index}`, result());
  assert.equal(history.length, 6);
  assert.equal(history[0].question, "Question 2");
  const uncovered = result();
  uncovered.problems[0].items = [];
  uncovered.problems[0].status = "unverified";
  assert.deepEqual(withVerifiedTurn(history, "Uncovered question", uncovered), history);
});

test("empty metadata and contradictory verification status fail closed", () => {
  for (const field of ["plan_source", "version", "disclaimer"]) {
    const invalid = result();
    invalid[field] = " ";
    assert.throws(() => parsePipelineResult(invalid));
  }
  const invalid = result();
  invalid.problems[0].status = "error";
  assert.throws(() => parsePipelineResult(invalid));
});

test("only authoritative pipeline modes are accepted for live and saved results", () => {
  for (const mode of ["llm", "unknown", "case", "question"]) {
    assert.throws(() => parsePipelineResult({ ...result(), plan_source: mode }));
  }
  for (const mode of ["single_question", "rules", "llm+rules"]) {
    assert.equal(parsePipelineResult({ ...result(), plan_source: mode }).plan_source, mode);
  }
});

test("unverified problem labels and unknown private fields do not enter verified memory", () => {
  const value = result();
  value.facts = { summary: "PRIVATE_FACT_MARKER" };
  value.problems[0].title = "UNVERIFIED_PLAN_MARKER";
  value.problems[0].patientSummary = "PRIVATE_FACT_MARKER";
  value.problems[0].items[0].draftText = "UNVERIFIED_MEMORY_MARKER";
  const parsed = parsePipelineResult(value);
  assert.doesNotMatch(verifiedSummary(parsed), /UNVERIFIED_PLAN_MARKER|PRIVATE_FACT_MARKER|UNVERIFIED_MEMORY_MARKER/);
  assert.equal(parsed.facts, undefined);
  assert.equal(parsed.problems[0].patientSummary, undefined);
  assert.equal(parsed.problems[0].items[0].draftText, undefined);
});
