using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.WorkspaceService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workspace");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:artifact_type", "TRANSCRIPT_EXPORT,SUMMARY_EXPORT,DEBUG_LOG,OPTIONAL_RECORDING,AUDIO_SAMPLE")
                .Annotation("Npgsql:Enum:consent_status", "GRANTED,REVOKED,EXPIRED")
                .Annotation("Npgsql:Enum:job_status", "QUEUED,PROCESSING,COMPLETED,FAILED,CANCELLED")
                .Annotation("Npgsql:Enum:notification_status", "PENDING,SENT,DELIVERED,FAILED,READ")
                .Annotation("Npgsql:Enum:participant_status", "INVITED,WAITING,CONNECTED,DISCONNECTED,LEFT,KICKED,REJECTED")
                .Annotation("Npgsql:Enum:room_status", "SCHEDULED,WAITING,IN_PROGRESS,PAUSED,ENDED,CANCELLED,EXPIRED,FAILED")
                .Annotation("Npgsql:Enum:ticket_status", "OPEN,IN_PROGRESS,RESOLVED,CLOSED")
                .Annotation("Npgsql:Enum:transcript.correction_status", "PENDING,ACCEPTED,REJECTED")
                .Annotation("Npgsql:Enum:transcript.correction_type", "STT,TRANSLATION")
                .Annotation("Npgsql:Enum:transcript.transcript_status", "RECORDING,FINALIZING,FINALIZED,ARCHIVED")
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", ",,");

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logo_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    allow_external_collaboration = table.Column<bool>(type: "boolean", nullable: false),
                    require_verified_domain_for_internal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    allow_subdomains = table.Column<bool>(type: "boolean", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspaces_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_documents",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_extension = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    detected_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    business_domain = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    keywords = table.Column<string>(type: "jsonb", nullable: true),
                    ai_eligible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ai_usage_policy = table.Column<string>(type: "jsonb", nullable: true),
                    ingestion_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValueSql: "'pending'::character varying"),
                    last_indexed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    index_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    confidentiality_level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValueSql: "'public_internal'::character varying"),
                    retention_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValueSql: "'active'::character varying"),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValueSql: "'active'::character varying"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_documents_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_documents_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_invitations",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'internal'::character varying"),
                    matched_domain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'pending'::character varying"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_invitations_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_invitations_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "workspace_knowledge_glossaries",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    business_domain = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    source_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    term = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    preferred_translation = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    part_of_speech = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    definition = table.Column<string>(type: "text", nullable: true),
                    usage_note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValueSql: "'active'::character varying"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_knowledge_glossaries_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_knowledge_glossaries_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_members",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'internal'::character varying"),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'active'::character varying"),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    removed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    removed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_members_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_members_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "workspace_verified_domains",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'pending'::character varying"),
                    verification_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    verification_token = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_verified_domains_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_verified_domains_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "workspace_document_access_policies",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    permission = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    effect = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_document_access_policies_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_document_access_policies_document_id_fkey",
                        column: x => x.document_id,
                        principalSchema: "workspace",
                        principalTable: "workspace_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "workspace_document_access_policies_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_document_audits",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    action_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspace_document_audits_pkey", x => x.id);
                    table.ForeignKey(
                        name: "workspace_document_audits_document_id_fkey",
                        column: x => x.document_id,
                        principalSchema: "workspace",
                        principalTable: "workspace_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "workspace_document_audits_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "workspace",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_doc_access_policies_doc_id",
                schema: "workspace",
                table: "workspace_document_access_policies",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_doc_access_policies_lookup",
                schema: "workspace",
                table: "workspace_document_access_policies",
                columns: new[] { "document_id", "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_document_access_policies_workspace_id",
                schema: "workspace",
                table: "workspace_document_access_policies",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "idx_workspace_doc_audits_actor_action",
                schema: "workspace",
                table: "workspace_document_audits",
                columns: new[] { "actor_id", "action_at" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_doc_audits_doc_id",
                schema: "workspace",
                table: "workspace_document_audits",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_workspace_doc_audits_workspace_action",
                schema: "workspace",
                table: "workspace_document_audits",
                columns: new[] { "workspace_id", "action_at" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_ai",
                schema: "workspace",
                table: "workspace_documents",
                columns: new[] { "workspace_id", "ai_eligible" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_confidentiality",
                schema: "workspace",
                table: "workspace_documents",
                columns: new[] { "workspace_id", "confidentiality_level" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_id",
                schema: "workspace",
                table: "workspace_documents",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_lang",
                schema: "workspace",
                table: "workspace_documents",
                columns: new[] { "workspace_id", "source_language" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_retention",
                schema: "workspace",
                table: "workspace_documents",
                columns: new[] { "workspace_id", "retention_state" });

            migrationBuilder.CreateIndex(
                name: "idx_workspace_documents_workspace_status",
                schema: "workspace",
                table: "workspace_documents",
                columns: new[] { "workspace_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_invitations_workspace_id",
                schema: "workspace",
                table: "workspace_invitations",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "workspace_invitations_token_hash_key",
                schema: "workspace",
                table: "workspace_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_workspace_glossaries_lookup",
                schema: "workspace",
                table: "workspace_knowledge_glossaries",
                columns: new[] { "workspace_id", "business_domain", "source_language" });

            migrationBuilder.CreateIndex(
                name: "workspace_knowledge_glossarie_workspace_id_business_domain__key",
                schema: "workspace",
                table: "workspace_knowledge_glossaries",
                columns: new[] { "workspace_id", "business_domain", "source_language", "target_language", "term" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "workspace_members_workspace_id_user_id_key",
                schema: "workspace",
                table: "workspace_members",
                columns: new[] { "workspace_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_workspace_verified_domains_unique_verified",
                schema: "workspace",
                table: "workspace_verified_domains",
                column: "domain",
                unique: true,
                filter: "((status)::text = 'verified'::text)");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_verified_domains_workspace_id",
                schema: "workspace",
                table: "workspace_verified_domains",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "workspaces_slug_key",
                schema: "workspace",
                table: "workspaces",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_document_access_policies",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_document_audits",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_invitations",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_knowledge_glossaries",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_members",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_verified_domains",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_documents",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "workspace");
        }
    }
}
