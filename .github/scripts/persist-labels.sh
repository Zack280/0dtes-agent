#!/usr/bin/env bash
# Persist data/labels.jsonl back to the repo, conflict-safe.
set -euo pipefail

if [ ! -f data/labels.jsonl ]; then
  echo "no labels produced this run"
  exit 0
fi

git config user.name "0dtes-agent[bot]"
git config user.email "0dtes-agent[bot]@users.noreply.github.com"

git add data/labels.jsonl
if git diff --cached --quiet; then
  echo "no label changes to commit"
  exit 0
fi

git commit -m "dataset: label $(wc -l < data/labels.jsonl) alert(s)"
git fetch origin master
git pull --rebase origin master
git push
