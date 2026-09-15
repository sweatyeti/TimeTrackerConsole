#!/usr/bin/env python3
"""Writes the session fixtures the TUI smoke harness drives.

Usage: tools/tui-fixtures.py <target-entries-dir>

Every fixture is a real entries/*.json session file. They are generated (not committed as JSON)
so the shapes stay reviewable next to the assertions that depend on them:

  basic.json        4 entries, newest-first 4(active) 3 2 1 - the fixture the review handoff uses,
                    with the in-progress entry at the HIGHEST id
  scale.json        45 entries / 44 completed task groups + 1 running entry (120x40 layout budget)
  unicode.json      CJK + emoji + combining-mark values in the task and in the description, plus one
                    ASCII-only control row (the alignment assertion compares the wide rows against it)
  complete.json     every entry complete -> the NOT ACTIVE banner (71-column art at 120 columns)
  mixedcase.json    two unlogged entries whose task differs only in case (one task group)
  corrupt-null.json       deserializes, task/description null  (must be normalized, then offered)
  corrupt-missing.json    no "entries" key at all              (must be normalized to empty)
  corrupt-future.json     schemaVersion 99                     (must be skipped, with a reason)
  corrupt-noschema.json   no "schemaVersion" key at all        (reads as 0 -> skipped, with a reason)
  corrupt-zero.json       schemaVersion 0                      (skipped, with a reason)
  corrupt-negative.json   schemaVersion -1                     (skipped, with a reason)
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

    # CJK / emoji / combining-mark values in BOTH the task and the description column, plus one
    # ASCII-only row as the control the alignment assertion compares the wide rows against (a wide
    # row must put its separators in the same columns as the ASCII row). The in-progress entry keeps
    # the highest id, as in every other fixture.
    fixtures["unicode.json"] = session("smoke-unicode", [
        entry(3, "\U0001f680 \u8a08\u753b", "caf\u00e9 e\u0301 \U0001f680 done",
              "2026-09-14T11:00:00"),
        entry(2, "\u8a08\u753b\u30ec\u30d3\u30e5\u30fc", "\u30bf\u30b9\u30af\u306e\u8aac\u660e",
              "2026-09-14T10:00:00", "2026-09-14T10:30:00"),
        entry(1, "wide-chars", "plain ascii control", "2026-09-14T09:00:00",
              "2026-09-14T09:30:00", logged=True),
    ], "33333333-3333-3333-3333-333333333333")

    # two unlogged entries whose task differs only in case: one task group, per the console path's
    # own counting/matching rules (the console smoke harness asserts it)
    fixtures["mixedcase.json"] = session("smoke-mixedcase", [
        entry(2, "Weeding", "second casing", "2026-09-14T10:00:00", "2026-09-14T10:30:00"),
        entry(1, "weeding", "first casing", "2026-09-14T09:00:00", "2026-09-14T09:30:00"),
    ], "99999999-9999-9999-9999-999999999999", started="2026-09-14T11:00:00")

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

    # schemaVersion absent entirely: the JSON layer leaves the record default (0), so the validator is
    # the only thing that can tell this apart from a supported file. Explicit 0 and negative values are
    # the same class of finding (a missing key and an explicit 0 deserialize identically).
    no_schema = session("smoke-noschema", [], "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                        started="2026-09-14T07:00:00")
    del no_schema["schemaVersion"]
    fixtures["corrupt-noschema.json"] = no_schema

    zero = session("smoke-zero", [], "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                   started="2026-09-14T06:00:00")
    zero["schemaVersion"] = 0
    fixtures["corrupt-zero.json"] = zero

    negative = session("smoke-negative", [], "cccccccc-cccc-cccc-cccc-cccccccccccc",
                       started="2026-09-14T05:00:00")
    negative["schemaVersion"] = -1
    fixtures["corrupt-negative.json"] = negative

    for name, payload in fixtures.items():
        if wanted and name not in wanted and name.removesuffix(".json") not in wanted:
            continue
        with open(os.path.join(out, name), "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2, ensure_ascii=False)
            handle.write("\n")

    print(f"wrote {len(fixtures)} fixtures to {out}")


if __name__ == "__main__":
    main()
