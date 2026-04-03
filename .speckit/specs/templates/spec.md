# Feature Specification: {FEATURE_TITLE}

> **Spec ID**: NNN
> **Branch**: `feat/NNN-{feature-kebab-name}`
> **Author**: {Name} ({StudentCode})
> **Created**: {YYYY-MM-DD}
> **Status**: `draft` | `needs-clarification` | `approved`

---

## 1. Problem Statement

> Describe the problem or user need this feature addresses. Focus on WHY this is needed.
> ✅ Focus on WHAT users need and WHY.
> ❌ Avoid HOW to implement (no tech stack, APIs, code structure).

{Describe the problem here}

---

## 2. User Stories

> Format: "As a [role], I want to [action], so that [benefit]."
> Use roles defined in the Constitution: system_admin, workspace_admin, host, participant.

- As a **{role}**, I want to {action}, so that {benefit}.
- As a **{role}**, I want to {action}, so that {benefit}.

---

## 3. Acceptance Criteria

> Each criterion must be **testable and unambiguous**. Use "Given/When/Then" format.

**Criterion 1: {Short title}**
- Given: {precondition}
- When: {action}
- Then: {expected outcome}

**Criterion 2: {Short title}**
- Given: {precondition}
- When: {action}
- Then: {expected outcome}

---

## 4. Out of Scope

> Explicitly list what this feature does NOT cover. This prevents scope creep.

- {What is explicitly excluded}
- {What will be handled in a future spec}

---

## 5. Open Questions

> Use `[NEEDS CLARIFICATION: specific question]` for every ambiguity. Do NOT guess.
> Remove all markers before status can move to `approved`.

- [NEEDS CLARIFICATION: {specific question about requirement}]
- [NEEDS CLARIFICATION: {another ambiguity}]

---

## 6. Business Rules

> Non-technical rules that must always hold true.

- {Rule 1}
- {Rule 2}

---

## 7. Non-Functional Requirements

> Specific NFRs for this feature (latency, uptime, etc.). Refer to Constitution Article VII.

- **Performance**: {specific target, e.g. "response ≤ 500ms"}
- **Reliability**: {fallback behavior if applicable}
- **Security**: {specific RBAC requirements, which roles can access}

---

## 8. Spec Completeness Checklist

> Complete this before changing status to `approved`.

- [ ] No `[NEEDS CLARIFICATION]` markers remain
- [ ] All acceptance criteria are testable and measurable
- [ ] User stories cover all roles that interact with this feature
- [ ] Out-of-scope section is filled
- [ ] No mention of tech stack, libraries, or implementation details
- [ ] Business rules are explicit and unambiguous
- [ ] NFRs are specific (not vague like "it should be fast")
