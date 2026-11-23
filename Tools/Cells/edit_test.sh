#!/usr/bin/env bash
# The scripted test edit for the cell routes work, run from a worktree's root (or the repo's):
#   Tools/Cells/edit_test.sh NAME [lat0 lon0 lat1 lon1]
# Builds Release, opens the World route in the builder, lays a branch (by default the one off the main line used to
# measure edits on 2026-11-22), quits, and puts the route's top-level files back as they were (the builder saves the
# network on quit). The log is written to Logs/NAME.log in the worktree, and its timing lines printed.
# Extra environment variables (HB_LOCALCHECK=1, HB_PROFILELAPS=1, ...) pass straight through.
set -euo pipefail
name="$1"; shift
lay=("${@:-42.3821627 -83.4673238 42.3832 -83.4660}")
root="$(git rev-parse --show-toplevel)"
route="$root/Content/Routes/World"
keep="$root/Logs/route-$name"
mkdir -p "$root/Logs"

dotnet build "$root/Highball" -c Release 2>&1 | grep -E " error |Build succeeded" | sort -u

rm -rf "$keep"; mkdir -p "$keep"
find "$route" -maxdepth 1 -type f -exec cp -p {} "$keep/" \;
for d in Network Cells; do [ -d "$route/$d" ] && cp -rp "$route/$d" "$keep/$d"; done

# shellcheck disable=SC2086
HB_LOGFILE="$root/Logs/$name.log" "$root/Highball/bin/Release/net10.0/Highball.exe" --route World --view --lay ${lay[*]} --quit 200 > /dev/null 2>&1 || true

for f in "$keep"/*; do [ -f "$f" ] && cp -p "$f" "$route/"; done
# (The folders saves write into put back whole: what the run added to them taken out too.)
for d in Network Cells; do [ -d "$keep/$d" ] && rm -rf "$route/$d" && cp -rp "$keep/$d" "$route/$d"; done
for f in "$keep"/*; do [ -f "$f" ] && { cmp -s "$f" "$route/$(basename "$f")" || echo "NOT RESTORED: $f"; }; done
# (Files the run made that weren't there before are taken out again.)
for f in "$route"/*; do [ -f "$f" ] && [ ! -e "$keep/$(basename "$f")" ] && rm -f "$f"; done

grep -E "Terrain open, ms|Track rebuild|XX|Local check|Cells" "$root/Logs/$name.log" | cut -c1-600 || true
