#!/usr/bin/env bash
# The cells after an edit, run from a worktree's root: Tools/Cells/save_test.sh NAME [lat0 lon0 lat1 lon1]
# Opens World, lays a branch (as edit_test.sh), saves (--save: what Ctrl+S does, so the cells the edit changed are written),
# quits; then opens it again with HB_CELLCHECK=1 (the cells read against the lines worked out afresh), and puts the route's
# top-level files, Network/ and Cells/ back as they were. Logs: Logs/NAME.log (the edit), Logs/NAME-reopen.log.
# Doesn't build: run edit_test.sh (or dotnet build) first.
set -euo pipefail
name="$1"; shift
lay=("${@:-42.3821627 -83.4673238 42.3832 -83.4660}")
root="$(git rev-parse --show-toplevel)"
route="$root/Content/Routes/World"
keep="$root/Logs/route-$name"
exe="$root/Highball/bin/Release/net10.0/Highball.exe"
mkdir -p "$root/Logs"

rm -rf "$keep"; mkdir -p "$keep"
find "$route" -maxdepth 1 -type f -exec cp -p {} "$keep/" \;
for d in Network Cells; do if [ -d "$route/$d" ]; then cp -rp "$route/$d" "$keep/$d"; fi; done

# shellcheck disable=SC2086
HB_LOGFILE="$root/Logs/$name.log" "$exe" --route World --view --lay ${lay[*]} --save --quit 240 > /dev/null 2>&1 || true
HB_CELLCHECK=1 HB_LOGFILE="$root/Logs/$name-reopen.log" "$exe" --route World --view --quit 200 > /dev/null 2>&1 || true

for f in "$keep"/*; do if [ -f "$f" ]; then cp -p "$f" "$route/"; fi; done
for d in Network Cells; do rm -rf "$route/$d"; if [ -d "$keep/$d" ]; then cp -rp "$keep/$d" "$route/$d"; fi; done
for f in "$route"/*; do if [ -f "$f" ] && [ ! -e "$keep/$(basename "$f")" ]; then rm -f "$f"; fi; done

grep -E "Track rebuild|Cells merged|Cells: |Cell check|Local check|XX" "$root/Logs/$name.log" | cut -c1-600 || true
echo "--- reopened"
grep -E "Terrain open, ms|Cells: |Cell check|XX" "$root/Logs/$name-reopen.log" | cut -c1-600 || true
