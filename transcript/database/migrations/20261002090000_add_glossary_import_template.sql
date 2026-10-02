-- Migration: 20261002090000_add_glossary_import_template
-- Ticket: WT-880 (follow-up) - the glossary import template is a FILE SHAPE, configured by the
-- platform admin.
-- Description:
--
-- One row holding the admin's configuration of the glossary import file: which columns the file
-- has, the group each belongs to (source / target / general), their order, whether a column is
-- hidden, its header name, the extra header aliases the importer accepts, and per-language sample
-- values for the faint sample row. The web builds the file for a language PAIR from this one
-- configuration (Source group from the source language's samples, Target group from the target
-- language's samples), so there is no per-pair or per-domain template to keep in sync.
--
-- Deliberately one jsonb document, not a table per column: the configuration is always read and
-- written whole, by one screen, and it holds no term content. glossary_terms is unchanged.
--
-- No seed row. While the table is empty the TranscriptService serves its built-in default
-- (GlossaryImportTemplateDefaults), so the template works with zero admin action, and "Reset to
-- default" in the admin screen is a DELETE of the row.
--
-- Forward-only and idempotent.

CREATE TABLE IF NOT EXISTS transcript.glossary_import_template (
    id          smallint    NOT NULL DEFAULT 1,
    config      jsonb       NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid        NULL,
    CONSTRAINT glossary_import_template_pkey PRIMARY KEY (id),
    CONSTRAINT glossary_import_template_singleton CHECK (id = 1),
    CONSTRAINT glossary_import_template_config_object CHECK (jsonb_typeof(config) = 'object')
);

COMMENT ON TABLE transcript.glossary_import_template IS
    'WT-880: the platform admin''s glossary import file shape (columns, groups, order, hidden, names, aliases, per-language sample values). Singleton row id = 1; absent = built-in default. updated_by is an AuthService user id (no physical FK).';
