extern alias LearningApi;
extern alias SchoolTests;
extern alias ChatTests;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;
using Vairas.LearningTracking.Persistance;
using Vairas.Scheduling.Business;
using Vairas.Scheduling.Domain;
using Vairas.Scheduling.Persistance;
using Vairas.SchoolManagement.Domain;
using OwnersFixture = SchoolTests::Vairas.SchoolManagement.Tests.IntegrationEnvironment;
using ModelFixture = ChatTests::Vairas.ChatBackend.Tests.TestModel;
using SchedulingFixture = Vairas.Scheduling.Tests.TestWorld;
namespace Vairas.ChatBackend.ServiceTests;

public sealed partial class ServiceSmokeTests
{
    private static async Task DelegationWriteDenials(HttpClient gateway, HttpClient scheduling, OwnersFixture owners, Guid school, string userToken)
    {
        async Task<string> Exchange(string caller, string subject, string resource, string scope)
        {
            using var response = await owners.Auth.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange", ["client_id"] = caller, ["client_secret"] = owners.Secret,
                ["subject_token"] = subject, ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token", ["resource"] = resource, ["scope"] = scope, ["school_id"] = school.ToString()
            }));
            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(); return body.GetProperty("access_token").GetString()!;
        }
        var first = await Exchange("chat-backend", userToken, "https://localhost:7900/mcp", "mcp.context.read mcp.preview_lesson_booking");
        var second = await Exchange("mcp-server", first, "https://localhost:7300/", "mcp.preview_lesson_booking");
        foreach (var target in new[] { gateway, scheduling })
        {
            await Json(target, HttpMethod.Post, "/api/v1/practical-lessons", second, new { schoolId = school }, HttpStatusCode.Forbidden);
            await Json(target, HttpMethod.Post, "/api/v1/practical-lessons/booking-previews", second, new { schoolId = Guid.NewGuid() }, HttpStatusCode.Forbidden);
        }
        await Json(scheduling, HttpMethod.Post, "/api/v1/practical-lessons/booking-previews", first, new { schoolId = school }, HttpStatusCode.Unauthorized);
    }
    private static async Task RemainingTools(HttpClient gateway, OwnersFixture owners, SchedulingDatabase schedule, SchedulingFixture fixture, Guid school, Guid conversation, ModelFixture model, string token)
    {
        async Task<JsonElement> Tool(string name, object arguments)
        {
            model.Turns.Enqueue(new(null, [new(Guid.NewGuid().ToString(), name, Vairas.ChatBackend.Business.Wire.Element(arguments))]));
            await Json(gateway, HttpMethod.Post, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", token, new { text = "Synthetic check " + name });
            using var result = JsonDocument.Parse(model.Prompts[^1].Last().Text!); return result.RootElement.Clone();
        }
        var input = fixture.Practical() with { SchoolId = school };
        var lessons = await Tool("get_my_lessons", new { from = input.StartsAt.AddDays(-1), to = input.StartsAt.AddDays(1) }); Assert.That(lessons.GetProperty("lessons").GetArrayLength(), Is.Zero);
        var availability = await Tool("find_available_lessons", new { input.BranchId, input.TeacherId, input.EnrollmentId, input.StudentId, input.VehicleId, category = input.Category, from = input.StartsAt, to = input.StartsAt.AddMinutes(90), durationMinutes = 45 }); Assert.That(availability.GetProperty("slots").GetArrayLength(), Is.GreaterThan(0));
        await using (var db = owners.OwnerDb())
        {
            var operation = await db.Set<SchoolOperation>().FirstAsync(o => o.SchoolId == school);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            db.Add(new StudentEnrollment { SchoolId = school, EnrollmentId = input.EnrollmentId!.Value, StudentId = input.StudentId!.Value, CategoryCode = "B", Status = EnrollmentStatus.Active, StartedAt = now, StatusChangedAt = now, Revision = 1, LastOperationId = operation.OperationId }); await db.SaveChangesAsync();
        }
        var progress = await Tool("get_student_training_progress", new { enrollmentId = input.EnrollmentId });
        Assert.That(progress.GetProperty("percent").ValueKind, Is.EqualTo(JsonValueKind.Null)); Assert.That(progress.GetProperty("sourcesVerified").GetBoolean(), Is.False); Assert.That(progress.GetProperty("state").GetString(), Is.EqualTo("AwaitingProgramme"));
        // Explicit pre-existing scheduled lesson fixture, after the zero-effects booking assertion.
        var original = new Lesson { Id = Guid.NewGuid(), SchoolId = school, BranchId = input.BranchId, TeacherId = input.TeacherId, EnrollmentId = input.EnrollmentId, StudentId = input.StudentId, VehicleId = input.VehicleId, CategoryCode = input.Category, Kind = LessonKind.Practical, Status = LessonStatus.Scheduled, Revision = 1, StartsAt = input.StartsAt, EndsAt = input.EndsAt, CreatedAt = fixture.Clock.Now };
        await using (var db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseScheduling(schedule.Owner).Options))
        {
            await db.Database.OpenConnectionAsync(); await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
            db.Add(original); await db.SaveChangesAsync();
        }
        var before = await SchedulingDatabase.Scalar(schedule.Owner, "SELECT (SELECT count(*) FROM lesson)+(SELECT count(*) FROM reservation_claim)+(SELECT count(*) FROM scheduling_operation)+(SELECT count(*) FROM outbox_message)");
        var move = await Tool("preview_lesson_rescheduling", new { lessonId = original.Id, enrollmentId = original.EnrollmentId, expectedVersion = 1, startsAt = original.StartsAt.AddMinutes(15), endsAt = original.EndsAt.AddMinutes(15), reason = "Synthetic advisory move" });
        Assert.That(move.GetProperty("executionAllowed").GetBoolean(), Is.False);
        Assert.That(await SchedulingDatabase.Scalar(schedule.Owner, "SELECT (SELECT count(*) FROM lesson)+(SELECT count(*) FROM reservation_claim)+(SELECT count(*) FROM scheduling_operation)+(SELECT count(*) FROM outbox_message)"), Is.EqualTo(before));
    }
    private sealed class LearningFactory(string root, OwnersFixture owners, LearningDatabase pg, OwnerRouting routing) : WebApplicationFactory<LearningApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root, "Vairas.LearningTracking", "Vairas.LearningTracking"));
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:LearningTracking"] = pg.Runtime, ["Dependencies:ClientSecret"] = owners.Secret, ["Dependencies:Scope"] = "identity.validate profiles.binding.read school.learning.read" }));
            b.ConfigureServices(s => { s.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(() => routing); foreach (var name in new[] { "User", "Workload" }) s.Configure<JwtBearerOptions>(name, o => o.ConfigurationManager = Metadata(owners.Auth)); });
        }
    }
    private sealed class LearningDatabase(string root) : IAsyncDisposable
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17").Build();
        public string Owner { get; private set; } = ""; public string Runtime { get; private set; } = "";
        public async Task Start()
        {
            await container.StartAsync(); var admin = container.GetConnectionString(); var secret = Guid.NewGuid().ToString("N");
            var roles = new Dictionary<string, string> { ["v_learning_tracking_migration"] = "cm_owner", ["v_learning_tracking_runtime"] = "cm_runtime", ["v_learning_tracking_delivery"] = "cm_delivery" };
            foreach (var role in roles.Values) await Sql(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await Sql(admin, "CREATE DATABASE mcp_learning OWNER cm_owner");
            string Connection(string role) => new NpgsqlConnectionStringBuilder(admin) { Database = "mcp_learning", Username = role, Password = secret }.ConnectionString;
            Owner = Connection("cm_owner"); Runtime = Connection("cm_runtime");
            await using (var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseLearning(Owner).Options)) await db.Database.MigrateAsync();
            foreach (var name in new[] { "canonical-roles.sql", "reliability-grants.sql" })
            {
                var sql = string.Join('\n', (await File.ReadAllLinesAsync(Path.Combine(root, "Vairas.LearningTracking", "database", name))).Where(line => !line.StartsWith('\\')));
                foreach (var pair in roles) sql = sql.Replace(pair.Key, pair.Value); await Sql(Owner, sql);
            }
        }
        private static async Task Sql(string connection, string sql) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync() { NpgsqlConnection.ClearAllPools(); await container.DisposeAsync(); }
    }
}
