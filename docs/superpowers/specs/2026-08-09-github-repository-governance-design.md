# ApexLab GitHub Repository Governance Design

**Date:** 2026-08-09

**Status:** Approved through the owner's instruction to choose and implement the strongest structure

**Repository:** `DarianGanev/apexlab-desktop`

## Purpose

ApexLab needs one durable system for release planning, issue decomposition, pull-request metadata,
and diploma evidence. The system must preserve GitFlow, stay useful for a solo developer, avoid
speculative ticket noise, and make the path from v0.3.0 to v1.0.0 visible to evaluators.

Repository documentation, GitHub Issues, milestones, labels, and one dedicated GitHub Project form
the source of truth. This design does not depend on conversation memory.

## Current-state findings

- `develop` is the GitHub default branch and the integration branch; `main` contains releases.
- GitHub allows merge commits and disables squash/rebase merges, matching the documented GitFlow.
- `delete_branch_on_merge` is disabled.
- Remote branches `codex/prepare-v0.2-release` and `release/0.1.0` have zero unique commits and are
  ancestors of both `main` and `develop`.
- All 22 pull requests are merged. No issues or milestones exist.
- Only GitHub's default labels exist.
- Personal Project 1 belongs to the unrelated Merry-popins repository and must remain untouched.

## Repository identity

The About description will be:

> Local-first F1 25 telemetry workbench for evidence-linked lap analysis and measured sim-racing
> coaching experiments.

Topics will be:

- `f1-25`
- `telemetry`
- `sim-racing`
- `wpf`
- `dotnet`
- `desktop-app`
- `data-analysis`
- `deterministic-replay`
- `local-first`
- `diploma-project`

No homepage URL is claimed until ApexLab has a real project or documentation site.

## Branch and merge policy

- Keep `develop` as the default branch. Feature, fix, documentation, and infrastructure pull
  requests target `develop`.
- Keep `main` release-only. Only `release/*` pull requests target `main`.
- Preserve merge commits; do not enable squash or rebase merges.
- Enable automatic deletion of merged pull-request branches.
- Delete the two verified stale remote branches listed above.
- Short branches use `feature/*`, `fix/*`, `release/*`, or `codex/*` when Codex owns the task.
- Tags remain annotated semantic versions and are created only after exact-main verification.

## Label taxonomy

Existing labels are renamed where doing so preserves historical associations. Unused default labels
that do not serve this private solo project are removed.

### Type labels

Exactly one type label is required on every issue and pull request.

| Label | Color | Meaning |
| --- | --- | --- |
| `type:bug` | `D73A4A` | Incorrect behavior or regression |
| `type:feature` | `1D76DB` | New product capability |
| `type:docs` | `0075CA` | Documentation or evidence only |
| `type:test` | `5319E7` | Test or verification infrastructure |
| `type:refactor` | `FBCA04` | Behavior-preserving structural change |
| `type:release` | `0E8A16` | Release preparation or publication |
| `type:chore` | `C5DEF5` | Repository or dependency maintenance |

### Area labels

At least one area label is required. Multiple areas are allowed when the boundary is genuine.

| Label | Color | Meaning |
| --- | --- | --- |
| `area:desktop` | `C2E0C6` | WPF shell, lifecycle, accessibility, or interaction |
| `area:protocol` | `BFD4F2` | F1 packet formats and decoding |
| `area:telemetry` | `BFDADC` | Live sources, replay sources, and canonical streams |
| `area:capture` | `1D76DB` | Capture workflow, counters, and evidence ingestion |
| `area:persistence` | `D4C5F9` | SQLite, evidence files, migrations, and recovery |
| `area:analysis` | `F9D0C4` | Laps, alignment, references, deltas, and findings |
| `area:coaching` | `FEF2C0` | Detectors, experiments, adherence, and outcomes |
| `area:validation` | `006B75` | Tests, private gates, and reproducibility evidence |
| `area:infrastructure` | `B60205` | CI, packaging, repository, and release tooling |

### Priority and workflow labels

Priority is required on open implementation issues, not on historical pull requests.

| Label | Color | Meaning |
| --- | --- | --- |
| `priority:P0` | `B60205` | Release blocker, data loss, or privacy/security failure |
| `priority:P1` | `D93F0B` | Required for the active milestone |
| `priority:P2` | `FBCA04` | Valuable but not release-blocking yet |
| `priority:P3` | `C5DEF5` | Explicitly deferred |
| `blocked` | `000000` | Cannot progress until the named dependency changes |
| `needs:evidence` | `7057FF` | Implementation exists but its required gate is incomplete |

Unused defaults `duplicate`, `good first issue`, `help wanted`, `invalid`, `question`, and `wontfix`
will be removed. They can be recreated later if collaboration needs change.

## Milestones and target dates

Milestones retain the roadmap's week boundaries, with week 5 beginning on 2026-08-10.

| Milestone | Due date | Scope |
| --- | --- | --- |
| `v0.3.0` | 2026-09-13 | Provisional Bahrain coaching vertical slice |
| `v0.4.0` | 2026-10-04 | Robust recorder and required protocol breadth |
| `v0.5.0` | 2026-10-25 | Trustworthy spatial comparison |
| `v0.6.0` | 2026-11-15 | Analysis workbench and transferable corner model |
| `v0.7.0` | 2026-12-06 | Generalized explainable coach |
| `v0.8.0` | 2026-12-20 | Experiment-engine hardening and evaluation freeze |
| `v0.9.0` | 2027-01-17 | Evaluation, product, and security hardening |
| `v1.0.0` | 2027-01-24 | Diploma-ready release and evidence package |

Dates are planning targets, not claims that quality gates may be skipped.

## Issue decomposition

The hybrid strategy prevents premature over-specification.

### Detailed v0.3.0 issues

Create one release epic and these issue-sized deliverables:

1. Lock the v0.3 Bahrain slice contract and frozen evidence inputs.
2. Decode the minimal Session, Lap Data, Motion, Car Telemetry, and Event fields.
3. Replay v0.2 evidence into a deterministic minimal canonical cache.
4. Represent manually audited comparable Bahrain laps and context exclusions.
5. Implement one-metre alignment, a robust personal reference, and cumulative delta.
6. Store one reviewed Bahrain corner and driver-visible landmarks.
7. Detect the evidence-linked braking point with confidence and abstention.
8. Implement immutable baseline/intervention/no-cue experiment state and outcomes.
9. Build the evidence-linked Review foreground flow.
10. Build the one-experiment Coach foreground flow.
11. Prove the complete private Bahrain gate and publish v0.3.0.

Each issue body includes: problem, in-scope behavior, exclusions, acceptance criteria, evidence gate,
dependencies, privacy boundary, and links to the parent release epic and roadmap.

### Later releases

Create one epic issue for each milestone from v0.4.0 through v1.0.0. Each epic transcribes the
roadmap's Build and Gate sections without creating child implementation issues prematurely. Child
issues are created only when that milestone enters design.

## GitHub Project design

Create a new private user-owned Project named **ApexLab Diploma Roadmap**. Personal Project 1 is
unrelated and remains untouched.

Project description:

> Engineering backlog, Kanban execution board, and release roadmap for the ApexLab diploma project.

Project fields:

| Field | Type | Values or use |
| --- | --- | --- |
| `Status` | built-in single select | Todo, In Progress, Done |
| `Priority` | single select | P0, P1, P2, P3 |
| `Effort` | number | Relative engineering points; 1, 2, 3, 5, or 8 |
| `Release` | single select | v0.3.0 through v1.0.0 |
| `Start date` | date | Planned start for roadmap positioning |
| `Target date` | date | Planned completion for roadmap positioning |

Project views:

- **Backlog**: table, open issues, grouped or sorted by Priority.
- **Kanban**: board, open issues and pull requests, column field Status.
- **Roadmap**: roadmap, open release epics and implementation issues positioned by Start date and
  Target date.

All new ApexLab issues are added. Historical pull requests remain outside the active Project to
avoid a Done-column archive that obscures current work. Future active pull requests are added when
opened and marked Done after merge.

## Historical pull-request classification

Pull requests 1-9 receive milestone `v0.1.0`; pull requests 10-22 receive milestone `v0.2.0`.
Milestones for released versions are created closed after classification.

| Pull requests | Type | Primary area |
| --- | --- | --- |
| 1 | `type:feature` | `area:infrastructure` |
| 2 | `type:feature` | `area:infrastructure` |
| 3 | `type:feature` | `area:persistence` |
| 4-5 | `type:feature` | `area:desktop` |
| 6 | `type:feature` | `area:persistence` |
| 7 | `type:bug` | `area:desktop`, `area:infrastructure` |
| 8 | `type:feature` | `area:infrastructure` |
| 9 | `type:release` | `area:infrastructure` |
| 10 | `type:docs` | `area:capture` |
| 11 | `type:feature` | `area:protocol` |
| 12 | `type:bug` | `area:desktop` |
| 13 | `type:feature` | `area:capture` |
| 14 | `type:feature` | `area:telemetry` |
| 15 | `type:feature` | `area:protocol` |
| 16 | `type:feature` | `area:telemetry`, `area:validation` |
| 17 | `type:feature` | `area:persistence`, `area:capture` |
| 18 | `type:feature` | `area:desktop`, `area:capture` |
| 19 | `type:test` | `area:capture`, `area:validation` |
| 20 | `type:feature` | `area:validation` |
| 21 | `type:docs` | `area:validation` |
| 22 | `type:release` | `area:infrastructure` |

## Durable issue and pull-request conventions

Add repository issue forms for feature work and bugs. Add a pull-request template requiring:

- linked issue or explicit release-maintenance reason;
- target milestone;
- type and area labels;
- concise scope and non-scope;
- commands and evidence proving the change;
- privacy/data handling statement;
- risks, limitations, and rollback;
- confirmation that generated artifacts and private telemetry are not committed.

Add `docs/repository-governance.md` as the concise operational policy. It records branch targeting,
label requirements, Project use, issue quality, evidence gates, and release cleanup. This document is
the persistent memory for future work.

## Automation boundary

Enable GitHub's automatic merged-branch deletion. Do not add a GitHub Actions workflow that needs a
long-lived personal Project token secret. Project membership and fields are applied explicitly by the
same workflow that creates an issue or pull request. This keeps the zero-cost private repository free
of an unnecessary high-scope secret.

## Verification and rollback

Verification must prove:

- only `main` and `develop` remain as long-lived remote branches;
- `develop` remains default and automatic merged-branch deletion is enabled;
- About metadata matches this design;
- labels and milestones match the declared set;
- every historical pull request has one type, at least one area, and the correct release milestone;
- all planned issues exist once, have acceptance criteria, and belong to the correct milestone;
- the ApexLab Project contains the planned issues, fields, and three named views;
- the unrelated Project 1 is unchanged;
- the repository worktree is clean and documentation verification passes.

Metadata mistakes are reversible through the GitHub API. Deleted stale branches can be recreated
from their recorded commits, which remain ancestors of the annotated release tags and both long-lived
branches. Issues are closed rather than deleted if a correction is required.
