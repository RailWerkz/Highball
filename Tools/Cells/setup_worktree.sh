#!/usr/bin/env bash
# Makes a git worktree runnable for the cell routes work (docs/cell-routes.md):
#   Tools/Cells/setup_worktree.sh BRANCH [BASE]
# creates .claude/worktrees/BRANCH-with-slashes-as-dashes from BASE (default cells/base) on a new branch BRANCH, then
# - hard-links in the content that isn't in git (audio, meshes, materials' textures, vegetation): read-only, costs nothing;
# - copies Content/Routes/World whole (a real copy: the test runs edit and save it, which must never touch the original).
set -euo pipefail
branch="$1"
base="${2:-cells/base}"
repo="$(git rev-parse --show-toplevel)"
tree="$repo/.claude/worktrees/${branch//\//-}"

if [ ! -d "$tree" ]; then
  git -C "$repo" worktree add -q -b "$branch" "$tree" "$base"
fi

cd "$repo"
{ git ls-files --others --exclude-standard Content; git ls-files --others --ignored --exclude-standard Content; } |
  grep -vE '^Content/(Routes|Packs)/' | sort -u |
  while IFS= read -r f; do
    [ -f "$f" ] || continue
    [ -e "$tree/$f" ] && continue
    mkdir -p "$tree/$(dirname "$f")"
    ln "$f" "$tree/$f"
  done

if [ ! -d "$tree/Content/Routes/World" ]; then
  mkdir -p "$tree/Content/Routes"
  cp -r "$repo/Content/Routes/World" "$tree/Content/Routes/World"
fi

echo "$tree"
