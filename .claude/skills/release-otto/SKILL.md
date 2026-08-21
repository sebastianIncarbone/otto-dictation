---
name: release-otto
description: "Trigger: release Otto, cut a version, tag a release, publish Otto, sacar una version, desplegar Otto. Bump, PR, tag, and hand an Otto release to CI with the guardrails the repo doesn't enforce on its own."
license: Apache-2.0
metadata:
  author: "sebastianIncarbone"
  version: "1.0"
---

## Activation Contract

Load when asked to release, cut, tag, publish, or deploy a new Otto version, or to check
what a release needs before it can ship.

## Hard Rules

- Never copy the `git tag v0.2.0` example from CLAUDE.md/README literally — `v0.2.0` is a
  real historical tag and the command will fail.
- Never tag before `<Version>` in `Directory.Build.props` is bumped **and merged to
  `main`**. A local build after tagging without this reports the stale version.
- `main` is branch-protected, enforced for admins too: 0 required approvals, but the
  `build` check is required and direct pushes are rejected server-side. The version-bump
  commit CANNOT be pushed straight to `main` — it goes through a branch + PR + merge like
  any other change, including the PR body style in the `otto-convenciones-de-pr` memory
  (no issue link, `## What this is` + `## Decisions worth a reviewer's attention`).
- Never tag a dirty working tree, and never tag a version that already exists locally or
  on `origin` — no `-f`.
- The tag must point at the merge commit that landed on `main`, never at an unmerged
  branch commit.

## Decision Gates

| Situation | Action |
|---|---|
| Working tree dirty | Stop; ask the user to commit or stash first |
| Next version unclear | Read the latest real tag and `Directory.Build.props`, propose a semver bump, confirm the exact number with the user before touching anything |
| Tag already exists (local or remote) | Stop; never force-overwrite it |
| User just wants to sanity-check packaging | Run `build/publicar.ps1` locally (needs Inno Setup) — this verifies the script, it is not how a real release ships |

## Execution Steps

1. `git status` — refuse to continue if the tree is dirty.
2. Determine the next version from the latest real tag vs `Directory.Build.props`;
   confirm the number with the user.
3. Branch, edit `<Version>` in `Directory.Build.props`, commit as
   `chore: release X.Y.Z` (standardized message — historical commits used two different
   phrasings, don't copy either literally).
4. `git push -u origin <branch>`, then `gh pr create` in the repo's own PR style.
5. Wait for the `build` check to go green, `gh pr merge --merge` (merge commit, never
   squash), delete the local and remote branch.
6. `git checkout main && git pull`; confirm the new tip is the bump commit.
7. `git tag vX.Y.Z && git push origin vX.Y.Z`.
8. CI takes it from there: the `build` job runs `publicar.ps1 -Version X.Y.Z` and uploads
   the installer + zip; the `release` job (gated on the `refs/tags/v*` ref) publishes the
   GitHub Release with SHA256 hashes and VirusTotal links. Point the user at the Actions
   run rather than hand-building the installer for a real release.

## Output Contract

Report the version tagged, the PR URL, the Actions run URL, and explicit confirmation
that the pushed tag points at a commit that is actually on `main`.

## References

- `docs/distribucion-y-primer-arranque.md` — packaging checklist for "can someone else
  install this"; a separate concern from cutting a version, link it rather than repeat it.
- `build/publicar.ps1` — the packaging script both CI and local verification call.
- `.github/workflows/build.yml` — the build/test/package/release CI pipeline.
