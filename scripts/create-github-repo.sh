#!/usr/bin/env bash
set -euo pipefail

REPOSITORY_NAME="${1:-StaffMessenger}"
VISIBILITY="${2:-private}"

if ! command -v gh >/dev/null 2>&1; then
    echo "GitHub CLI is required: https://cli.github.com/" >&2
    exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
    echo "Authenticate first with: gh auth login" >&2
    exit 1
fi

if [[ ! -d .git ]]; then
    git init --initial-branch=main
fi

if ! git config user.name >/dev/null; then
    git config user.name "$(gh api user --jq '.name // .login')"
fi

if ! git config user.email >/dev/null; then
    GITHUB_LOGIN="$(gh api user --jq .login)"
    git config user.email "$GITHUB_LOGIN@users.noreply.github.com"
fi

git add .
if ! git diff --cached --quiet; then
    git commit -m "Initial StaffMessenger CI/CD setup"
fi

gh repo create "$REPOSITORY_NAME" \
    "--$VISIBILITY" \
    --source=. \
    --remote=origin \
    --push

echo "Repository created. Publish the first release with:"
echo "  git tag v0.1.0"
echo "  git push origin v0.1.0"
