#!/usr/bin/env bash
# Persist data/alerts.jsonl back to the repo. Conflict-safe: fetches latest and
# rebases before pushing so concurrent jobs (regime/label) cannot corrupt master.
set -euo pipefail

if [ ! -f data/alerts.jsonl ]; then
  echo "no alerts produced this window"
  exit 0
fi

git config user.name "0dtes-agent[bot]"
git config user.email "0dtes-agent[bot]@users.noreply.github.com"

git add data/alerts.jsonl
if git diff --cached --quiet; then
  echo "no alert changes to commit"
  exit 0
fi

git commit -m "dataset: record $(wc -l < data/alerts.jsonl) alert(s)"
git fetch origin master
git pull --rebase origin master
git push
