using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Npgsql.NameTranslation;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Persistance;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    private static readonly NpgsqlNullNameTranslator Names = new();
    public static void Enums(NpgsqlDbContextOptionsBuilder o)
    {
        var names = Names;
        o.MapEnum<ConversationStatus>("conversation_status", nameTranslator: names);
        o.MapEnum<MessageRole>("message_role", nameTranslator: names);
        o.MapEnum<RunStatus>("run_status", nameTranslator: names);
        o.MapEnum<ToolEffect>("tool_effect", nameTranslator: names);
        o.MapEnum<InvocationStatus>("invocation_status", nameTranslator: names);
    }
    protected override void OnModelCreating(ModelBuilder model)
    {
        var tables = new Dictionary<Type, string> { [typeof(ModelDeployment)] = "ai_model_deployment", [typeof(Conversation)] = "conversation", [typeof(ChatMessage)] = "chat_message", [typeof(AssistantRun)] = "assistant_run", [typeof(ToolInvocation)] = "tool_invocation", [typeof(ActionConfirmation)] = "action_confirmation" };
        foreach (var (type, table) in tables)
        {
            var e = model.Entity(type).ToTable(table);
            foreach (var property in type.GetProperties())
            {
                var p = e.Property(property.Name).HasColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
                if (property.Name is "CanonicalArguments" or "Preview" or "ExpectedSourceRevisions" or "ResultSummary") p.HasColumnType("jsonb");
                if (property.Name is "Id" or "InvocationId") p.ValueGeneratedNever();
                if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(DateTimeOffset)) p.HasPrecision(6);
            }
        }
        model.Entity<ModelDeployment>().HasKey(m => m.Id);
        model.Entity<Conversation>().HasKey(c => new { c.SchoolId, c.Id });
        model.Entity<Conversation>().HasIndex(c => new { c.OwnerUserId, c.CreationRequestId }).IsUnique();
        model.Entity<Conversation>().HasIndex(c => new { c.OwnerUserId, c.ArchiveRequestId }).IsUnique();
        model.Entity<ChatMessage>().HasKey(m => new { m.SchoolId, m.ConversationId, m.Id });
        model.Entity<ChatMessage>().HasIndex(m => new { m.SchoolId, m.ConversationId, m.ClientRequestId }).IsUnique();
        model.Entity<ChatMessage>().HasIndex(m => new { m.SchoolId, m.ConversationId, m.SequenceNo }).IsUnique();
        model.Entity<AssistantRun>().HasKey(r => new { r.SchoolId, r.ConversationId, r.Id });
        model.Entity<ToolInvocation>().HasKey(i => new { i.SchoolId, i.ConversationId, i.Id });
        model.Entity<ToolInvocation>().HasIndex(i => new { i.SchoolId, i.Id }).IsUnique();
        model.Entity<ToolInvocation>().HasIndex(i => new { i.SchoolId, i.OperationId }).IsUnique();
        model.Entity<ActionConfirmation>().HasKey(c => new { c.SchoolId, c.ConversationId, c.InvocationId });
        model.Entity<ActionConfirmation>().Property(c => c.ConfirmedAt).ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        foreach (var type in new[] { typeof(ChatMessage), typeof(AssistantRun), typeof(ToolInvocation), typeof(ActionConfirmation) })
            model.Entity(type).HasOne(typeof(Conversation)).WithMany().HasForeignKey("SchoolId", "ConversationId").OnDelete(DeleteBehavior.Restrict);
        model.Entity<AssistantRun>().HasOne<ModelDeployment>().WithMany().HasForeignKey(r => r.ModelDeploymentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AssistantRun>().HasOne<ChatMessage>().WithMany().HasForeignKey(r => new { r.SchoolId, r.ConversationId, r.TriggerMessageId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AssistantRun>().HasOne<ChatMessage>().WithMany().HasForeignKey(r => new { r.SchoolId, r.ConversationId, r.ResponseMessageId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ToolInvocation>().HasOne<AssistantRun>().WithMany().HasForeignKey(i => new { i.SchoolId, i.ConversationId, i.RunId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ActionConfirmation>().HasOne<ToolInvocation>().WithMany().HasForeignKey(c => new { c.SchoolId, c.ConversationId, c.InvocationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
