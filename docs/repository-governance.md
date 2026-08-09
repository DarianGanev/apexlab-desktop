# Repository governance

This document is the operating policy for ApexLab development. The detailed product schedule remains
in [`apexlab-roadmap.md`](apexlab-roadmap.md).

## Branches and integration

- `develop` is the default integration branch. Feature, fix, test, documentation, and infrastructure
  pull requests target `develop`.
- `main` is release-only. Only verified `release/*` pull requests target `main`.
- Work uses short-lived `feature/*`, `fix/*`, `release/*`, or `codex/*` branches in isolated
  worktrees. Merged branches are deleted automatically.
- Pull requests use merge commits. Squash and rebase merges remain disabled so reviewed branch and
  release ancestry stays visible.
- Every release is verified on its exact `main` merge commit, receives an annotated semantic-version
  tag, publishes tag-built assets, and is merged back into `develop`.

## Issues

An implementation issue must state:

- the problem and user-visible or engineering outcome;
- in-scope and explicitly excluded behavior;
- measurable acceptance criteria;
- required automated, synthetic, or private evidence;
- dependencies and ordering constraints;
- privacy and local-data impact.

The active release receives issue-sized deliverables. Later releases remain single epics until their
design work begins, avoiding speculative decomposition.

## Labels and milestones

Every issue and pull request has exactly one `type:*` label and at least one `area:*` label. Open
implementation issues also carry one `priority:*` label. `blocked` names a real external dependency;
`needs:evidence` means implementation exists but its declared gate has not passed.

Every issue and pull request belongs to the release milestone that consumes it. A milestone date is
a planning target, never permission to weaken a quality gate.

## GitHub Project

The **ApexLab Diploma Roadmap** Project is the live planning surface:

- **Backlog** exposes open issues and priority.
- **Kanban** tracks active issues and pull requests through Status.
- **Roadmap** shows release epics and deliverables using Start date and Target date.

Historical merged pull requests stay out of the active Project. A new issue is added as Todo. Its
implementation pull request is added as In Progress and marked Done after merge. No stored personal
Project token is permitted in repository secrets; Project updates are performed explicitly by the
workflow creating or merging the item.

## Pull requests

Each pull request records a linked issue or explicit release-maintenance reason, milestone, labels,
scope, non-scope, verification commands/results, privacy impact, risks, limitations, and rollback.
The exact head SHA must pass CI before merge. Generated output, raw telemetry, private rates,
capture identifiers, and machine-local paths remain outside Git.

## Evidence and safety

Claims must be reproducible from committed tests, sanitized fixtures, tagged artifacts, or the
privacy-safe conclusion of a documented private gate. A passing build is not evidence for behavioral
correctness, and a private test never authorizes committing private source data.

Security and privacy vulnerabilities are reported through a private repository security advisory,
not a public issue.
