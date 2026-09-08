#!/usr/bin/env bash
# Run the agent's continuous scan loop for a bounded window while incrementally
# committing data/alerts.jsonl every INCREMENT_MIN so the hosted dashboard stays
# near-live instead of only updating when the window finishes.
#
# Usage: run-window.sh <window-minutes>
#   - The agent runs in the background with --run-window-minutes <window-minutes>.
#   - Every INCREMENT_MIN (~3 min) a conflict-safe commit of data/alerts.jsonl is
#     attempted (fetch + rebase + push), so Pages/dashboard updates ~live.
#   - A final persist runs when the agent exits, guaranteeing the window's data
#     is not lost even if the runner later stops.
set -euo pipefail

WINDOW_MIN="${1:?usage: run-window.sh <window-minutes>}"
INCREMENT_MIN="${INCREMENT_MIN:-3}"
NTFY_TOPIC="${NTFY_TOPIC:-}"

configure_git() {
  git config user.name "0dtes-agent[bot]"
  git config user.email "0dtes-agent[bot]@users.noreply.github.com"
}

# Attempt a conflict-safe commit of data/alerts.jsonl if it has changes.
persist_alerts() {
  [ -f data/alerts.jsonl ] || { echo "[incremental] no alerts file yet"; return 0; }
  git add data/alerts.jsonl
  if git diff --cached --quiet; then
    echo "[incremental] no alert changes"
    return 0
  fi
  git commit -m "dataset: record $(wc -l < data/alerts.jsonl) alert(s)"
  git fetch origin master
  git pull --rebase origin master
  git push
  echo "[incremental] committed alerts.jsonl"
}

configure_git

echo "[run-window] starting agent for ${WINDOW_MIN} min (ntfy topic: '${NTFY_TOPIC}')"
export NTFY_TOPIC
dotnet run --project 0dtes-agent.csproj -c Release -- --run-window-minutes "$WINDOW_MIN" &
AGENT_PID=$!

# Reap commit loop until the agent exits.
while kill -0 "$AGENT_PID" 2>/dev/null; do
  sleep "$INCREMENT_MIN"m
  if kill -0 "$AGENT_PID" 2>/dev/null; then
    persist_alerts || echo "[incremental] commit skipped (recoverable)"
  fi
done

# Final persist so nothing from the window is lost.
wait "$AGENT_PID" || true
echo "[run-window] agent finished; final persist"
persist_alerts
