#!/usr/bin/env bash
# Persist live data (data/alerts.jsonl, data/captures.jsonl) back to the repo.
# Conflict-safe: fetches latest and rebases before pushing so concurrent jobs
# (regime/label) cannot corrupt master.
set -euo pipefail

if [ ! -f data/alerts.jsonl ] && [ ! -f data/captures.jsonl ]; then
  echo "no live data produced this window"
  exit 0
fi

git config user.name "0dtes-agent[bot]"
git config user.email "0dtes-agent[bot]@users.noreply.github.com"

git add data/alerts.jsonl data/captures.jsonl 2>/dev/null || true
if git diff --cached --quiet; then
  echo "no live data changes to commit"
  exit 0
fi

git commit -m "dataset: record $(wc -l < data/alerts.jsonl 2>/dev/null || echo 0) alert(s), $(wc -l < data/captures.jsonl 2>/dev/null || echo 0) option capture(s)"
git fetch origin master
git pull --rebase origin master
git push
