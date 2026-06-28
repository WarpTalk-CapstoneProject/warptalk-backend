# Screen: Terminology

## Goal

Let workspace managers maintain business terminology so translation and AI prompts use consistent domain vocabulary.

## Screen flow

```mermaid
flowchart TD
    A["Open Terminology"] --> B["Fetch glossary list by workspace"]
    B --> C{"RBAC allows glossary management?"}
    C -->|No| D["Read-only or forbidden state"]
    C -->|Yes| E["Render glossary table and filters"]
    E --> F{"Action selected"}
    F -->|Create glossary| G["Validate domain and language pair"]
    F -->|Add/edit term| H["Validate duplicate term and required fields"]
    F -->|Import CSV| I["Validate language, schema and duplicates"]
    F -->|Deactivate term| J["Confirm status change"]
    G --> K["Persist and refresh glossary"]
    H --> K
    I --> K
    J --> K
```

## RBAC screen variants

| Role | Screen behavior |
|---|---|
| Owner | Full glossary create/edit/import/export/deactivate. |
| Admin | Full operational glossary management unless workspace policy reserves it for Owner. |
| Member | Read/use visible glossary terms; management controls hidden. |
| External Member | No glossary management; direct meeting UI may show applied term behavior without exposing glossary records. |

## Workspace schema touched

| Entity | Fields shown or affected |
|---|---|
| `workspace.workspace_knowledge_glossaries` | `business_domain`, `source_language`, `target_language`, `term`, `preferred_translation`, `definition`, `usage_note`, `status` |

## Actions

- Create glossary.
- Add/edit/deactivate term.
- Import/export CSV.
- Filter by domain and language pair.

## Requirement baseline behavior

- Show prompt-adapter readiness: active terms only, workspace-scoped, no duplicate same domain/source/target/term.
- Show validation before import: unsupported language, duplicate term, malformed CSV.
