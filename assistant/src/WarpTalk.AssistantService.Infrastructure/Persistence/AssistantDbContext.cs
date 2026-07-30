using Microsoft.EntityFrameworkCore;
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

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
