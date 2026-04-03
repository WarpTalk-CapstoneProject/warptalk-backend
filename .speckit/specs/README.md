# WarpTalk Backend — Spec-Driven Development Guide

> **Repo**: `warptalk-backend`
> **Methodology**: [github/spec-kit](https://github.com/github/spec-kit) — Spec-Driven Development (SDD)
> **Constitution**: [memory/constitution.md](../memory/constitution.md) — read this first.

---

## What is SDD?

Instead of jumping straight into coding, we write a **specification first**, then a **technical plan**, then an **executable task list**. The AI (or any developer) then implements from the task list — not from vague memory.

This eliminates: "what was the requirement again?", drift from design, and untraceable decisions.

---

## Directory Structure

```
.speckit/
├── memory/
│   └── constitution.md          ← Immutable governing principles (READ FIRST)
└── specs/
    ├── README.md                 ← This file
    ├── templates/
    │   ├── spec.md               ← Copy this to start a new feature spec
    │   ├── plan.md               ← Copy this after spec is approved
    │   └── tasks.md              ← Copy this after plan is approved
    │
    ├── 001-{feature-name}/       ← One folder per feature
    │   ├── spec.md               ← Business requirements
    │   ├── plan.md               ← Technical implementation plan
    │   ├── tasks.md              ← Executable task list
    │   └── contracts/            ← gRPC proto snippets, REST schemas
    │
    └── 002-{another-feature}/
        └── ...
```

---

## Workflow: Step by Step

### Step 1 — Create a Spec (`spec.md`)

When to do this: **Before any code is written for a new feature.**

1. Determine the next spec number (look at existing folders in `specs/`)
2. Create folder: `specs/NNN-{feature-kebab-name}/`
3. Copy `templates/spec.md` → `specs/NNN-{feature}/spec.md`
4. Fill in: Problem Statement, User Stories, Acceptance Criteria
5. Mark `[NEEDS CLARIFICATION]` for every ambiguity — **do NOT guess**
6. Complete the Spec Completeness Checklist at the bottom
7. Get it reviewed and set status to `approved`
8. Create branch: `git checkout -b feat/NNN-{feature-name}`

> 💡 **Prompt to AI**: `"Create a spec for: {describe what you want to build}"`
> The AI will use `templates/spec.md` as the template and fill it in.

---

### Step 2 — Create a Plan (`plan.md`)

When to do this: **After spec is `approved`.**

1. Copy `templates/plan.md` → `specs/NNN-{feature}/plan.md`
2. Complete the Phase -1 Constitution Gates — all must pass
3. Describe data model changes (EF Core migrations)
4. Define gRPC contracts (copy to `contracts/` subfolder)
5. List REST endpoints with request/response shapes
6. Fill in Complexity Tracking if any Article was not fully satisfied

> 💡 **Prompt to AI**: `"Create a technical plan for the spec at specs/NNN-{feature}/spec.md"`

---

### Step 3 — Create Tasks (`tasks.md`)

When to do this: **After plan is `approved`.**

1. Copy `templates/tasks.md` → `specs/NNN-{feature}/tasks.md`
2. Read through the plan and fill in specific task items
3. Mark independent tasks with `[P]` (safe to work in parallel)
4. Share with team and assign tasks

> ⚠️ **Rule**: Phase 0 (writing tests) MUST be completed before Phase 1 (Domain code).
> This is required by **Constitution Article IV**.

> 💡 **Prompt to AI**: `"Generate tasks from specs/NNN-{feature}/plan.md"`

---

### Step 4 — Implement

Work through tasks in order. Mark `[/]` when starting, `[x]` when done.

> 💡 **Prompt to AI**: `"Implement task: [task description] following Constitution Article I-IX"`

---

## Naming Conventions

| Item | Convention | Example |
|---|---|---|
| Spec folder | `NNN-{kebab-name}` | `003-notification-low-credit` |
| Branch | `feat/NNN-{kebab-name}` | `feat/003-notification-low-credit` |
| Spec status | `draft` → `needs-clarification` → `approved` | — |
| Commit | Conventional Commits | `feat(notification): add low-credit email trigger` |

---

## Quick Reference — Constitution Articles

| Article | Rule (summary) |
|---|---|
| I | Clean Architecture — 4 layers, no cross-layer leakage |
| II | gRPC for sync .NET↔.NET; Redis Streams for async AI pipeline & events |
| III | .NET 10; PgBouncer port 6432; UUID v7; no hardcoded secrets; expand-and-contract migrations |
| IV | Tests written BEFORE implementation. Testcontainers for integration. |
| V | `feat/`, `hotfix/`, etc. prefix; Conventional Commits; spec required before PR |
| VI | `/api/v1/` prefix; Swagger mandatory; ProblemDetails errors |
| VII | ≤2s translation; ≤5s AI feedback; Voice Clone → EdgeTTS → Text-only fallback |
| VIII | 4 roles; JWT everywhere except login/register; 5-fail lockout |
| IX | Credit deduction via SubscriptionService only; formula = BaseRate × Languages × Hours |

---

## FAQ

**Q: Do I need a spec for a bug fix?**
A: No. Bug fixes use `hotfix/` branch. Only new features require a spec.

**Q: What if the spec changes mid-implementation?**
A: Update `spec.md`, re-approve, update `plan.md` and `tasks.md`. Do not implement against an unapproved spec change.

**Q: Can I skip the gRPC contract step?**
A: No. This is a Constitution Article IV gate. Contracts must exist before tests, and tests before implementation.

**Q: Who approves specs?**
A: Project Leader: Huỳnh Thái Tú (SE183307). Document approval by setting `Status: approved` in the spec file header.
