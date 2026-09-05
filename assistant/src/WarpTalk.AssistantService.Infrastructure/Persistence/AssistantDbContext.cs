using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Persistence;

public partial class AssistantDbContext : DbContext
{
    public AssistantDbContext()
    {
    }

    public AssistantDbContext(DbContextOptions<AssistantDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AssistantConversation> AssistantConversations { get; set; }

    public virtual DbSet<AssistantMessage> AssistantMessages { get; set; }

    public virtual DbSet<AssistantToolCall> AssistantToolCalls { get; set; }
    public virtual DbSet<Plugin> Plugins { get; set; }
    public virtual DbSet<PluginInstallation> PluginInstallations { get; set; }
    public virtual DbSet<PluginConnection> PluginConnections { get; set; }
    public virtual DbSet<PluginToolAudit> PluginToolAudits { get; set; }
    public virtual DbSet<PluginConfirmationToken> PluginConfirmationTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssistantConversation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("assistant_conversations_pkey");
            entity.ToTable("assistant_conversations", "assistant");

            entity.HasIndex(e => new { e.WorkspaceId, e.UserId, e.LastMessageAt }, "idx_assistant_conversations_workspace_user");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Title).HasMaxLength(255).HasDefaultValue("New chat").HasColumnName("title");
            entity.Property(e => e.ContextScope).HasColumnName("context_scope");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.LastMessageAt).HasColumnName("last_message_at");
            entity.Property(e => e.IsArchived).HasDefaultValue(false).HasColumnName("is_archived");
        });

        modelBuilder.Entity<AssistantMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("assistant_messages_pkey");
            entity.ToTable("assistant_messages", "assistant");

            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt }, "idx_assistant_messages_conversation");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ConversationId).HasColumnName("conversation_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Role).HasMaxLength(20).HasColumnName("role");
            entity.Property(e => e.Content).HasDefaultValue("").HasColumnName("content");
            entity.Property(e => e.ToolCallsJson).HasColumnName("tool_calls_json");
            entity.Property(e => e.ToolResultsJson).HasColumnName("tool_results_json");
            entity.Property(e => e.SourcesJson)
                .HasColumnType("jsonb")
                .HasColumnName("sources_json");
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("completed").HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");

            entity.HasOne(d => d.Conversation).WithMany(p => p.Messages)
                .HasForeignKey(d => d.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("assistant_messages_conversation_id_fkey");
        });

        modelBuilder.Entity<AssistantToolCall>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("assistant_tool_calls_pkey");
            entity.ToTable("assistant_tool_calls", "assistant");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.ToolName).HasMaxLength(100).HasColumnName("tool_name");
            entity.Property(e => e.ArgumentsJson).HasColumnName("arguments_json");
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("pending").HasColumnName("status");
            entity.Property(e => e.ResultJson).HasColumnName("result_json");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");

            entity.HasOne(d => d.Message).WithMany(p => p.ToolCalls)
                .HasForeignKey(d => d.MessageId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("assistant_tool_calls_message_id_fkey");
        });

        modelBuilder.Entity<Plugin>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plugins_pkey");
            entity.ToTable("plugins", "assistant");
            entity.HasIndex(e => e.PluginKey, "plugins_plugin_key").IsUnique();
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PluginKey).HasMaxLength(100).HasColumnName("plugin_key");
            entity.Property(e => e.Label).HasMaxLength(150).HasColumnName("label");
            entity.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
            entity.Property(e => e.AvatarUrl).HasMaxLength(1000).HasColumnName("avatar_url");
            entity.Property(e => e.Provider).HasMaxLength(100).HasColumnName("provider");
            entity.Property(e => e.RequiredScopesJson).HasColumnType("jsonb").HasDefaultValue("[]").HasColumnName("required_scopes_json");
            entity.Property(e => e.ToolsJson).HasColumnType("jsonb").HasDefaultValue("[]").HasColumnName("tools_json");
            entity.Property(e => e.Kind).HasMaxLength(20).HasDefaultValue(PluginConstants.PluginKind.Native).HasColumnName("kind");
            entity.Property(e => e.McpServerUrl).HasMaxLength(1000).HasColumnName("mcp_server_url");
            entity.Property(e => e.OAuthAuthorizationEndpoint).HasMaxLength(1000).HasColumnName("oauth_authorization_endpoint");
            entity.Property(e => e.OAuthTokenEndpoint).HasMaxLength(1000).HasColumnName("oauth_token_endpoint");
            entity.Property(e => e.OAuthRevokeEndpoint).HasMaxLength(1000).HasColumnName("oauth_revoke_endpoint");
            entity.Property(e => e.OAuthRegistrationEndpoint).HasMaxLength(1000).HasColumnName("oauth_registration_endpoint");
            entity.Property(e => e.OAuthClientId).HasMaxLength(500).HasColumnName("oauth_client_id");
            entity.Property(e => e.OAuthClientSecretEncrypted).HasColumnName("oauth_client_secret_encrypted");
            entity.Property(e => e.OAuthClientSource).HasMaxLength(20).HasDefaultValue("unresolved").HasColumnName("oauth_client_source");
            entity.Property(e => e.OAuthCimdSupported).HasColumnName("oauth_cimd_supported");
            entity.Property(e => e.OAuthIssParameterSupported).HasColumnName("oauth_iss_parameter_supported");
            entity.Property(e => e.OAuthTokenEndpointAuthMethod).HasMaxLength(40).HasColumnName("oauth_token_endpoint_auth_method");
            entity.Property(e => e.ToolsSyncedAt).HasColumnName("tools_synced_at");
            entity.Property(e => e.ToolsManifestHash).HasMaxLength(128).HasColumnName("tools_manifest_hash");
            entity.Property(e => e.IsActive).HasDefaultValue(true).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
        });

        modelBuilder.Entity<PluginInstallation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plugin_installations_pkey");
            entity.ToTable("plugin_installations", "assistant");
            entity.HasIndex(e => new { e.UserId, e.PluginId }, "plugin_installations_user_plugin_id_key").IsUnique();
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.PluginId).HasColumnName("plugin_id");
            entity.Property(e => e.Status).HasMaxLength(30).HasColumnName("status");
            entity.Property(e => e.ConfigJson).HasColumnType("jsonb").HasColumnName("config_json");
            entity.Property(e => e.InstalledAt).HasDefaultValueSql("now()").HasColumnName("installed_at");
            entity.Property(e => e.DisabledAt).HasColumnName("disabled_at");

            entity.HasOne<Plugin>()
                .WithMany()
                .HasForeignKey(e => e.PluginId)
                .HasConstraintName("plugin_installations_plugin_id_fkey");
        });

        modelBuilder.Entity<PluginConnection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plugin_connections_pkey");
            entity.ToTable("plugin_connections", "assistant");
            entity.HasIndex(e => new { e.UserId, e.PluginId }, "plugin_connections_user_plugin_id_key").IsUnique();
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.PluginId).HasColumnName("plugin_id");
            entity.Property(e => e.ProviderAccountId).HasMaxLength(255).HasColumnName("provider_account_id");
            entity.Property(e => e.ProviderEmail).HasMaxLength(320).HasColumnName("provider_email");
            entity.Property(e => e.Status).HasMaxLength(30).HasColumnName("status");
            entity.Property(e => e.ScopesJson).HasColumnType("jsonb").HasDefaultValue("[]").HasColumnName("scopes_json");
            entity.Property(e => e.EncryptedRefreshToken).HasColumnName("encrypted_refresh_token");
            entity.Property(e => e.EncryptedAccessToken).HasColumnName("encrypted_access_token");
            entity.Property(e => e.AccessTokenExpiresAt).HasColumnName("access_token_expires_at");
            entity.Property(e => e.TokenRotatedAt).HasColumnName("token_rotated_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");

            entity.HasOne<Plugin>()
                .WithMany()
                .HasForeignKey(e => e.PluginId)
                .HasConstraintName("plugin_connections_plugin_id_fkey");
        });

        modelBuilder.Entity<PluginToolAudit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plugin_tool_audits_pkey");
            entity.ToTable("plugin_tool_audits", "assistant");
            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "idx_plugin_tool_audits_workspace_created");
            entity.HasIndex(e => new { e.UserId, e.CreatedAt }, "idx_plugin_tool_audits_user_created");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ConversationId).HasColumnName("conversation_id");
            entity.Property(e => e.AssistantMessageId).HasColumnName("assistant_message_id");
            entity.Property(e => e.PluginId).HasColumnName("plugin_id");
            entity.Property(e => e.PluginKey).HasMaxLength(100).HasColumnName("plugin_key");
            entity.Property(e => e.ToolName).HasMaxLength(150).HasColumnName("tool_name");
            entity.Property(e => e.InputSummary).HasColumnName("input_summary");
            entity.Property(e => e.ResultStatus).HasMaxLength(80).HasColumnName("result_status");
            entity.Property(e => e.ProviderResourceRef).HasMaxLength(500).HasColumnName("provider_resource_ref");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");

            entity.HasOne<Plugin>()
                .WithMany()
                .HasForeignKey(e => e.PluginId)
                .HasConstraintName("plugin_tool_audits_plugin_id_fkey");
        });

        modelBuilder.Entity<PluginConfirmationToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plugin_confirmation_tokens_pkey");
            entity.ToTable("plugin_confirmation_tokens", "assistant");
            entity.HasIndex(e => new { e.UserId, e.ExpiresAt }, "idx_plugin_confirmation_tokens_user_expires");
            entity.HasIndex(e => new { e.PluginId, e.ToolName, e.CreatedAt }, "idx_plugin_confirmation_tokens_plugin_tool_created");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.PluginId).HasColumnName("plugin_id");
            entity.Property(e => e.PluginKey).HasMaxLength(100).HasColumnName("plugin_key");
            entity.Property(e => e.ToolName).HasMaxLength(150).HasColumnName("tool_name");
            entity.Property(e => e.ArgumentHash).HasMaxLength(64).HasColumnName("argument_hash");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");

            entity.HasOne<Plugin>()
                .WithMany()
                .HasForeignKey(e => e.PluginId)
                .HasConstraintName("plugin_confirmation_tokens_plugin_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
