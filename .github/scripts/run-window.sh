#!/usr/bin/env bash
# Run the agent's continuous scan loop for a bounded window while incrementally
# committing live data files (data/alerts.jsonl, data/captures.jsonl) every
# INCREMENT_MIN so the hosted dashboard stays near-live instead of only updating
# when the window finishes.
#
# Usage: run-window.sh <window-minutes>
#   - The agent runs in the background with --run-window-minutes <window-minutes>.
#   - Every INCREMENT_MIN (~3 min) a conflict-safe commit of the live data files
#     is attempted (fetch + rebase + push), so Pages/dashboard updates ~live.
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

# Attempt a conflict-safe commit of live data files if any have changes.
persist_data() {
  local anything=0
  local msg="dataset: "
  if [ -f data/alerts.jsonl ]; then
    git add data/alerts.jsonl
  fi
  if [ -f data/captures.jsonl ]; then
    git add data/captures.jsonl
  fi
  if ! git diff --cached --quiet; then
    anything=1
    msg+="alerts $(wc -l < data/alerts.jsonl 2>/dev/null || echo 0) captures $(wc -l < data/captures.jsonl 2>/dev/null || echo 0)"
  fi
  if [ "$anything" -eq 0 ]; then
    echo "[incremental] no live data changes"
    return 0
  fi
  git commit -m "$msg"
  git fetch origin master
  git pull --rebase origin master
  git push
  echo "[incremental] committed live data"
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
    persist_data || echo "[incremental] commit skipped (recoverable)"
  fi
done

# Final persist so nothing from the window is lost.
wait "$AGENT_PID" || true
echo "[run-window] agent finished; final persist"
persist_data
