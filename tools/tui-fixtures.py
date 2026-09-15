#!/usr/bin/env python3
"""Writes the session fixtures the TUI smoke harness drives.

Usage: tools/tui-fixtures.py <target-entries-dir>

Every fixture is a real entries/*.json session file. They are generated (not committed as JSON)
so the shapes stay reviewable next to the assertions that depend on them:

  basic.json        4 entries, newest-first 4(active) 3 2 1 - the fixture the review handoff uses,
                    with the in-progress entry at the HIGHEST id
  scale.json        45 entries / 44 completed task groups + 1 running entry (120x40 layout budget)
  unicode.json      CJK + emoji + combining-mark task/description
  long.json         a short first task, for the "type a wider task afterwards" check
  corrupt-null.json       deserializes, task/description null  (must be normalized, then offered)
  corrupt-missing.json    no "entries" key at all              (must be normalized to empty)
  corrupt-future.json     schemaVersion 99                     (must be skipped, with a reason)
"""

import json
import os
import sys

SCHEMA = 2


def session(name, entries, session_id, started="2026-09-14T08:00:00"):
    return {
        "schemaVersion": SCHEMA,
        "sessionId": session_id,
        "name": name,
        "startedAt": started,
        "endedAt": None,
        "entries": entries,
    }


def entry(entry_id, task, description, start, end=None, logged=False, deleted=False):
    return {
        "id": entry_id,
        "startTime": start,
        "endTime": end,
        "task": task,
        "description": description,
        "logged": logged,
        "isComplete": end is not None,
        "isDeleted": deleted,
    }


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)

    out = sys.argv[1]
    wanted = sys.argv[2:]
    os.makedirs(out, exist_ok=True)

    fixtures = {}

    fixtures["basic.json"] = session("smoke-basic", [
        entry(4, "active", "in progress", "2026-09-14T12:00:00"),
        entry(3, "planning", "weekly review", "2026-09-14T10:00:00", "2026-09-14T10:30:00"),
        entry(2, "reading", "chapter 3", "2026-09-14T09:30:00", "2026-09-14T10:00:00"),
        entry(1, "weeding", "back bed", "2026-09-14T09:00:00", "2026-09-14T09:30:00", logged=True),
    ], "11111111-1111-1111-1111-111111111111")

    # 44 completed groups + one running entry: enough to overflow any window
    scale = [entry(45, "running", "still going", "2026-09-14T18:00:00")]
    for index in range(44, 0, -1):
        task = f"task-{index:02d}"
        scale.append(entry(index, task, f"entry {index}",
                           f"2026-09-14T{9 + (index % 8):02d}:00:00",
                           f"2026-09-14T{9 + (index % 8):02d}:30:00",
                           logged=(index % 2 == 0)))
    fixtures["scale.json"] = session("smoke-scale", scale, "22222222-2222-2222-2222-222222222222")

    fixtures["unicode.json"] = session("smoke-unicode", [
        entry(2, "\u8a08\u753b\u30ec\u30d3\u30e5\u30fc", "caf\u00e9 e\u0301 \U0001f680 done",
              "2026-09-14T10:00:00"),
        entry(1, "\u30bf\u30b9\u30af", "wide chars above", "2026-09-14T09:00:00",
              "2026-09-14T09:30:00"),
    ], "33333333-3333-3333-3333-333333333333")

    fixtures["long.json"] = session("smoke-long", [
        entry(1, "a", "short", "2026-09-14T09:00:00"),
    ], "44444444-4444-4444-4444-444444444444")

    # every entry complete -> the NOT ACTIVE banner, whose block art is 71 columns wide at the TUI's
    # wide spacing (the narrow-terminal scenario needs a banner that has to be shrunk to fit)
    fixtures["complete.json"] = session("smoke-complete", [
        entry(2, "planning", "weekly review", "2026-09-14T10:00:00", "2026-09-14T10:30:00"),
        entry(1, "weeding", "back bed", "2026-09-14T09:00:00", "2026-09-14T09:30:00", logged=True),
    ], "88888888-8888-8888-8888-888888888888")

    fixtures["corrupt-null.json"] = session("smoke-null", [
        {"id": 1, "startTime": "2026-09-14T09:00:00", "endTime": None, "task": None,
         "description": None, "logged": False, "isComplete": False, "isDeleted": False},
    ], "55555555-5555-5555-5555-555555555555", started="2026-09-14T13:00:00")

    missing = session("smoke-missing", [], "66666666-6666-6666-6666-666666666666",
                      started="2026-09-14T12:00:00")
    del missing["entries"]
    fixtures["corrupt-missing.json"] = missing

    future = session("smoke-future", [], "77777777-7777-7777-7777-777777777777",
                     started="2026-09-14T10:00:00")
    future["schemaVersion"] = 99
    fixtures["corrupt-future.json"] = future

    for name, payload in fixtures.items():
        if wanted and name not in wanted and name.removesuffix(".json") not in wanted:
            continue
        with open(os.path.join(out, name), "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2, ensure_ascii=False)
            handle.write("\n")

    print(f"wrote {len(fixtures)} fixtures to {out}")


if __name__ == "__main__":
    main()
