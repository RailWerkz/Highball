#!/usr/bin/env bash
# The junction scorecard for a set of routes: how each route's junctions were read (turnouts, crossovers, ladder links,
# diamonds...), how many were laid out to a standard design, and what's left to fix by hand, by reason. Run it before
# and after a change to the junction fitting (World/CrossoverFitting.cs, TurnoutFitting.cs, TrackLayouts.cs) to see
# what it did to each route.
#
#   Tools/junction-scorecard.sh <folder with Highball.exe> <route> [route...]
#
# The routes are folder names under that build's Content/Routes. Each is opened in the editor with --profilestats and
# closed again; the scorecard comes from the log.
set -euo pipefail

if [ $# -lt 2 ]; then
    echo "usage: $0 <folder with Highball.exe> <route> [route...]" >&2
    exit 1
fi

build="$1"
shift
log="$(mktemp)"
trap 'rm -f "$log"' EXIT

for route in "$@"; do
    (cd "$build" && ./Highball.exe --skipsplash --route "$route" --view --profilestats --quit 25 > "$log" 2>&1) || true
    echo "== $route"
    grep -E "Fitted to a standard turnout|Junctions:|  to fix:" "$log" | sed -E 's/^[0-9:]+ -- //' || echo "(no report: did the route open?)"
done
