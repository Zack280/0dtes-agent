#!/usr/bin/env bash
# Persist data/regime.jsonl back to the repo, conflict-safe.
set -euo pipefail

if [ ! -f data/regime.jsonl ]; then
  echo "regime seed produced no output"
  exit 0
fi

git config user.name "0dtes-agent[bot]"
git config user.email "0dtes-agent[bot]@users.noreply.github.com"

git add data/regime.jsonl
if git diff --cached --quiet; then
  echo "no regime changes to commit"
  exit 0
fi

git commit -m "dataset: refresh regime history ($(wc -l < data/regime.jsonl) day(s))"
git fetch origin master
git pull --rebase origin master
git push
