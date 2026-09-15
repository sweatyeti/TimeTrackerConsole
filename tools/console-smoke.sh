#!/usr/bin/env bash
#
# Behavioural smoke harness for the DEFAULT interface (Spectre.Console), driven through tmux.
#
# Why this exists: the review fixes touched shared `Session` logic (snapshot repair, what
# `StopCurrentEntry` stops, the unlogged-task-group projection) and `Program.cs` (skipped-file
# reporting). Logic shared with the console path has to be shown not to regress there, and the
# console flows are interactive prompts that need a real terminal - the same reason as tui-smoke.sh.
#
# It runs from a disposable scratch cwd (EntryStore writes entries/*.json relative to the process
# working directory) and never touches the repo's own entries/.
#
# Usage: tools/console-smoke.sh [--no-build] [--only <name>]
#   names: actions | new | mixedcase
set -uo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DLL="$REPO_ROOT/bin/Debug/net10.0/TimeTrackerConsole.dll"
FIXTURES="$REPO_ROOT/tools/tui-fixtures.py"
DLL="${TTC_SMOKE_DLL:-$DLL}"

BUILD=1
ONLY=""
while [ $# -gt 0 ]; do
	case "$1" in
		--no-build) BUILD=0 ; shift ;;
		--only) shift; ONLY="${1:-}" ; shift ;;
		*) echo "unknown argument: $1" >&2; exit 2 ;;
	esac
done

SOCKET="ttc-console-$$"
SCRATCH=$(mktemp -d "${TMPDIR:-/tmp}/ttc-console-XXXXXX")
PASS=0
FAIL=0
RESULTS=()

pass() { PASS=$((PASS + 1)); RESULTS+=("PASS  $1"); echo "  PASS  $1"; }
fail() { FAIL=$((FAIL + 1)); RESULTS+=("FAIL  $1"); echo "  FAIL  $1"; }

contains() { if grep -qF -- "$3" "$2"; then pass "$1"; else fail "$1 - expected to find: $3"; fi; }
absent() { if grep -qF -- "$3" "$2"; then fail "$1 - did not expect: $3"; else pass "$1"; fi; }

new_scene() { # name width height dir app-args...
	local name="$1" width="$2" height="$3" dir="$4"
	shift 4
	tmux -L "$SOCKET" new-session -d -x "$width" -y "$height" -s "$name" -c "$dir" \
		"TERM=xterm-256color dotnet $DLL $*; echo TTC-EXITED; exec bash"
}

send() { tmux -L "$SOCKET" send-keys -t "$1" "$2"; }
type_text() { tmux -L "$SOCKET" send-keys -t "$1" -l "$2"; }
frame() { tmux -L "$SOCKET" capture-pane -p -t "$1" > "$2"; }
pane_has() { tmux -L "$SOCKET" capture-pane -p -t "$1" 2>/dev/null | grep -qF -- "$2"; }

wait_for() { # scene needle seconds
	local i
	for i in $(seq 1 "$3"); do
		if pane_has "$1" "$2"; then return 0; fi
		sleep 1
	done
	return 1
}

no_exception() { # name file
	if grep -qE 'Unhandled exception|System\.[A-Za-z]*Exception' "$2"; then
		fail "$1 - frame contains an exception"
	else
		pass "$1"
	fi
}

kill_scene() { tmux -L "$SOCKET" kill-session -t "$1" 2>/dev/null; }

# ---- scenarios -------------------------------------------------------------------------------

# every admin option of the console main menu, in order, on one resumed session
scenario_actions() {
	echo "scenario: console admin flows (log a task group, deleted entries, stop tracking, stop and exit)"
	local dir="$SCRATCH/actions" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" basic corrupt-future > /dev/null

	new_scene actions 120 40 "$dir" continue
	wait_for actions "Select a session to resume" 40 || { fail "actions: the session picker never appeared"; return; }
	frame actions "$SCRATCH/actions-picker.txt"
	contains "actions: the readable session is listed" "$SCRATCH/actions-picker.txt" "smoke-basic"
	contains "actions: the unsupported file is reported with a reason" "$SCRATCH/actions-picker.txt" "schema version 99"
	send actions Enter
	wait_for actions "Select an option" 30 || { fail "actions: the main menu never appeared"; return; }
	sleep 1
	frame actions "$SCRATCH/actions-menu.txt"
	contains "actions: the summary renders" "$SCRATCH/actions-menu.txt" "Unlogged (hh:mm)"
	contains "actions: the in-progress entry is marked" "$SCRATCH/actions-menu.txt" "In Progress"

	# menu order is: stop/start, log a task group, view deleted, stop tracking, stop and exit
	send actions Down ; sleep 1 ; send actions Enter          # log a task group
	wait_for actions "Select a task group to log" 20 || { fail "actions: the task-group prompt never appeared"; return; }
	sleep 1
	frame actions "$SCRATCH/actions-groups.txt"
	contains "actions: a task group with its unlogged count is offered" "$SCRATCH/actions-groups.txt" "unlogged)"
	send actions Enter
	wait_for actions "Select an option" 30 || { fail "actions: the menu did not come back after logging"; return; }
	sleep 1
	frame actions "$SCRATCH/actions-logged.txt"
	absent "actions: nothing is left unlogged after logging the group" "$SCRATCH/actions-logged.txt" "unlogged)"
	contains "actions: the logged entry is marked Logged" "$SCRATCH/actions-logged.txt" "Logged"

	send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Enter   # view deleted
	wait_for actions "No deleted entries" 20 || { fail "actions: the empty deleted-entries message never appeared"; return; }
	send actions Enter ; sleep 2                             # "press any key"

	send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Enter  # stop tracking
	wait_for actions "Select an option" 30 || { fail "actions: the menu did not come back after stop tracking"; return; }
	sleep 1
	frame actions "$SCRATCH/actions-stopped.txt"
	absent "actions: the stopped entry is no longer in progress" "$SCRATCH/actions-stopped.txt" "In Progress"
	contains "actions: the stop/start option now offers a new entry" "$SCRATCH/actions-stopped.txt" "Start a new entry"

	send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Down ; sleep 1 ; send actions Enter  # stop and exit
	wait_for actions "TTC-EXITED" 30 || { fail "actions: the app did not exit on stop-and-exit"; return; }
	frame actions "$SCRATCH/actions-exit.txt"
	no_exception "actions: no exception on the console path" "$SCRATCH/actions-exit.txt"
	contains "actions: the final table is printed on exit" "$SCRATCH/actions-exit.txt" "End Time"
	kill_scene actions
}

scenario_new() {
	echo "scenario: console 'new' session (inline first-task prompt, entry row, session file)"
	local dir="$SCRATCH/new" ; mkdir -p "$dir/entries"

	new_scene new 120 40 "$dir" new --name console-smoke
	wait_for new "Entry started, enter a task if desired:" 40 || { fail "new: the task prompt never appeared"; return; }
	type_text new "console-task" ; sleep 1 ; send new Enter
	wait_for new "Select an option" 30 || { fail "new: the main menu never appeared"; return; }
	sleep 1
	frame new "$SCRATCH/new-menu.txt"
	contains "new: the typed task is on the entry row" "$SCRATCH/new-menu.txt" "console-task"
	contains "new: the session is active" "$SCRATCH/new-menu.txt" "In Progress"

	send new Down ; sleep 1 ; send new Down ; sleep 1 ; send new Down ; sleep 1 ; send new Down ; sleep 1 ; send new Enter
	wait_for new "TTC-EXITED" 30 || { fail "new: the app did not exit"; return; }
	no_exception "new: no exception" "$SCRATCH/new-exit.txt"
	frame new "$SCRATCH/new-exit.txt"

	if ls "$dir"/entries/*.json > /dev/null 2>&1; then
		pass "new: the session file was written under the scratch entries/ directory"
	else
		fail "new: no session file was written"
	fi
	kill_scene new
}

scenario_mixedcase() {
	echo "scenario: mixed-case task names are one task group on the console path"
	local dir="$SCRATCH/mixedcase" ; mkdir -p "$dir/entries"
	python3 "$FIXTURES" "$dir/entries" mixedcase > /dev/null

	new_scene mixed 120 40 "$dir" continue
	wait_for mixed "Select a session to resume" 40 || { fail "mixedcase: the session picker never appeared"; return; }
	send mixed Enter
	wait_for mixed "Select an option" 30 || { fail "mixedcase: the main menu never appeared"; return; }
	sleep 1

	send mixed Down ; sleep 1 ; send mixed Enter          # log a task group
	wait_for mixed "Select a task group to log" 20 || { fail "mixedcase: the task-group prompt never appeared"; return; }
	sleep 1
	frame mixed "$SCRATCH/mixedcase-groups.txt"
	# The invariant is "one group", not a particular spelling: Distinct keeps the first casing it
	# meets, so the label is "Weeding" here. Pre-fix this prompt listed both spellings.
	local group_lines
	group_lines=$(grep -c "unlogged)" "$SCRATCH/mixedcase-groups.txt")
	if [ "$group_lines" -eq 1 ]; then
		pass "mixedcase: exactly one task group is offered for both spellings"
	else
		fail "mixedcase: $group_lines task groups were offered (expected 1)"
	fi
	kill_scene mixed
}

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

case "$ONLY" in
	"") scenario_actions ; scenario_new ; scenario_mixedcase ;;
	actions) scenario_actions ;;
	new) scenario_new ;;
	mixedcase) scenario_mixedcase ;;
	*) echo "unknown scenario: $ONLY" >&2; exit 2 ;;
esac

tmux -L "$SOCKET" kill-server 2>/dev/null

echo
echo "==== console smoke summary ===="
for result in "${RESULTS[@]}"; do echo "$result"; done
echo "passed: $PASS   failed: $FAIL"
echo "scratch: $SCRATCH (frames left in place for inspection)"

[ "$FAIL" -eq 0 ]