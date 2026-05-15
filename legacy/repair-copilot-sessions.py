"""Auto-repair Copilot CLI sessions that have dangling tool_use events.

Background
----------
When a Copilot CLI tool execution is aborted (Ctrl+C, hung MCP call,
network blip), the session's events.jsonl ends up with a tool_use event
that has no matching tool.execution_complete. On --resume, the CLI replays
the conversation history to the Anthropic API and the API rejects the
request with:

    400 invalid_request_error: tool_use ids were found without tool_result
    blocks immediately after: <id>. Each tool_use block must have a
    corresponding tool_result block in the next message.

This script scans every session in ~/.copilot/session-state/<uuid>/
events.jsonl, finds any dangling tool_use IDs, and inserts a synthetic
tool.execution_complete event with success=false + error.code=aborted
right after the corresponding tool.execution_start. The original file is
backed up to events.jsonl.bak-<timestamp> before any change.

Usage
-----
Invoked from Launch-Copilot.ps1 on every launch via:

    py -3 scripts/repair-copilot-sessions.py

Idempotent: a session that's already clean does nothing. Returns 0 on
success (with or without patches), nonzero only on hard error.

Designed to be silent unless it actually patches something.
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import uuid
from datetime import datetime
from pathlib import Path

SESSION_ROOT = Path(os.environ["USERPROFILE"]) / ".copilot" / "session-state"
DEFAULT_MODEL = "claude-opus-4.7"


def find_dangling(events: list[dict]) -> list[tuple[int, int, str, str]]:
    """Return list of (asst_idx, start_idx, tool_call_id, interaction_id) for
    every tool_use that lacks a matching tool.execution_complete.
    """
    tool_uses: dict[str, tuple[int, str]] = {}  # id -> (asst_idx, interactionId)
    tool_starts: dict[str, int] = {}  # id -> start_idx
    completed: set[str] = set()

    for i, e in enumerate(events):
        t = e.get("type", "")
        d = e.get("data", {}) or {}
        if t == "assistant.message":
            iid = d.get("interactionId", "")
            for tr in d.get("toolRequests", []) or []:
                tid = tr.get("toolCallId")
                if tid:
                    tool_uses[tid] = (i, iid)
        elif t == "tool.execution_start":
            tid = d.get("toolCallId")
            if tid:
                tool_starts[tid] = i
        elif t == "tool.execution_complete":
            tid = d.get("toolCallId")
            if tid:
                completed.add(tid)

    result = []
    for tid, (asst_idx, iid) in tool_uses.items():
        if tid not in completed:
            start_idx = tool_starts.get(tid)
            if start_idx is not None:
                result.append((asst_idx, start_idx, tid, iid))
    return result


def detect_model(events: list[dict]) -> str:
    """Find the most recent claude/gpt model used in this session."""
    for e in reversed(events):
        if e.get("type") == "tool.execution_complete":
            m = e.get("data", {}).get("model")
            if m:
                return m
        if e.get("type") == "session.model_change":
            m = e.get("data", {}).get("newModel")
            if m:
                return m
    return DEFAULT_MODEL


def synthesize_complete(
    tool_call_id: str, interaction_id: str, model: str, start_event: dict
) -> dict:
    return {
        "type": "tool.execution_complete",
        "data": {
            "toolCallId": tool_call_id,
            "model": model,
            "interactionId": interaction_id,
            "success": False,
            "error": {
                "message": (
                    "Tool execution was aborted (Ctrl+C, network drop, or hung "
                    "MCP call). Synthesized by Launch-Copilot.ps1 to satisfy the "
                    "Anthropic API tool_use/tool_result pairing invariant."
                ),
                "code": "aborted",
            },
        },
        "id": str(uuid.uuid4()),
        "timestamp": start_event.get("timestamp", ""),
        "parentId": start_event.get("id"),
    }


def repair_session(events_path: Path) -> int:
    """Repair one session's events.jsonl. Returns the number of patches applied."""
    try:
        with events_path.open("r", encoding="utf-8") as f:
            events = [json.loads(line) for line in f if line.strip()]
    except (OSError, json.JSONDecodeError):
        return 0

    dangling = find_dangling(events)
    if not dangling:
        return 0

    model = detect_model(events)

    # Build inserts list (start_idx, synthetic_event); insert in reverse order
    inserts = []
    for _asst_idx, start_idx, tid, iid in dangling:
        synthetic = synthesize_complete(tid, iid, model, events[start_idx])
        inserts.append((start_idx, synthetic))
    inserts.sort(key=lambda x: x[0], reverse=True)

    # Backup before mutating
    bak = events_path.with_suffix(
        events_path.suffix + f".bak-{datetime.now().strftime('%Y%m%d-%H%M%S')}"
    )
    try:
        shutil.copy2(events_path, bak)
    except OSError as exc:
        print(
            f"[repair-copilot-sessions] failed to back up {events_path}: {exc}",
            file=sys.stderr,
        )
        return 0

    new_events = list(events)
    for idx, syn in inserts:
        new_events.insert(idx + 1, syn)

    try:
        # Atomic write to a tmp file, then replace
        tmp = events_path.with_suffix(events_path.suffix + ".tmp")
        with tmp.open("w", encoding="utf-8", newline="\n") as f:
            for e in new_events:
                f.write(json.dumps(e, separators=(",", ":")) + "\n")
        os.replace(tmp, events_path)
    except OSError as exc:
        print(
            f"[repair-copilot-sessions] failed to write {events_path}: {exc}",
            file=sys.stderr,
        )
        # restore from backup
        try:
            shutil.copy2(bak, events_path)
        except OSError:
            pass
        return 0

    print(
        f"[repair-copilot-sessions] patched {len(dangling)} dangling tool_use(s) "
        f"in {events_path.parent.name} (backup: {bak.name})"
    )
    return len(dangling)


def main() -> int:
    if not SESSION_ROOT.exists():
        return 0
    total_sessions = 0
    total_patched = 0
    sessions_patched = 0
    for session_dir in SESSION_ROOT.iterdir():
        if not session_dir.is_dir():
            continue
        events_path = session_dir / "events.jsonl"
        if not events_path.exists():
            continue
        # Skip if locked (in-use by an active CLI session) — patching it would
        # race with the live writer. The lock file is created by the CLI and
        # removed on shutdown.
        if any(session_dir.glob("inuse.*.lock")):
            continue
        total_sessions += 1
        n = repair_session(events_path)
        if n:
            total_patched += n
            sessions_patched += 1
    if sessions_patched:
        print(
            f"[repair-copilot-sessions] scanned {total_sessions} sessions; "
            f"patched {total_patched} dangling tool_use(s) across {sessions_patched} session(s)"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
