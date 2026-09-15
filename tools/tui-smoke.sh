#!/usr/bin/env bash
#
# Behavioural smoke harness for the Terminal.Gui interface (--tui), driven through tmux.
#
# Why tmux: Terminal.Gui needs a real terminal emulator (a bare pty never answers its cursor query,
# so a raw pty either hangs or reports no ANSI support). tmux gives the app a real screen and lets
# the harness send keys and read back the rendered frame.
#
# Why a scratch cwd: EntryStore resolves entries/ against the process working directory and writes
# session files there. The harness therefore runs the built DLL from a disposable directory under
# $TMPDIR and generates its fixtures there - it never reads or writes the repo's own entries/.
#
# Usage:
#   tools/tui-smoke.sh                 # build, then run every scenario
#   tools/tui-smoke.sh --no-build      # use the existing bin/Debug DLL
#   tools/tui-smoke.sh --keep          # keep the scratch dir and print its path
#
# Exit code is non-zero if any check failed. Every check prints PASS/FAIL with what it looked for,
# so the output can be pasted into a review as evidence.
set -uo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DLL="$REPO_ROOT/bin/Debug/net10.0/TimeTrackerConsole.dll"
FIXTURES="$REPO_ROOT/tools/tui-fixtures.py"
FRAME_CHECKER="$REPO_ROOT/tools/tui-frame.py"

# Point the harness at a pre-built DLL instead (e.g. a baseline worktree build, to show which checks
# a change actually fixes): TTC_SMOKE_DLL=/tmp/ttc-baseline/bin/Debug/net10.0/TimeTrackerConsole.dll
DLL="${TTC_SMOKE_DLL:-$DLL}"

BUILD=1
KEEP=0
ONLY=""
while [ $# -gt 0 ]; do
	case "$1" in
		--no-build) BUILD=0 ; shift ;;
		--keep) KEEP=1 ; shift ;;
		--only) shift; ONLY="${1:-}" ; shift ;;
		*) echo "unknown argument: $1" >&2; exit 2 ;;
	esac
done

SOCKET="ttc-smoke-$$"
SCRATCH=$(mktemp -d "${TMPDIR:-/tmp}/ttc-smoke-XXXXXX")
PASS=0
FAIL=0
RESULTS=()

pass() { PASS=$((PASS + 1)); RESULTS+=("PASS  $1"); echo "  PASS  $1"; }
fail() { FAIL=$((FAIL + 1)); RESULTS+=("FAIL  $1"); echo "  FAIL  $1"; }

contains() { # name file needle
	if grep -qF -- "$3" "$2"; then pass "$1"; else fail "$1 - expected to find: $3"; fi
}

absent() { # name file needle
	if grep -qF -- "$3" "$2"; then fail "$1 - did not expect: $3"; else pass "$1"; fi
}

# ---- tmux plumbing ---------------------------------------------------------------------------

new_scene() { # name width height dir  -> starts the app in the pane
	local name="$1" width="$2" height="$3" dir="$4"
	shift 4
	tmux -L "$SOCKET" new-session -d -x "$width" -y "$height" -s "$name" -c "$dir" \
		"TERM=xterm-256color dotnet $DLL $*; echo TTC-EXITED; exec bash"
}

send() { tmux -L "$SOCKET" send-keys -t "$1" "$2"; }

type_text() { tmux -L "$SOCKET" send-keys -t "$1" -l "$2"; }

frame() { # scene outfile
	tmux -L "$SOCKET" capture-pane -p -t "$1" > "$2"
}

# The widest banner art line in a captured frame, in columns. The banner is the first block of rows
# inside the window frame and the art is drawn from '#' glyphs only, so dropping every other
# character separates the art from the window border - and trimming the remaining spaces removes the
# Label's centring padding, leaving the block's own width.
art_width() { # frame-file
	sed -n '2,6p' "$1" | sed -e 's/[^# ]//g' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
		| awk '{ if (length($0) > max) max = length($0) } END { print max + 0 }'
}

pane_has() { # scene needle
	tmux -L "$SOCKET" capture-pane -p -t "$1" 2>/dev/null | grep -qF -- "$2"
}

wait_for() { # scene needle timeout-seconds
	local i
	for i in $(seq 1 "$3"); do
		if pane_has "$1" "$2"; then return 0; fi
		sleep 1
	done
	return 1
}

# the session picker is the first runnable on this path; Enter accepts its preselected item
pick_first_session() { # scene
	if ! wait_for "$1" "Select a session to resume" 40; then
		frame "$1" "$SCRATCH/$1-picker-timeout.txt"
		return 1
	fi
	sleep 1
	send "$1" Enter
	return 0
}

# the window is up when its status bar is on screen. A timeout captures whatever the pane shows, so a
# crash (or a status bar that never rendered) leaves its evidence behind
wait_for_window() { # scene
	if ! wait_for "$1" "F6" 30; then
		frame "$1" "$SCRATCH/$1-window-timeout.txt"
		return 1
	fi
	return 0
}

# waits until the app process is gone (F6 exits and the pane returns to its shell)
wait_for_exit() { # scene
	local i
	for i in $(seq 1 30); do
		if tmux -L "$SOCKET" capture-pane -p -t "$1" 2>/dev/null | grep -q 'TTC-EXITED'; then
			tmux -L "$SOCKET" capture-pane -p -t "$1" > "$SCRATCH/$1-exit.txt"
			return 0
		fi
		sleep 1
	done
	return 1
}

kill_scene() { tmux -L "$SOCKET" kill-session -t "$1" 2>/dev/null; }

# asserts no scene ever printed an unhandled exception (checked on the exit frames)
no_exception() { # name file
	if [ ! -f "$2" ]; then
		fail "$1 - no captured frame at $2 (the check would otherwise pass for the wrong reason)"
		return
	fi
	if grep -qE 'Unhandled exception|System\.[A-Za-z]*Exception' "$2"; then
		fail "$1 - frame contains an exception"
	else
		pass "$1"
	fi
}

# Display-column alignment: every row of every table in the frame must put its column separators in
# the same columns. Measured in characters rather than columns, a row containing wide (CJK/emoji) text
# always *looks* shifted against an ASCII row even when the terminal shows them aligned, so the
# assertion works on display columns (see tools/tui-frame.py). As with every other check here, a
# missing frame is a failure: alignment cannot be proven from a frame that was never captured.
check_alignment() { # name frame required-table-blocks
	local name="$1" frame="$2" blocks="${3:-2}" output
	if [ ! -f "$frame" ]; then
		fail "$name - no captured frame at $frame"
		return
	fi
	if output=$(python3 "$FRAME_CHECKER" align "$frame" --require-blocks "$blocks" 2>&1); then
		pass "$name - $(echo "$output" | tail -1 | sed 's/^ *//')"
	else
		fail "$name - $(echo "$output" | grep -E 'MISALIGNED|expected|only ' | head -3 | tr '\n' ' ')"
	fi
}

# ---- scenarios -------------------------------------------------------------------------------

scenario_long_task() {
	echo "scenario: editing a task to something wider must re-measure the entry columns immediately"
	local dir="$SCRATCH/long" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" basic > /dev/null

	new_scene long 120 40 "$dir" continue --tui
	pick_first_session long || { fail "long: the session picker never appeared"; return; }
	wait_for_window long || { fail "long: window never rendered"; return; }
	sleep 2

	# row 0 is the in-progress entry, so Enter goes straight to the update dialog (in-progress
	# entries are not deletable); the task field is pre-selected, so typing replaces it. The row
	# count does not change - only the content does, which is the case that used to leave the table
	# with stale column widths until something else forced a re-measure.
	send long Enter ; sleep 2
	type_text long "WIDER-TASK-THAN-BEFORE-AAAAA"
	send long Enter
	sleep 3

	frame long "$SCRATCH/long-frame.txt"
	contains "long: the wider task renders in full without a resize" \
		"$SCRATCH/long-frame.txt" "WIDER-TASK-THAN-BEFORE-AAAAA"
	no_exception "long: no exception" "$SCRATCH/long-frame.txt"

	kill_scene long
}

scenario_selection_survives_refresh() {
	echo "scenario: F5 must not reset the selection to row 0"
	local dir="$SCRATCH/selection" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" basic > /dev/null

	new_scene sel 120 40 "$dir" continue --tui
	pick_first_session sel || { fail "selection: the session picker never appeared"; return; }
	wait_for_window sel || { fail "selection: window never rendered"; return; }
	sleep 2

	send sel Down ; sleep 1 ; send sel Down ; sleep 1     # row 2 = entry 2 (newest-first 4,3,2,1)

	# No Enter before the refresh: pressing Enter would record the selected id, which is exactly the
	# state the old refresh relied on. Navigating with Down only is the case that used to snap back
	# to row 0 - so the observable is which entry the delete confirm names afterwards.
	send sel F5 ; sleep 3                                 # stop tracking -> Refresh()
	send sel Enter ; sleep 2
	frame sel "$SCRATCH/sel-after.txt"
	contains "selection: the entry the user navigated to is still selected after F5" \
		"$SCRATCH/sel-after.txt" "id 2"
	no_exception "selection: no exception" "$SCRATCH/sel-after.txt"
	send sel Escape ; sleep 1
	send sel Escape ; sleep 1

	kill_scene sel
}

scenario_scale() {
	echo "scenario: 44 task groups at 120x40 keep the totals line and usable entry rows"
	local dir="$SCRATCH/scale" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" scale > /dev/null

	new_scene scale 120 40 "$dir" continue --tui
	pick_first_session scale || { fail "scale: the session picker never appeared"; return; }
	wait_for_window scale || { fail "scale: window never rendered"; return; }
	sleep 2
	frame scale "$SCRATCH/scale-frame.txt"

	contains "scale: totals line is still on screen" "$SCRATCH/scale-frame.txt" "Total unlogged task time"

	# entry rows are the ones carrying an "#<id>" cell
	local rows
	rows=$(grep -oE '#[0-9]+' "$SCRATCH/scale-frame.txt" | wc -l)
	if [ "$rows" -ge 3 ]; then
		pass "scale: at least 3 entry rows visible ($rows found)"
	else
		fail "scale: only $rows entry rows visible"
	fi

	send scale C-End ; sleep 2                             # jump to the last row
	send scale Enter ; sleep 2
	frame scale "$SCRATCH/scale-last.txt"
	contains "scale: the oldest entry is reachable (Ctrl+End then Enter names id 1)" \
		"$SCRATCH/scale-last.txt" "id 1"
	send scale Escape ; sleep 1

	kill_scene scale
}

scenario_corrupt() {
	echo "scenario: malformed-but-deserializable sessions are normalized, unsupported ones are reported"
	local dir="$SCRATCH/corrupt" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" corrupt-null corrupt-missing corrupt-future \
		corrupt-noschema corrupt-zero corrupt-negative > /dev/null

	new_scene corrupt 120 40 "$dir" continue --tui
	# capture the PICKER frame explicitly: it is the only frame that lists the session names, so this
	# is where "offered" vs "refused" is observable (the window frame after Enter only shows the name
	# of the session that was resumed)
	wait_for corrupt "Select a session to resume" 40 || { fail "corrupt: the session picker never appeared"; return; }
	sleep 2
	frame corrupt "$SCRATCH/corrupt-picker.txt"
	contains "corrupt: the readable session is offered" "$SCRATCH/corrupt-picker.txt" "smoke-null"
	absent "corrupt: the newer-schema file is not offered" "$SCRATCH/corrupt-picker.txt" "smoke-future"
	absent "corrupt: the no-schemaVersion file is not offered" "$SCRATCH/corrupt-picker.txt" "smoke-noschema"
	absent "corrupt: the schemaVersion:0 file is not offered" "$SCRATCH/corrupt-picker.txt" "smoke-zero"
	absent "corrupt: the negative-schemaVersion file is not offered" "$SCRATCH/corrupt-picker.txt" "smoke-negative"
	no_exception "corrupt: no exception in the picker frame" "$SCRATCH/corrupt-picker.txt"

	send corrupt Enter
	wait_for_window corrupt || { fail "corrupt: window never rendered"; return; }
	sleep 2
	frame corrupt "$SCRATCH/corrupt-frame.txt"
	contains "corrupt: the null-task session renders (task normalized to none)" \
		"$SCRATCH/corrupt-frame.txt" "none"
	no_exception "corrupt: no exception" "$SCRATCH/corrupt-frame.txt"

	send corrupt F6 ; sleep 3
	if wait_for_exit corrupt; then
		contains "corrupt: the skipped file is named with a reason after exit" \
			"$SCRATCH/corrupt-exit.txt" "Skipped corrupt-future.json"
		contains "corrupt: the reason names the unsupported schema version" \
			"$SCRATCH/corrupt-exit.txt" "schema version 99"

		# the finding this scenario exists for: a missing schemaVersion deserializes to 0 and must be
		# reported as unsupported, not offered as a usable session
		contains "corrupt: the missing-schemaVersion file is skipped with a reason" \
			"$SCRATCH/corrupt-exit.txt" "Skipped corrupt-noschema.json"
		contains "corrupt: the missing-schemaVersion reason is actionable" \
			"$SCRATCH/corrupt-exit.txt" "schema version 0 is not supported"
		contains "corrupt: the reason names the supported range" \
			"$SCRATCH/corrupt-exit.txt" "schema versions 1..2"
		contains "corrupt: an explicit schemaVersion 0 is skipped too" \
			"$SCRATCH/corrupt-exit.txt" "Skipped corrupt-zero.json"
		contains "corrupt: a negative schemaVersion is skipped too" \
			"$SCRATCH/corrupt-exit.txt" "Skipped corrupt-negative.json"
		no_exception "corrupt: no exception on the exit frame" "$SCRATCH/corrupt-exit.txt"
	else
		fail "corrupt: the app did not exit on F6"
	fi

	kill_scene corrupt

	# the session with no "entries" key at all: still offered, opens an empty session
	new_scene corrupt2 120 40 "$dir" continue --tui
	wait_for corrupt2 "Select a session to resume" 40 || { fail "corrupt2: the session picker never appeared"; return; }
	sleep 1
	send corrupt2 Down                                     # second session = corrupt-missing.json
	sleep 1
	send corrupt2 Enter
	wait_for_window corrupt2 || { fail "corrupt2: window never rendered"; return; }
	sleep 2
	frame corrupt2 "$SCRATCH/corrupt2-frame.txt"
	contains "corrupt: the missing-entries session opens and renders" "$SCRATCH/corrupt2-frame.txt" "F6"
	no_exception "corrupt2: no exception" "$SCRATCH/corrupt2-frame.txt"
	kill_scene corrupt2
}

scenario_esc() {
	echo "scenario: Esc is inert on the main window and cancels dialogs"
	local dir="$SCRATCH/esc" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" basic > /dev/null

	new_scene esc 120 40 "$dir" continue --tui
	pick_first_session esc || { fail "esc: the session picker never appeared"; return; }
	wait_for_window esc || { fail "esc: window never rendered"; return; }
	sleep 2

	send esc Escape ; sleep 2
	frame esc "$SCRATCH/esc-main.txt"
	contains "esc: the main window ignores Esc" "$SCRATCH/esc-main.txt" "F6"

	send esc F2 ; sleep 2
	frame esc "$SCRATCH/esc-dialog.txt"
	contains "esc: F2 opens the task dialog" "$SCRATCH/esc-dialog.txt" "Entry started, enter a task if desired:"
	send esc Escape ; sleep 2
	frame esc "$SCRATCH/esc-dialog-closed.txt"
	absent "esc: Esc cancels the dialog" "$SCRATCH/esc-dialog-closed.txt" "Entry started, enter a task if desired:"
	contains "esc: the window is still there afterwards" "$SCRATCH/esc-dialog-closed.txt" "F6"

	kill_scene esc
}

scenario_narrow() {
	echo "scenario: 60x24 keeps the banner, the shortcut bar and the totals line intact"
	local dir="$SCRATCH/narrow" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" complete > /dev/null

	# the full titles are the normal-size presentation; the compact ones must only replace them
	# below the threshold in ViewportBudget
	new_scene wide 120 40 "$dir" continue --tui
	pick_first_session wide || { fail "narrow(wide): the session picker never appeared"; return; }
	wait_for_window wide || { fail "narrow(wide): window never rendered"; return; }
	sleep 2
	frame wide "$SCRATCH/wide-frame.txt"
	contains "narrow: full status titles at 120 columns" "$SCRATCH/wide-frame.txt" "Stop tracking"

	local wide_art
	wide_art=$(art_width "$SCRATCH/wide-frame.txt")
	if [ "$wide_art" -eq 71 ]; then
		pass "narrow: the wide 71-column banner block is used at 120 columns"
	else
		fail "narrow: expected the 71-column banner block at 120 columns, measured $wide_art"
	fi
	kill_scene wide

	new_scene narrow 60 24 "$dir" continue --tui
	pick_first_session narrow || { fail "narrow: the session picker never appeared"; return; }
	# wait for the window frame rather than the status bar: on a 60-column terminal the shortcut bar
	# is exactly what may clip F6, and that must be reported as a missing shortcut, not as a window
	# that never rendered
	wait_for narrow "TimeTracker - smoke-complete" 40 || { fail "narrow: window never rendered"; return; }
	sleep 2
	frame narrow "$SCRATCH/narrow-frame.txt"

	contains "narrow: the totals line fits" "$SCRATCH/narrow-frame.txt" "Total unlogged task time"
	absent "narrow: full shortcut titles are replaced at 60 columns" "$SCRATCH/narrow-frame.txt" "Stop tracking"
	for key in F2 F3 F4 F5 F6; do
		contains "narrow: $key is on the status bar" "$SCRATCH/narrow-frame.txt" "$key"
	done

	# The banner block is built at the widest spacing that fits, so at 60 columns "NOT ACTIVE" is the
	# 55-column (1,3) block rather than the 71-column one clipped at the window edge; the 45-column
	# "ACTIVE" block is what a 60-column terminal *can* still show at the wide spacing.
	local art
	art=$(art_width "$SCRATCH/narrow-frame.txt")
	if [ "$art" -eq 55 ]; then
		pass "narrow: the banner shrank to the 55-column block instead of being clipped ($art columns)"
	else
		fail "narrow: the banner block is not the expected 55-column fit (measured $art columns)"
	fi
	no_exception "narrow: no exception" "$SCRATCH/narrow-frame.txt"

	# the dialogs bound their field widths to the driver's screen width, so a dialog on a 60-column
	# terminal must fit rather than overflow the right edge (its border row is measured, not assumed)
	send narrow F2 ; sleep 2
	frame narrow "$SCRATCH/narrow-dialog.txt"
	contains "narrow: the task dialog opens at 60 columns" "$SCRATCH/narrow-dialog.txt" "Entry started, enter a task if desired:"
	contains "narrow: the dialog buttons are on screen" "$SCRATCH/narrow-dialog.txt" "Cancel"
	no_exception "narrow: the dialog did not crash" "$SCRATCH/narrow-dialog.txt"

	local dialog_width
	dialog_width=$(sed -nE 's/.*(┏[^┓]*┓).*/\1/p' "$SCRATCH/narrow-dialog.txt" | awk '{ if (length($0) > max) max = length($0) } END { print max + 0 }')
	if [ "$dialog_width" -gt 0 ] && [ "$dialog_width" -lt 60 ]; then
		pass "narrow: the dialog fits inside 60 columns (border row $dialog_width wide)"
	else
		fail "narrow: the dialog box is $dialog_width columns wide (0 = its top-right corner is off-screen)"
	fi
	send narrow Escape ; sleep 1

	kill_scene narrow
}

scenario_unicode() {
	echo "scenario: CJK / emoji / combining-mark task and description render, and the columns line up"
	local dir="$SCRATCH/unicode" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" unicode > /dev/null

	new_scene unicode 120 40 "$dir" continue --tui
	pick_first_session unicode || { fail "unicode: the session picker never appeared"; return; }
	wait_for_window unicode || { fail "unicode: window never rendered"; return; }
	sleep 2
	frame unicode "$SCRATCH/unicode-frame.txt"

	contains "unicode: the CJK task renders" "$SCRATCH/unicode-frame.txt" "計画レビュー"
	contains "unicode: the CJK description renders" "$SCRATCH/unicode-frame.txt" "タスクの説明"
	contains "unicode: the emoji task renders" "$SCRATCH/unicode-frame.txt" "🚀 計画"
	contains "unicode: the combining-mark description renders" "$SCRATCH/unicode-frame.txt" "café"
	no_exception "unicode: no exception" "$SCRATCH/unicode-frame.txt"

	# the ticket's claim, asserted in display columns: a wide row must place its separators in the
	# same columns as the ASCII control row - in the entry table *and* in the summary table
	check_alignment "unicode: every entry-table row puts its separators in the same columns" \
		"$SCRATCH/unicode-frame.txt" 2
	kill_scene unicode

	# same fixture at 60 columns, where the columns no longer fit and cells are truncated/clipped:
	# a wide glyph must not push the separators of its own row out of line there either
	new_scene unicodenarrow 60 24 "$dir" continue --tui
	pick_first_session unicodenarrow || { fail "unicode(60): the session picker never appeared"; return; }
	wait_for_window unicodenarrow || { fail "unicode(60): window never rendered"; return; }
	sleep 2
	frame unicodenarrow "$SCRATCH/unicode-narrow-frame.txt"
	check_alignment "unicode: entry and summary tables still line up at 60 columns" \
		"$SCRATCH/unicode-narrow-frame.txt" 2
	no_exception "unicode: no exception at 60 columns" "$SCRATCH/unicode-narrow-frame.txt"
	kill_scene unicodenarrow

	echo "  ---- observed unicode frame (for the PR, not an assertion) ----"
	grep -nE '計画|タスク|café|🚀' "$SCRATCH/unicode-frame.txt" | head -5 | sed 's/^/  | /'
}

# ---- main ------------------------------------------------------------------------------------

echo "repo:    $REPO_ROOT"
echo "dll:     $DLL"
echo "scratch: $SCRATCH (fixtures and frames live here; the repo's entries/ is untouched)"

if [ "$BUILD" -eq 1 ]; then
	echo "building..."
	dotnet build "$REPO_ROOT/TimeTrackerConsole.csproj" -v q --nologo || { echo "build failed"; exit 2; }
fi

if [ ! -f "$DLL" ]; then
	echo "missing $DLL - run without --no-build first" >&2
	exit 2
fi

# The frame checker is what the alignment assertions measure with, so its own width model and both
# directions of its verdict are proven before any scenario runs (it also fails if given no frame to
# compare, which is the way an alignment check could otherwise pass for the wrong reason).
echo "checking the frame checker..."
if python3 "$FRAME_CHECKER" selftest > "$SCRATCH/frame-selftest.txt" 2>&1; then
	pass "frame checker self-test ($(tail -1 "$SCRATCH/frame-selftest.txt" | sed 's/^selftest: //'))"
else
	fail "frame checker self-test - see $SCRATCH/frame-selftest.txt"
	sed 's/^/    /' "$SCRATCH/frame-selftest.txt"
fi

# --only runs a single scenario (fast iteration while tuning an assertion)
run_scenario() { # name
	case "$1" in
		long) scenario_long_task ;;
		selection) scenario_selection_survives_refresh ;;
		scale) scenario_scale ;;
		corrupt) scenario_corrupt ;;
		esc) scenario_esc ;;
		narrow) scenario_narrow ;;
		unicode) scenario_unicode ;;
		*) echo "unknown scenario: $1" >&2; exit 2 ;;
	esac
}

if [ -n "$ONLY" ]; then
	run_scenario "$ONLY"
else
	for name in long selection scale corrupt esc narrow unicode; do
		run_scenario "$name"
	done
fi

tmux -L "$SOCKET" kill-server 2>/dev/null

echo
echo "==== smoke summary ===="
for result in "${RESULTS[@]}"; do echo "$result"; done
echo "passed: $PASS   failed: $FAIL"

if [ "$KEEP" -eq 1 ]; then
	echo "scratch kept at $SCRATCH"
else
	echo "scratch: $SCRATCH (frames left in place for inspection)"
fi

[ "$FAIL" -eq 0 ]
