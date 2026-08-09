# GitHub Repository Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn ApexLab's roadmap into a durable GitHub issue, milestone, Project, and pull-request
workflow while preserving the repository's verified GitFlow history.

**Architecture:** Repository-owned Markdown and YAML define the operating policy and intake quality.
GitHub repository metadata, labels, milestones, issues, and one user-owned Project provide the live
planning system. External mutations are applied idempotently where possible and audited against an
exact catalog after creation.

**Tech Stack:** Git, GitHub CLI, GitHub REST API version `2026-03-10`, GitHub Projects v2, Markdown,
GitHub issue forms, YAML, PowerShell.

## Global Constraints

- Keep `develop` as the default integration branch and `main` as the release-only branch.
- Preserve merge commits; do not enable squash or rebase merges.
- Do not modify or delete personal Project 1, which belongs to the Merry-popins repository.
- Do not commit credentials, one-time device codes, private telemetry, capture identifiers, or exact
  private rates.
- Create detailed implementation issues only for v0.3.0; later releases receive one epic each until
  they enter design.
- Every issue and pull request has exactly one `type:*` label and at least one `area:*` label.
- GitHub Project automation must not require a stored personal access token secret.
- All local repository changes use the owner's configured Git identity.

---

### Task 1: Persist the repository operating policy

**Files:**
- Create: `docs/repository-governance.md`
- Create: `.github/pull_request_template.md`
- Create: `.github/ISSUE_TEMPLATE/feature.yml`
- Create: `.github/ISSUE_TEMPLATE/bug.yml`
- Create: `.github/ISSUE_TEMPLATE/config.yml`
- Modify: `docs/superpowers/specs/2026-08-09-github-repository-governance-design.md`

**Interfaces:**
- Consumes: the approved governance design and `docs/apexlab-roadmap.md`.
- Produces: the durable rules future issue and pull-request creation must follow.

- [ ] **Step 1: Write the concise operational policy**

  Record branch targets, short-lived branch naming, required labels, milestone assignment, Project
  status use, evidence requirements, privacy exclusions, release procedure, and merged-branch
  cleanup in `docs/repository-governance.md`.

- [ ] **Step 2: Add the pull-request template**

  Require linked issue/release reason, milestone, type and area labels, scope/non-scope, verification
  evidence, privacy statement, risks/rollback, and a private-data/generated-artifact check.

- [ ] **Step 3: Add feature and bug issue forms**

  The feature form requires problem, scope, non-scope, acceptance criteria, evidence gate,
  dependencies, and privacy impact. The bug form requires reproduction, expected/actual behavior,
  impact, environment, sanitized evidence, and regression acceptance criteria.

- [ ] **Step 4: Configure the issue chooser**

  Disable blank issues and provide a security contact link to the repository's private Security
  advisory page rather than inviting public disclosure.

- [ ] **Step 5: Verify repository files**

  Run:

  ```powershell
  git diff --check
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
  ```

  Expected: no whitespace errors; 449 tests pass with zero build warnings/errors.

- [ ] **Step 6: Commit the durable policy**

  ```powershell
  git add -- docs/repository-governance.md .github/ISSUE_TEMPLATE `
    .github/pull_request_template.md `
    docs/superpowers/specs/2026-08-09-github-repository-governance-design.md
  git commit -m "docs: establish repository governance"
  ```

### Task 2: Configure repository identity and branch hygiene

**Files:**
- Modify externally: GitHub repository About metadata and merge settings.
- Delete externally: verified remote branches `codex/prepare-v0.2-release` and `release/0.1.0`.

**Interfaces:**
- Consumes: exact metadata and branch policy from the design.
- Produces: a discoverable repository with automated merged-branch cleanup.

- [ ] **Step 1: Reprove stale-branch containment**

  For each branch, run `git merge-base --is-ancestor` against `origin/main` and `origin/develop`, and
  require `git rev-list --count origin/develop..<branch>` to equal zero.

- [ ] **Step 2: Set About metadata**

  Use `gh repo edit` to set the approved description and ten topics. Leave the homepage unset.

- [ ] **Step 3: Enable merged-branch deletion**

  Patch the repository with `delete_branch_on_merge=true`; retain default branch `develop`, merge
  commits enabled, and squash/rebase disabled.

- [ ] **Step 4: Delete only the two verified stale branches**

  Delete the named remote refs and fetch with pruning. Do not delete `main` or `develop`.

- [ ] **Step 5: Audit repository settings**

  Require: default `develop`, `delete_branch_on_merge=true`, expected description/topics, merge
  commits enabled, and exactly the two long-lived remote branches.

### Task 3: Create labels and release milestones

**Files:**
- Modify externally: GitHub labels and milestones.

**Interfaces:**
- Consumes: exact label names/colors/descriptions and release dates from the design.
- Produces: stable metadata referenced by issues, pull requests, and Project views.

- [ ] **Step 1: Rename association-bearing default labels**

  Rename `bug` to `type:bug`, `documentation` to `type:docs`, and `enhancement` to
  `type:feature`, updating colors and descriptions in place.

- [ ] **Step 2: Create the remaining declared labels**

  Create all remaining type, area, priority, `blocked`, and `needs:evidence` labels with exact
  colors/descriptions. Treat an identical existing label as success.

- [ ] **Step 3: Remove unused default labels**

  Confirm each is unused, then delete `duplicate`, `good first issue`, `help wanted`, `invalid`,
  `question`, and `wontfix`.

- [ ] **Step 4: Create historical release milestones**

  Create `v0.1.0` and `v0.2.0` milestones, then close them after historical PR assignment.

- [ ] **Step 5: Create planned milestones**

  Create v0.3.0 through v1.0.0 with the exact due dates and release scope in the design.

- [ ] **Step 6: Verify taxonomy and dates**

  Compare `gh label list --json` and the milestones REST response to the design catalog; reject
  missing, duplicate, or misspelled entries.

### Task 4: Classify historical pull requests

**Files:**
- Modify externally: pull requests 1-22.

**Interfaces:**
- Consumes: labels and milestones from Task 3 and the PR mapping in the design.
- Produces: a navigable v0.1/v0.2 engineering history.

- [ ] **Step 1: Assign v0.1.0 history**

  Assign pull requests 1-9 to milestone `v0.1.0` and apply the exact type/area labels in the mapping.

- [ ] **Step 2: Assign v0.2.0 history**

  Assign pull requests 10-22 to milestone `v0.2.0` and apply the exact type/area labels in the
  mapping.

- [ ] **Step 3: Remove obsolete broad labels**

  Ensure no PR retains a broad pre-taxonomy label after the rename/mapping operation.

- [ ] **Step 4: Audit every PR**

  Query all 22 PRs and require exactly one `type:*`, at least one `area:*`, and the correct release
  milestone. Historical PRs receive no priority label and are not added to the active Project.

### Task 5: Create the roadmap issue hierarchy

**Files:**
- Modify externally: GitHub Issues.

**Interfaces:**
- Consumes: roadmap, issue forms, milestones, and labels.
- Produces: 12 detailed v0.3 issues and seven later-release epics.

- [ ] **Step 1: Create the v0.3.0 release epic**

  Title it `[v0.3.0] Deliver the provisional Bahrain coaching vertical slice`. Include the roadmap
  gate, explicit non-claims, privacy boundary, and a checklist populated with links after children
  are created. Assign `type:feature`, `area:analysis`, `area:coaching`, `priority:P1`, and milestone
  `v0.3.0`.

- [ ] **Step 2: Create the eleven v0.3 deliverable issues**

  Use the exact issue list in the design. Each body contains problem, scope, non-scope, measurable
  acceptance criteria, evidence gate, dependencies, privacy boundary, and parent-epic link. Apply
  one type, relevant areas, `priority:P1`, and milestone `v0.3.0`.

- [ ] **Step 3: Link the v0.3 hierarchy**

  Replace the parent epic checklist with the eleven created issue links and add dependency links
  where one deliverable consumes another.

- [ ] **Step 4: Create later-release epics**

  Create one issue for each v0.4.0-v1.0.0 milestone. Copy that release's Build and Gate commitments
  from the roadmap, state that child issue decomposition waits for design, and apply
  `type:feature`, relevant areas, `priority:P2` (v0.4) or `priority:P3` (v0.5-v1.0), plus the matching
  milestone.

- [ ] **Step 5: Audit issue quality**

  Require 19 open issues total, unique titles, correct milestones/labels, no private evidence, and
  eleven linked children in the v0.3 epic.

### Task 6: Create and populate the ApexLab GitHub Project

**Files:**
- Modify externally: new user Project `ApexLab Diploma Roadmap`.

**Interfaces:**
- Consumes: 19 issues from Task 5 and the authenticated `project` scope.
- Produces: one Backlog table, Kanban board, and dated Roadmap view.

- [ ] **Step 1: Preserve unrelated projects**

  Record Project 1 ID/title/item count before creation and never mutate it.

- [ ] **Step 2: Create the ApexLab Project**

  Create a private user-owned project with the exact name, short description, and README from the
  design. Record its number and node ID.

- [ ] **Step 3: Create custom fields**

  Create Priority, Effort, Release, Start date, and Target date with exact types/options. Reuse the
  built-in Status field.

- [ ] **Step 4: Create named views**

  Use REST API version `2026-03-10` to create Backlog (`table`), Kanban (`board`), and Roadmap
  (`roadmap`) views for the user-owned Project.

- [ ] **Step 5: Add issues and set fields**

  Add all 19 issues. Set Release and Priority from labels, Effort for v0.3 deliverables, dates from
  milestone ranges, and Todo status. Later epics span their complete milestone windows.

- [ ] **Step 6: Link the Project to ApexLab**

  Enable repository Projects and link the new Project when the API supports the relationship. If
  linking is UI-only, retain the Project URL in `docs/repository-governance.md` and report that exact
  bounded limitation.

- [ ] **Step 7: Audit Project contents**

  Require the exact Project title, 19 ApexLab items, declared custom fields, three named views, and
  unchanged Project 1 metadata/item count.

### Task 7: Publish and verify the governance change

**Files:**
- Modify: `docs/repository-governance.md` with final Project URL if not known earlier.
- Modify: this plan's checkboxes as execution evidence when appropriate.

**Interfaces:**
- Consumes: all prior local and GitHub changes.
- Produces: a reviewed governance commit on `develop` and a clean v0.3 starting point.

- [ ] **Step 1: Run final local verification**

  Run `git diff --check` and `scripts/Verify.ps1`; require 449 tests, zero failures, and zero build
  warnings/errors.

- [ ] **Step 2: Commit final evidence**

  Commit any final Project URL or audit corrections under the owner's Git identity.

- [ ] **Step 3: Push and open a PR to develop**

  Push `codex/repository-governance`, open a ready PR targeting `develop`, apply `type:docs`,
  `area:infrastructure`, and milestone `v0.3.0`, then add it to the new Project as In Progress.

- [ ] **Step 4: Wait for exact-head CI and merge**

  Require clean mergeability and successful exact-head CI. Merge with a merge commit, verify the
  exact `develop` merge CI, and mark the Project item Done.

- [ ] **Step 5: Clean completed branch state**

  Confirm automatic remote branch deletion, remove the clean governance worktree/local branch, and
  leave the root `develop` worktree clean and synchronized.

- [ ] **Step 6: Produce the final audit**

  Report repository About URL, Project URL, issue/milestone/label counts, PR classification result,
  default/long-lived branches, exact CI links, and any API limitation that required a UI follow-up.
