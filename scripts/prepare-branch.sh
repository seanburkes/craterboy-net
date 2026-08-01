#!/usr/bin/env bash
set -euo pipefail

description="${1:-}"
if [[ -z "$description" ]]; then
    echo "usage: $0 \"short branch description\"" >&2
    exit 2
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

if [[ -n "$(git status --porcelain)" ]]; then
    echo "refusing to branch from a dirty worktree" >&2
    exit 1
fi

base_branch="${BASE_BRANCH:-main}"
git fetch origin "$base_branch"

if ! git show-ref --verify --quiet "refs/heads/$base_branch"; then
    echo "local base branch '$base_branch' does not exist" >&2
    exit 1
fi

read -r ahead behind < <(git rev-list --left-right --count "$base_branch...origin/$base_branch")
if (( ahead != 0 )); then
    echo "refusing to move '$base_branch': it has $ahead unpublished commit(s)" >&2
    echo "preserve those commits on their feature branch before retrying" >&2
    exit 1
fi

git switch "$base_branch"
git pull --ff-only origin "$base_branch"

slug="$(printf '%s' "$description" | tr '[:upper:] ' '[:lower:]-' | tr -cs 'a-z0-9-' '-' | sed 's/^-*//; s/-*$//')"
if [[ -z "$slug" ]]; then
    echo "description must contain at least one letter or digit" >&2
    exit 2
fi

branch="agent/$slug"
if git show-ref --verify --quiet "refs/heads/$branch"; then
    echo "branch '$branch' already exists" >&2
    exit 1
fi

git switch -c "$branch"
echo "created $branch from origin/$base_branch"
