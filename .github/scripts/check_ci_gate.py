"""Fail closed unless every explicitly required GitHub Actions job succeeded."""

import json
import os


REQUIRED_JOBS = ("dotnet", "pytest", "frontend", "secret-scan")
KNOWN_RESULTS = {"success", "failure", "cancelled", "skipped", "neutral", "timed_out"}


def main():
    try:
        results = json.loads(os.environ.get("CI_JOB_RESULTS", ""))
    except (TypeError, ValueError):
        print("CI gate failed: missing or invalid job evidence.")
        return 1

    if not isinstance(results, dict):
        print("CI gate failed: job evidence must be an object.")
        return 1

    valid = set(results) == set(REQUIRED_JOBS)
    for name in REQUIRED_JOBS:
        evidence = results.get(name)
        status = evidence.get("result") if isinstance(evidence, dict) else None
        # Never log arbitrary input, job outputs, credentials or case text.
        label = status if isinstance(status, str) and status in KNOWN_RESULTS else "missing/invalid"
        print(f"{name}: {label}")
        valid = valid and status == "success"

    if not valid:
        print("CI gate failed: all and only the required jobs must report success.")
        return 1

    print("CI gate passed: every required job succeeded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
