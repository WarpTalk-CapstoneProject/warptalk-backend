CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'workspace') THEN
        CREATE SCHEMA workspace;
    END IF;
END $EF$;


CREATE TABLE workspace.workspaces (
    id uuid NOT NULL DEFAULT (uuidv7()),
    name character varying(150) NOT NULL,
    slug character varying(100) NOT NULL,
    owner_id uuid NOT NULL,
    logo_url character varying(500),
    allow_external_collaboration boolean NOT NULL,
    require_verified_domain_for_internal boolean NOT NULL DEFAULT TRUE,
    allow_subdomains boolean NOT NULL,
    settings jsonb NOT NULL DEFAULT ('{}'::jsonb),
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    created_by uuid,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_by uuid,
    deleted_at timestamp with time zone,
    deleted_by uuid,
    CONSTRAINT workspaces_pkey PRIMARY KEY (id)
);

CREATE TABLE workspace.workspace_documents (
    id uuid NOT NULL DEFAULT (uuidv7()),
    workspace_id uuid NOT NULL,
    uploaded_by uuid,
    owner_id uuid,
    name character varying(255) NOT NULL,
    file_name character varying(255) NOT NULL,
    file_extension character varying(20) NOT NULL,
    mime_type character varying(100) NOT NULL,
    size_bytes bigint NOT NULL,
    storage_provider character varying(50) NOT NULL,
    storage_key character varying(500) NOT NULL,
    source_type character varying(50) NOT NULL,
    source_id uuid,
    document_type character varying(50) NOT NULL,
    source_language character varying(20),
    detected_language character varying(20),
    business_domain character varying(100),
    summary text,
    keywords jsonb,
    ai_eligible boolean NOT NULL DEFAULT TRUE,
    ai_usage_policy jsonb,
    ingestion_status character varying(30) NOT NULL DEFAULT ('pending'::character varying),
    last_indexed_at timestamp with time zone,
    index_version character varying(50),
    is_sensitive boolean NOT NULL,
    confidentiality_level character varying(30) NOT NULL DEFAULT ('public_internal'::character varying),
    retention_state character varying(30) NOT NULL DEFAULT ('active'::character varying),
    status character varying(30) NOT NULL DEFAULT ('active'::character varying),
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    deleted_by uuid,
    CONSTRAINT workspace_documents_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_documents_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id) ON DELETE RESTRICT
);

CREATE TABLE workspace.workspace_invitations (
    id uuid NOT NULL DEFAULT (uuidv7()),
    workspace_id uuid NOT NULL,
    email character varying(320) NOT NULL,
    role_id uuid NOT NULL,
    membership_type character varying(20) NOT NULL DEFAULT ('internal'::character varying),
    matched_domain_id uuid,
    invited_by uuid NOT NULL,
    token_hash character varying(255) NOT NULL,
    status character varying(20) NOT NULL DEFAULT ('pending'::character varying),
    expires_at timestamp with time zone NOT NULL,
    accepted_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT workspace_invitations_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_invitations_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id)
);

CREATE TABLE workspace.workspace_knowledge_glossaries (
    id uuid NOT NULL DEFAULT (uuidv7()),
    workspace_id uuid NOT NULL,
    name character varying(255) NOT NULL,
    business_domain character varying(100),
    source_language character varying(20) NOT NULL,
    target_language character varying(20) NOT NULL,
    term character varying(255) NOT NULL,
    preferred_translation character varying(255) NOT NULL,
    part_of_speech character varying(50),
    definition text,
    usage_note text,
    status character varying(30) NOT NULL DEFAULT ('active'::character varying),
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    created_by uuid,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_by uuid,
    CONSTRAINT workspace_knowledge_glossaries_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_knowledge_glossaries_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id) ON DELETE RESTRICT
);

CREATE TABLE workspace.workspace_members (
    id uuid NOT NULL DEFAULT (uuidv7()),
    workspace_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    membership_type character varying(20) NOT NULL DEFAULT ('internal'::character varying),
    status character varying(20) NOT NULL DEFAULT ('active'::character varying),
    joined_at timestamp with time zone NOT NULL DEFAULT (now()),
    removed_at timestamp with time zone,
    removed_by uuid,
    CONSTRAINT workspace_members_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_members_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id)
);

CREATE TABLE workspace.workspace_verified_domains (
    id uuid NOT NULL DEFAULT (uuidv7()),
    workspace_id uuid NOT NULL,
    domain character varying(255) NOT NULL,
    status character varying(20) NOT NULL DEFAULT ('pending'::character varying),
    verification_method character varying(50) NOT NULL,
    verification_token character varying(255) NOT NULL,
    verified_at timestamp with time zone,
    verified_by uuid,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    created_by uuid,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_by uuid,
    CONSTRAINT workspace_verified_domains_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_verified_domains_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id)
);

CREATE TABLE workspace.workspace_document_access_policies (
    id uuid NOT NULL DEFAULT (uuidv7()),
    document_id uuid NOT NULL,
    workspace_id uuid NOT NULL,
    subject_type character varying(30) NOT NULL,
    subject_id uuid,
    subject_key character varying(150),
    permission character varying(30) NOT NULL,
    effect character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    created_by uuid,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_by uuid,
    CONSTRAINT workspace_document_access_policies_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_document_access_policies_document_id_fkey FOREIGN KEY (document_id) REFERENCES workspace.workspace_documents (id) ON DELETE CASCADE,
    CONSTRAINT workspace_document_access_policies_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id) ON DELETE RESTRICT
);

CREATE TABLE workspace.workspace_document_audits (
    id uuid NOT NULL DEFAULT (uuidv7()),
    document_id uuid NOT NULL,
    workspace_id uuid NOT NULL,
    actor_id uuid,
    action character varying(50) NOT NULL,
    action_at timestamp with time zone NOT NULL DEFAULT (now()),
    metadata jsonb,
    ip_address character varying(64),
    user_agent character varying(500),
    CONSTRAINT workspace_document_audits_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_document_audits_document_id_fkey FOREIGN KEY (document_id) REFERENCES workspace.workspace_documents (id) ON DELETE CASCADE,
    CONSTRAINT workspace_document_audits_workspace_id_fkey FOREIGN KEY (workspace_id) REFERENCES workspace.workspaces (id) ON DELETE RESTRICT
);

CREATE INDEX idx_doc_access_policies_doc_id ON workspace.workspace_document_access_policies (document_id);

CREATE INDEX idx_doc_access_policies_lookup ON workspace.workspace_document_access_policies (document_id, subject_type, subject_id);

CREATE INDEX "IX_workspace_document_access_policies_workspace_id" ON workspace.workspace_document_access_policies (workspace_id);

CREATE INDEX idx_workspace_doc_audits_actor_action ON workspace.workspace_document_audits (actor_id, action_at);

CREATE INDEX idx_workspace_doc_audits_doc_id ON workspace.workspace_document_audits (document_id);

CREATE INDEX idx_workspace_doc_audits_workspace_action ON workspace.workspace_document_audits (workspace_id, action_at);

CREATE INDEX idx_workspace_documents_workspace_ai ON workspace.workspace_documents (workspace_id, ai_eligible);

CREATE INDEX idx_workspace_documents_workspace_confidentiality ON workspace.workspace_documents (workspace_id, confidentiality_level);

CREATE INDEX idx_workspace_documents_workspace_id ON workspace.workspace_documents (workspace_id);

CREATE INDEX idx_workspace_documents_workspace_lang ON workspace.workspace_documents (workspace_id, source_language);

CREATE INDEX idx_workspace_documents_workspace_retention ON workspace.workspace_documents (workspace_id, retention_state);

CREATE INDEX idx_workspace_documents_workspace_status ON workspace.workspace_documents (workspace_id, status);

CREATE INDEX "IX_workspace_invitations_workspace_id" ON workspace.workspace_invitations (workspace_id);

CREATE UNIQUE INDEX workspace_invitations_token_hash_key ON workspace.workspace_invitations (token_hash);

CREATE INDEX idx_workspace_glossaries_lookup ON workspace.workspace_knowledge_glossaries (workspace_id, business_domain, source_language);

CREATE UNIQUE INDEX workspace_knowledge_glossarie_workspace_id_business_domain__key ON workspace.workspace_knowledge_glossaries (workspace_id, business_domain, source_language, target_language, term);

CREATE UNIQUE INDEX workspace_members_workspace_id_user_id_key ON workspace.workspace_members (workspace_id, user_id);

CREATE UNIQUE INDEX idx_workspace_verified_domains_unique_verified ON workspace.workspace_verified_domains (domain) WHERE ((status)::text = 'verified'::text);

CREATE INDEX "IX_workspace_verified_domains_workspace_id" ON workspace.workspace_verified_domains (workspace_id);

CREATE UNIQUE INDEX workspaces_slug_key ON workspace.workspaces (slug);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260614132711_InitialWorkspace', '10.0.5');

COMMIT;

