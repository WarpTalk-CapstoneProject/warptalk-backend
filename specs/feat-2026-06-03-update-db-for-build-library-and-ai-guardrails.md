# Spec: Update DB for Document Library and AI Guardrails
Date: 2026-06-03
Status: draft

## Problem Statement
WarpTalk needs a centralized knowledge library for Enterprise workspaces. This library must store and manage workspace documents, enforce security guardrails (sensitivity, confidentiality, access levels), support compliance (retention states), and prepare metadata for downstream AI processing (RAG, vector search, semantic context).

## Bounded Context & Scoping Decisions

To minimize Sprint scope and keep the boundary between **Workspace Service** and **AI Service** clean, we propose the following database scoping:

1. **Keep `workspace.workspace_documents` (Core Metadata)**: 
   - Stores general metadata (name, size, type, storage_key, storage_provider) and guardrail/AI flags (`is_sensitive`, `ai_eligible`, `confidentiality_level`, `ingestion_status`).
2. **Drop `workspace.workspace_document_chunks` (Moved to AI Domain)**:
   - *Scoping Decision*: Do NOT duplicate chunks in the `workspace` schema. The database already contains `ai.vector_documents` and `ai.vector_chunks` in the `ai` schema. Text chunking, token counting, and vector IDs are specific to the AI/RAG domain. Workspace service should only catalog the source documents.
3. **Keep `workspace.workspace_document_audits` (Compliance)**:
   - Tracks audit logs (views, uploads, deletes, index, translate) to comply with Enterprise security audits.
4. **Keep `workspace.workspace_document_access_policies` (Centralized ACL)**:
   - *Scoping Decision*: To avoid hardcoding visibility rules (which prevents Owners/Admins from specifying exact users or custom exclusions), we use a dedicated policies table. This allows for flexible Access Control Lists (ACLs) to specifically grant or deny permissions for individual members or roles.
5. **Keep and Migrate `workspace_knowledge_glossaries` (Workspace-Level Terminology Catalog)**:
   - *Scoping Decision*: Specialized glossaries (dictionaries) are essential for context-correct translation, so we migrate workspace-wide glossaries to `workspace.workspace_knowledge_glossaries`. By adding a `business_domain` column, we support department-specific professional dictionaries (e.g. engineering vs. legal terminology).
   - *Glossary Levels*:
     - **Transcript-Level Glossary** (stored in `transcript.glossaries` & `transcript.glossary_terms` under the `transcript` schema): Short-term, session-specific terms.
     - **Workspace-Level Glossary** (stored in `workspace.workspace_knowledge_glossaries` under the `workspace` schema): Reusable terms across the workspace.

---

## Business Rules & Access Policies

### 1. Document Access Control (ACL Model)
Access control is fully dynamic and driven by rows in `workspace_document_access_policies` matching a document:
- **Default Policy (On Upload)**:
  By default, a newly created document registers the following default permissions:
  - `Allow` `view` & `download` for Role `owner`
  - `Allow` `view` & `download` for Role `admin`
  - `Allow` `view` & `download` for Role `member` (internal users only)
  - Guest/external users (`external` role) have **no access** by default.
- **Granular Override Rules**:
  Workspace Owners and Admins can configure custom rules to override default policies:
  - **Explicit Allow**: Grant a specific Guest/External user access (e.g. `Allow` `view` for `subject_type = 'member'` with `subject_id = {external_user_id}`).
  - **Explicit Deny (Ban)**: Block specific internal members from seeing a sensitive document (e.g. `Deny` `view` for `subject_type = 'member'` with `subject_id = {internal_user_id}`).

### 2. Evaluation Order (Deny-Override)
When evaluating if a user has access to a document:
1. **Explicit Deny**: Check if there are any `Deny` rules matching the user's ID (`subject_type = 'member'`) or the user's workspace role (`subject_type = 'role'`). If any match, access is immediately **dened**.
2. **Explicit Allow**: Check if there is an `Allow` rule matching the user's ID. If yes, access is **allowed**.
3. **Role-based Allow**: Check if there is an `Allow` rule matching the user's workspace role. If yes, access is **allowed**.
4. **Default**: Otherwise, access is **denied**.

### 3. Management Permissions
- Only workspace **Owners** or **Admins** have the right to insert, update, or delete rules in `workspace_document_access_policies`. Regular members and external guests cannot modify access policies.

### 4. Workspace Lifecycle & File Soft-Deletes
- **No Hard Deletes**: The workspace service does not support hard deleting workspaces. Workspaces are deactivated (`is_active = false` or `status = 'inactive'`) and soft-deleted (`deleted_at` is set).
- **Foreign Key Safety**: Database foreign key constraints from `workspace_documents` to `workspaces` will use `ON DELETE RESTRICT` to ensure data integrity and prevent accidental cascade drops.
- **File Soft-Delete Policy**: No physical delete operations are performed on S3/MinIO binary files when a document is soft-deleted or its retention state changes. This preserves full file integrity for compliance and audit trail records.

### 5. AI Contextual Translation & Workspace Tone Alignment
To support context-correct translation per department, custom dictionaries, and workspace-specific cultural language tone:
1. **Department-Specific Context (`business_domain`)**:
   - The `business_domain` column on `workspace_documents` (e.g. `legal`, `engineering`, `support`) specifies the department the document belongs to.
   - When translating a conversation for a specific department's room, the translation engine queries `workspace_documents` for documents in that domain (e.g., `document_type = 'translation_context'`) and uses their content/summaries as system context prompts for the LLM.
2. **Professional Dictionaries (`workspace_knowledge_glossaries`)**:
   - Custom translation dictionaries are stored in `workspace_knowledge_glossaries` and can be scoped to a specific `business_domain` (department).
   - This ensures that technical terminology is translated differently and correctly depending on whether the department is Legal, Engineering, or Sales.
3. **Workspace Tone & Culture Alignment**:
   - Workspace localization, politeness rules, and team conventions are stored as metadata in `workspaces.settings` (e.g., `translation_tone: 'formal' | 'informal'`) and as dedicated files in `workspace_documents` with `document_type = 'style_guide'`.
   - The translation/transcription services pull these settings to inject formatting and tone instructions into the LLM system prompt.

---

## Microservice Document Library ERD

```mermaid
erDiagram
    WORKSPACES {
        uuid id PK
        varchar name
        varchar slug UK
        uuid owner_id
        boolean is_active
    }

    WORKSPACE_DOCUMENTS {
        uuid id PK
        uuid workspace_id FK
        uuid uploaded_by
        uuid owner_id
        varchar name
        varchar file_name
        varchar file_extension
        varchar mime_type
        bigint size_bytes
        varchar storage_provider
        varchar storage_key
        varchar source_type
        varchar document_type
        varchar source_language
        varchar detected_language
        varchar business_domain
        text summary
        jsonb keywords
        boolean ai_eligible
        jsonb ai_usage_policy
        varchar ingestion_status
        timestamptz last_indexed_at
        varchar index_version
        boolean is_sensitive
        varchar confidentiality_level
        varchar retention_state
        varchar status
        timestamptz created_at
        timestamptz updated_at
        timestamptz deleted_at
        uuid deleted_by
    }

    WORKSPACE_DOCUMENT_ACCESS_POLICIES {
        uuid id PK
        uuid document_id FK
        uuid workspace_id FK
        varchar subject_type
        uuid subject_id
        varchar role_key
        varchar permission
        varchar effect
        timestamptz created_at
        uuid created_by
        timestamptz updated_at
        uuid updated_by
    }

    WORKSPACE_DOCUMENT_AUDITS {
        uuid id PK
        uuid document_id FK
        uuid workspace_id FK
        uuid actor_id
        varchar action
        timestamptz action_at
        jsonb metadata
        varchar ip_address
        varchar user_agent
    }

    WORKSPACE_KNOWLEDGE_GLOSSARIES {
        uuid id PK
        uuid workspace_id FK
        varchar name
        varchar business_domain
        varchar source_language
        varchar target_language
        varchar term
        varchar preferred_translation
        varchar part_of_speech
        text definition
        text usage_note
        varchar status
        timestamptz created_at
        uuid created_by
        timestamptz updated_at
        uuid updated_by
    }

    VECTOR_DOCUMENTS {
        uuid id PK
        uuid workspace_id
        uuid collection_id
        varchar source_type
        uuid source_id
    }
    note for VECTOR_DOCUMENTS "Located in 'ai' schema"

    VECTOR_CHUNKS {
        uuid id PK
        uuid vector_document_id FK
        int chunk_order
        text text_preview
        varchar qdrant_point_id UK
    }
    note for VECTOR_CHUNKS "Located in 'ai' schema"

    WORKSPACES ||--o{ WORKSPACE_DOCUMENTS : "contains"
    WORKSPACES ||--o{ WORKSPACE_DOCUMENT_ACCESS_POLICIES : "defines"
    WORKSPACES ||--o{ WORKSPACE_KNOWLEDGE_GLOSSARIES : "owns"
    WORKSPACE_DOCUMENTS ||--o{ WORKSPACE_DOCUMENT_ACCESS_POLICIES : "governs"
    WORKSPACE_DOCUMENTS ||--o{ WORKSPACE_DOCUMENT_AUDITS : "logs"
    WORKSPACE_DOCUMENTS ||--o? VECTOR_DOCUMENTS : "vectorized_in"
    VECTOR_DOCUMENTS ||--o{ VECTOR_CHUNKS : "divided_into"
```

---

## Constitution Compliance Check
- [x] Follows Article I (Clean Architecture)? Yes, workspace service catalogs metadata; AI service handles vectors.
- [x] Communication channels unchanged (Article II)? Yes, downstream services register files via gRPC or events.
- [x] No hardcoded secrets (Article III)? Yes.
- [x] UUID v7 for primary keys? Yes.
