#!/usr/bin/env python3
"""
Converts qlty's SARIF output into a GitLab Code Quality report (the Code Climate JSON subset
GitLab reads from `artifacts:reports:codequality`). qlty has no Code Climate output of its own.

Usage:
  scripts/ci/sarif-to-codequality.py <file.sarif>... > gl-code-quality-report.json

The fingerprint is what GitLab diffs between the base and head reports to call an issue "new"
or "resolved", so it must survive unrelated edits. It therefore leaves out the line number
(code above the issue moves it) and the digits in the message ("complexity (count = 19)"
becomes 20 without being a different issue), and prefers qlty's partialFingerprints when a
result has them. Identical issues in one file are told apart by occurrence order.
"""
import hashlib
import json
import re
import sys
from collections import Counter

SEVERITY = {"error": "major", "warning": "minor", "note": "info", "none": "info"}


def convert(sarif_docs):
    seen = Counter()
    out = []
    for doc in sarif_docs:
        for run in doc.get("runs", []):
            for r in run.get("results", []):
                rule = r.get("ruleId") or "qlty"
                message = r.get("message", {}).get("text", "")
                loc = (r.get("locations") or [{}])[0].get("physicalLocation", {})
                path = loc.get("artifactLocation", {}).get("uri", "")
                region = loc.get("region", {})
                begin = region.get("startLine", 1)
                identity = json.dumps(r.get("partialFingerprints")) if r.get("partialFingerprints") else re.sub(r"\d+", "#", message)
                key = (rule, path, identity)
                seen[key] += 1
                out.append({
                    "type": "issue",
                    "check_name": rule,
                    "engine_name": rule.split(":", 1)[0],
                    "description": message,
                    "severity": SEVERITY.get(r.get("level", "warning"), "minor"),
                    "fingerprint": hashlib.md5(json.dumps([*key, seen[key]]).encode(), usedforsecurity=False).hexdigest(),
                    "location": {"path": path, "lines": {"begin": begin, "end": region.get("endLine", begin)}},
                })
    return out


if __name__ == "__main__":
    docs = []
    for p in sys.argv[1:]:
        with open(p) as f:
            docs.append(json.load(f))
    issues = convert(docs)
    json.dump(issues, sys.stdout)
    # The job log's summary; the report itself is for GitLab.
    for label, field in (("severity", "severity"), ("engine", "engine_name")):
        counts = Counter(i[field] for i in issues).most_common()
        print(f"{label}: " + ", ".join(f"{k} {n}" for k, n in counts), file=sys.stderr)
    print(f"total: {len(issues)}", file=sys.stderr)
