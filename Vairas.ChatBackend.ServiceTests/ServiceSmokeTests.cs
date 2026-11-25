extern alias ChatApi;
extern alias ChatTests;
extern alias SchoolTests;
extern alias SchedulingApi;
extern alias GatewayApi;
extern alias McpApi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.OpenApi;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;
using Vairas.ChatBackend.Business;
using Vairas.Scheduling.Business;
using Vairas.Scheduling.Domain;
using Vairas.Scheduling.Persistance;
using Vairas.SchoolManagement.Domain;
using OwnersFixture = SchoolTests::Vairas.SchoolManagement.Tests.IntegrationEnvironment;
using PgFixture = ChatTests::Vairas.ChatBackend.Tests.ChatPostgres;
using ModelFixture = ChatTests::Vairas.ChatBackend.Tests.TestModel;
using SchedulingFixture = Vairas.Scheduling.Tests.TestWorld;

namespace Vairas.ChatBackend.ServiceTests;

[TestFixture, Category("Services"), NonParallelizable]
public sealed partial class ServiceSmokeTests
{
    [Test]
    public async Task Gateway_real_owners_and_PostgreSQL_record_intent_without_any_Scheduling_effect()
    {
        await using var owners = new OwnersFixture(); var postgres = new PgFixture(); await postgres.InitializeAsync();
        var bridges = new List<WebApplication>();
        try
        {
            owners.ConfigureAuth = c =>
            {
                c["Authentication:Delegation:Enabled"] = "true";
                c["Authentication:WorkloadClients:chat-backend:AllowTokenExchange"] = "true";
                c["Authentication:WorkloadClients:mcp-server:AllowTokenExchange"] = "true";
                c["Authentication:WorkloadClients:mcp-server:SecretHash"] = c["Authentication:WorkloadClients:school-management:SecretHash"];
                c["Authentication:WorkloadClients:mcp-server:Scopes:0"] = "identity.validate";
                c["Authentication:WorkloadClients:mcp-server:Scopes:1"] = "school.assistant.read";
                c["Authentication:WorkloadClients:scheduling:Scopes:2"] = "profiles.binding.read";
                c["Authentication:WorkloadClients:learning-tracking:SecretHash"] = c["Authentication:WorkloadClients:school-management:SecretHash"];
                c["Authentication:WorkloadClients:learning-tracking:Scopes:0"] = "identity.validate";
                c["Authentication:WorkloadClients:learning-tracking:Scopes:1"] = "profiles.binding.read";
                c["Authentication:WorkloadClients:learning-tracking:Scopes:2"] = "school.learning.read";
                c["Authentication:WorkloadClients:chat-backend:SecretHash"] = c["Authentication:WorkloadClients:school-management:SecretHash"];
                c["Authentication:WorkloadClients:chat-backend:Scopes:0"] = "identity.validate"; c["Authentication:WorkloadClients:chat-backend:Scopes:1"] = "school.assistant.read";
                c["Authentication:WorkloadClients:chat-evidence-read:SecretHash"] = c["Authentication:WorkloadClients:school-management:SecretHash"];
                c["Authentication:WorkloadClients:chat-evidence-read:Scopes:0"] = "confirmations.validate";
                c["Authentication:WorkloadClients:scheduling:Scopes:0"] = "identity.validate";
            };
            owners.ConfigureSchool = c => { c["Authentication:WorkloadClients:chat-backend:0"] = "school.assistant.read"; c["Authentication:WorkloadClients:mcp-server:0"] = "school.assistant.read"; c["Authentication:WorkloadClients:learning-tracking:0"] = "school.learning.read"; };
            owners.ConfigureUsers = s => s.Configure<Vairas.UserManagement.Configuration.Options.JwtOptions>(o => { o.WorkloadClients["scheduling"] = ["profiles.binding.read"]; o.WorkloadClients["learning-tracking"] = ["profiles.binding.read"]; });
            await owners.StartAsync();
            var root = OwnersFixture.Backend; var school = await owners.SeedSchool();
            await using var schedule = new SchedulingDatabase(root);
            await schedule.Start();
            var data = new SchedulingFixture { Decision = null, PlanOverride = null }; data.Clock.Now = DateTimeOffset.UtcNow;
            data.Actor = data.Actor with { UserId = owners.Administrator.UserId, ValidUntil = data.Clock.Now.AddSeconds(30) };
            foreach (var rule in data.Store.Rows.OfType<SchedulingRuleVersion>()) { rule.SchoolId = null; rule.CreatedAt = data.Clock.Now; rule.DecidedAt = data.Clock.Now; }
            await using (var db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseScheduling(schedule.Owner).Options))
            { db.AddRange(data.Store.Rows.OfType<SchedulingRuleVersion>()); await db.SaveChangesAsync(); }
            await using var schedulingFactory = new SchedulingFactory(root, owners, schedule, data);
            using var scheduling = schedulingFactory.CreateClient(new() { BaseAddress = new("https://localhost:7300") });
            var model = new ModelFixture();
            var routing = new OwnerRouting(owners);
            await using var learningDatabase = new LearningDatabase(root); await learningDatabase.Start();
            await using var learningFactory = new LearningFactory(root, owners, learningDatabase, routing);
            using var learning = learningFactory.CreateClient(new() { BaseAddress = new("https://localhost:7500") }); routing.Learning = learning;
            await using var mcpFactory = new McpFactory(root, owners, routing);
            using var mcp = mcpFactory.CreateClient(new() { BaseAddress = new("https://localhost:7900") }); routing.Mcp = mcp;
            await using var chatFactory = new ChatFactory(root, owners, postgres, model, routing);
            using var chat = chatFactory.CreateClient(new() { BaseAddress = new("https://localhost:7800") });
            var destinations = new Dictionary<string, string>();
            foreach (var (name, target) in new[] { ("authentication", owners.Auth), ("user-management", owners.Users), ("school-management", owners.Client), ("scheduling", scheduling), ("learning-tracking", learning), ("chat", chat) })
            { var bridge = await Bridge(target); bridges.Add(bridge); destinations[name] = Address(bridge) + "/"; }

            // Export exact owner applications, never a mock's route description.
            var specs = new Dictionary<string, string>();
            foreach (var (name, service, target) in new[] { ("authentication", "Authentication", owners.Auth), ("school-management", "SchoolManagement", owners.Client), ("user-management", "UserManagement", owners.Users), ("scheduling", "Scheduling", scheduling), ("learning-tracking", "LearningTracking", learning), ("chat", "ChatBackend", chat) })
            {
                using var response = await target.GetAsync("/openapi/v1.json"); var json = await response.Content.ReadAsStringAsync(); Assert.That(response.IsSuccessStatusCode, Is.True, json);
                var parsed = OpenApiDocument.Parse(json, "json"); Assert.That(parsed.Diagnostic!.Errors, Is.Empty); Assert.That(parsed.Document!.Validate(ValidationRuleSet.GetDefaultRuleSet()), Is.Empty);
                specs[name] = json;
                if (Environment.GetEnvironmentVariable("VAIRAS_CHAT_EXPORT_CONTRACTS") == "on")
                {
                    var stem = name == "chat" ? "chat" : name == "school-management" ? "school-management" : name;
                    var directory = Path.Combine(root, "Vairas." + service, "api"); Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, stem + ".openapi.json"), json);
                    await File.WriteAllTextAsync(Path.Combine(directory, stem + ".openapi.yaml"), await parsed.Document!.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi3_1));
                }
            }
            var gatewayRoot = Path.Combine(root, "Vairas.Gateway", "Vairas.Gateway");
            var catalog = JsonSerializer.Deserialize<ProxyCatalog>(await File.ReadAllTextAsync(Path.Combine(gatewayRoot, "routes.json")))!.PublicRoutes.ToList();
            foreach (var name in new[] { "chat", "scheduling" })
            {
                using var spec = JsonDocument.Parse(specs[name]);
                foreach (var path in spec.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/v1/", StringComparison.Ordinal)))
                foreach (var operation in path.Value.EnumerateObject().Where(p => p.Name is "get" or "post" or "put" or "patch" or "delete"))
                    if (!catalog.Any(r => r.Method == operation.Name.ToUpperInvariant() && r.Path == path.Name)) catalog.Add(new(name + "." + operation.Value.GetProperty("operationId").GetString() + "." + operation.Name, name, operation.Name.ToUpperInvariant(), path.Name));
            }
            var routeConfiguration = new Dictionary<string, string?>();
            for (var i = 0; i < catalog.Count; i++)
            {
                var r = catalog[i]; var prefix = $"PublicRoutes:{i}:";
                routeConfiguration[prefix + "Id"] = r.Id; routeConfiguration[prefix + "Service"] = r.Service; routeConfiguration[prefix + "Method"] = r.Method; routeConfiguration[prefix + "Path"] = r.Path;
                routeConfiguration[prefix + "Anonymous"] = r.Anonymous.ToString(); routeConfiguration[prefix + "FileTransfer"] = r.FileTransfer.ToString(); routeConfiguration[prefix + "Upload"] = r.Upload.ToString();
            }
            await using var gateway = GatewayApi::Vairas.Gateway.GatewayApplication.Build(["--environment", "Development"], b =>
            {
                b.Configuration.Sources.Clear(); b.Configuration.AddJsonFile(Path.Combine(gatewayRoot, "appsettings.json")).AddInMemoryCollection(routeConfiguration);
                foreach (var p in destinations) b.Configuration[$"Gateway:Services:{p.Key}:Address"] = p.Value;
                b.Configuration["AllowedHosts"] = "127.0.0.1"; b.WebHost.UseUrls("http://127.0.0.1:0");
                foreach (var service in GatewayApi::Vairas.Gateway.Configuration.ProxyConfiguration.ServiceNames) b.Services.Configure<JwtBearerOptions>(service, o => o.ConfigurationManager = Metadata(owners.Auth));
            });
            await gateway.StartAsync(); using var client = new HttpClient { BaseAddress = new(Address(gateway)) }; routing.Gateway = client;
            var token = owners.Administrator.Tokens.AccessToken;
            var session = await Json(client, HttpMethod.Post, "/api/v1/chat/sessions", token, new { schoolId = school }, HttpStatusCode.Created); var conversation = session.GetProperty("sessionId").GetGuid();
            model.Turns.Enqueue(new(null, [new("enrollments", "get_my_enrollments", Vairas.ChatBackend.Business.Wire.Element(new { }))]));
            await Json(client, HttpMethod.Post, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", token, new { text = "Read my enrollments", schoolId = school });
            Assert.That(model.Prompts[1].Last().Text, Does.Contain("enrollments"));
            var booking = data.Practical() with { SchoolId = school };
            model.Turns.Enqueue(new(null, [new("preview", "preview_lesson_booking", Vairas.ChatBackend.Business.Wire.Element(new { schoolId = school, booking.BranchId, booking.EnrollmentId, booking.StudentId, booking.TeacherId, booking.VehicleId, booking.Category, booking.StartsAt, booking.EndsAt }))]));
            var reply = await Json(client, HttpMethod.Post, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", token, new { text = "Prepare exact booking", schoolId = school });
            var previewId = reply.GetProperty("previewId").GetGuid();
            var preview = await Json(client, HttpMethod.Get, $"/api/v1/chat/action-previews/{previewId}?schoolId={school}", token);
            var operationId = preview.GetProperty("operationId").GetGuid(); var digest = preview.GetProperty("argumentsFingerprint").GetString()!;
            var approval = await Json(client, HttpMethod.Post, $"/api/v1/chat/action-previews/{previewId}/confirmations?schoolId={school}", token, new { operationId, argumentsFingerprint = digest, approved = true }, operation: operationId, origin: "https://localhost:4200");
            Assert.That(approval.GetProperty("executionAllowed").GetBoolean(), Is.False);
            var evidenceToken = await owners.Workload("chat-evidence-read", "confirmations.validate");
            var evidence = await Json(chat, HttpMethod.Post, "/internal/v1/chat/confirmations/validate", evidenceToken, new ConfirmationQuery(school, conversation, previewId, owners.Administrator.Tokens.IdentityId, operationId, digest));
            Assert.That(evidence.GetProperty("confirmed").GetBoolean(), Is.True); Assert.That(evidence.GetProperty("executionAllowed").GetBoolean(), Is.False);
            await Json(client, HttpMethod.Post, "/internal/v1/chat/confirmations/validate", evidenceToken, new { }, HttpStatusCode.NotFound);
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner, "SELECT (SELECT count(*) FROM lesson)+(SELECT count(*) FROM reservation_claim)+(SELECT count(*) FROM scheduling_operation)+(SELECT count(*) FROM outbox_message)"), Is.EqualTo(0L));
            await DelegationWriteDenials(client, scheduling, owners, school, token);
            var audit = await Json(client, HttpMethod.Get, $"/api/v1/schools/{school}/assistant-audit", token); Assert.That(audit.GetArrayLength(), Is.EqualTo(1)); Assert.That(audit.GetRawText(), Does.Not.Contain("arguments").And.Not.Contain("text"));
            await Json(client, HttpMethod.Get, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", owners.Outsider.Tokens.AccessToken, expected: HttpStatusCode.NotFound);
            await RemainingTools(client, owners, schedule, data, school, conversation, model, token);
            // Explicit existing-member fixtures exercise actual User/School admission changes.
            var targetMembership = Guid.NewGuid();
            await using (var db = owners.OwnerDb())
            {
                var basis = await db.Set<SchoolOperation>().FirstAsync(o => o.SchoolId == school);
                db.Add(new SchoolMembership { SchoolId = school, MembershipId = targetMembership, UserId = owners.Target.UserId, Role = SchoolRole.Student, Revision = 1, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), LastOperationId = basis.OperationId });
                await db.SaveChangesAsync();
            }
            var targetToken = owners.Target.Tokens.AccessToken;
            var targetSession = (await Json(client, HttpMethod.Post, "/api/v1/chat/sessions", targetToken, new { schoolId = school }, HttpStatusCode.Created)).GetProperty("sessionId").GetGuid();
            await OwnersFixture.Sql(owners.UserOwner, $"UPDATE user_profile SET status='Inactive',revision=revision+1,updated_at=clock_timestamp() WHERE user_id='{owners.Target.UserId}'");
            await Json(client, HttpMethod.Get, $"/api/v1/chat/sessions/{targetSession}?schoolId={school}", targetToken, expected: HttpStatusCode.Forbidden);
            await OwnersFixture.Sql(owners.UserOwner, $"UPDATE user_profile SET status='Active',revision=revision+1,updated_at=clock_timestamp() WHERE user_id='{owners.Target.UserId}'");
            await using (var db = owners.OwnerDb())
            {
                var member = await db.Set<SchoolMembership>().SingleAsync(m => m.MembershipId == targetMembership); member.EndedAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(); member.Revision++; await db.SaveChangesAsync();
            }
            await Json(client, HttpMethod.Get, $"/api/v1/chat/sessions/{targetSession}?schoolId={school}", targetToken, expected: HttpStatusCode.NotFound);
            await using (var db = owners.OwnerDb())
            {
                var basis = await db.Set<SchoolOperation>().FirstAsync(o => o.SchoolId == school);
                var rejoinedAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
                db.Add(new SchoolMembership { SchoolId = school, MembershipId = Guid.NewGuid(), UserId = owners.Target.UserId, Role = SchoolRole.Student, Revision = 1, StartedAt = rejoinedAt, LastOperationId = basis.OperationId }); await db.SaveChangesAsync();
            }
            await Json(client, HttpMethod.Get, $"/api/v1/chat/sessions/{targetSession}?schoolId={school}", targetToken, expected: HttpStatusCode.NotFound);
            await Json(client, HttpMethod.Post, "/api/v1/chat/sessions", targetToken, new { schoolId = school }, HttpStatusCode.Created);
            await Json(client, HttpMethod.Post, $"/api/v1/auth/sessions/{owners.Administrator.Tokens.SessionId}/revocation", token, new { });
            await Json(client, HttpMethod.Get, $"/api/v1/chat/sessions/{conversation}?schoolId={school}", token, expected: HttpStatusCode.Unauthorized);
            TestContext.Out.WriteLine("Real Chat -> MCP SDK -> Gateway -> Auth/User/School/Scheduling/Learning API pipelines; six PostgreSQL databases. All six read/preview tools executed. Separate two-hop RS256 tokens, current School access and recorded consent checked. Scheduling resource authority and model are explicit fixtures. No business dispatch or live cloud/Vehicle provider.");
        }
        finally { foreach (var bridge in bridges) await bridge.DisposeAsync(); await postgres.DisposeAsync(); }
    }
    private sealed record ProxyCatalog(GatewayApi::Vairas.Gateway.Configuration.Options.PublicRoute[] PublicRoutes);
    private static async Task<JsonElement> Json(HttpClient client, HttpMethod method, string path, string token, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, Guid? operation = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new("Bearer", token);
        if (body is not null) { request.Content = JsonContent.Create(body); request.Headers.Add("Idempotency-Key", (operation ?? Guid.NewGuid()).ToString()); }
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync(); Assert.That(response.StatusCode, Is.EqualTo(expected), text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    private static ConfigurationManager<OpenIdConnectConfiguration> Metadata(HttpClient auth) => new("https://localhost:7100/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(auth) { RequireHttps = true });
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task<WebApplication> Bridge(HttpClient target)
    {
        var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.Configuration["AllowedHosts"] = "127.0.0.1"; b.WebHost.UseUrls("http://127.0.0.1:0"); var app = b.Build();
        app.Run(async http =>
        {
            using var request = new HttpRequestMessage(new(http.Request.Method), new Uri(target.BaseAddress!, http.Request.Path + http.Request.QueryString));
            if (http.Request.ContentLength > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(http.Request.Body);
            foreach (var h in http.Request.Headers) if (h.Key is not ("Host" or "Connection" or "Transfer-Encoding" or "Content-Length") && !request.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            using var response = await target.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted); http.Response.StatusCode = (int)response.StatusCode;
            foreach (var h in response.Headers.Concat(response.Content.Headers)) if (h.Key is not ("Transfer-Encoding" or "Connection")) http.Response.Headers[h.Key] = h.Value.ToArray();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }); await app.StartAsync(); return app;
    }
    private sealed class OwnerRouting(OwnersFixture owners) : HttpMessageHandler
    {
        public HttpClient? Gateway { get; set; }
        public HttpClient? Mcp { get; set; }
        public HttpClient? Learning { get; set; }
        public HttpClient? Chat { get; set; }
        public HttpClient? Scheduling { get; set; }
        public HttpClient? Vehicle { get; set; }
        public bool DropExecutionResponse { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var client = request.RequestUri!.Port switch { 7100 => owners.Auth, 7200 => owners.Client, 7001 => owners.Users, 7300 => Scheduling ?? throw new InvalidOperationException("Scheduling not attached"), 7400 => Vehicle ?? throw new InvalidOperationException("Vehicle not attached"), 7800 => Chat ?? throw new InvalidOperationException("Chat not attached"), 7500 => Learning ?? throw new InvalidOperationException("Learning not attached"), 7900 => Mcp ?? throw new InvalidOperationException("MCP not attached"), 7000 => Gateway ?? throw new InvalidOperationException("Gateway not attached"), _ => throw new InvalidOperationException("Unexpected owner") };
            using var copy = new HttpRequestMessage(request.Method, new Uri(client.BaseAddress!, request.RequestUri.PathAndQuery));
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.Content is not null) { copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct)); foreach (var h in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value); }
            var response = await client.SendAsync(copy, ct);
            if (DropExecutionResponse && request.RequestUri.AbsolutePath.Contains("/confirmed-", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            { DropExecutionResponse = false; response.Dispose(); throw new HttpRequestException("Synthetic lost response after owner commit"); }
            return response;
        }
    }
    private sealed class ChatFactory(string root, OwnersFixture owners, PgFixture pg, ModelFixture model, OwnerRouting routing) : WebApplicationFactory<ChatApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            var directory = Path.Combine(root, "Vairas.ChatBackend", "Vairas.ChatBackend"); b.UseEnvironment("Testing").UseContentRoot(directory);
            b.ConfigureAppConfiguration((_, c) => { c.Sources.Clear(); c.AddJsonFile(Path.Combine(directory, "appsettings.json")).AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Chat"] = pg.Runtime, ["Store:DeploymentId"] = pg.Deployment.ToString(), ["Dependencies:ClientSecret"] = owners.Secret, ["Authentication:WorkloadClients:chat-evidence-read:0"] = "confirmations.validate" }); });
            b.ConfigureServices(s => { s.AddSingleton(new ChatApi::Vairas.ChatBackend.Configuration.Options.McpOptions { Enabled = true }); s.AddSingleton<IModelAdapter>(model); s.AddHttpClient("owners").ConfigurePrimaryHttpMessageHandler(() => routing); s.AddHttpClient("mcp").ConfigurePrimaryHttpMessageHandler(() => routing); foreach (var name in new[] { "User", "Workload" }) s.Configure<JwtBearerOptions>(name, o => o.ConfigurationManager = Metadata(owners.Auth)); });
        }
    }
    private sealed class McpFactory(string root, OwnersFixture owners, OwnerRouting routing) : WebApplicationFactory<McpApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root, "Vairas.McpServer", "Vairas.McpServer"));
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Dependencies:ClientSecret"] = owners.Secret }));
            b.ConfigureServices(s => { s.AddHttpClient("owners").ConfigurePrimaryHttpMessageHandler(() => routing); s.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.ConfigurationManager = Metadata(owners.Auth)); });
        }
    }
    private sealed class SchedulingFactory(string root, OwnersFixture owners, SchedulingDatabase pg, SchedulingFixture fixture) : WebApplicationFactory<SchedulingApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root, "Vairas.Scheduling", "Vairas.Scheduling"));
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Scheduling"] = pg.Runtime, ["ConnectionStrings:ReservationAuthority"] = pg.Authority, ["Scheduling:SyntheticMode"] = "true", ["Dependencies:ClientSecret"] = owners.Secret, ["Dependencies:Scope"] = "identity.validate school.participants.read profiles.binding.read" }));
            b.ConfigureServices(s => { s.AddSingleton<TimeProvider>(fixture.Clock); s.AddSingleton<IOwners>(fixture); s.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(() => new OwnerRouting(owners)); foreach (var name in new[] { "User", "Workload" }) s.Configure<JwtBearerOptions>(name, o => o.ConfigurationManager = Metadata(owners.Auth)); });
        }
    }
    private sealed class SchedulingDatabase(string root) : IAsyncDisposable
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17").Build();
        public string Owner { get; private set; } = ""; public string Runtime { get; private set; } = ""; public string Authority { get; private set; } = "";
        public async Task Start()
        {
            await container.StartAsync(); var admin = container.GetConnectionString(); var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var roles = new Dictionary<string, string> { ["v_scheduling_migration"] = "cs_owner", ["v_scheduling_runtime"] = "cs_runtime", ["v_scheduling_reservation_authority"] = "cs_authority", ["v_scheduling_outbox"] = "cs_outbox" };
            foreach (var role in roles.Values) await Sql(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await Sql(admin, "CREATE DATABASE chat_scheduling OWNER cs_owner");
            string Connection(string role) => new NpgsqlConnectionStringBuilder(admin) { Database = "chat_scheduling", Username = role, Password = secret }.ConnectionString;
            Owner = Connection("cs_owner"); Runtime = Connection("cs_runtime"); Authority = Connection("cs_authority");
            await using (var db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseScheduling(Owner).Options)) await db.Database.MigrateAsync();
            foreach (var file in new[] { "database-roles.sql", "reliability-grants.sql", "review-grants.sql" }) { var sql = await File.ReadAllTextAsync(Path.Combine(root, "Vairas.Scheduling", "database", file)); foreach (var pair in roles) sql = sql.Replace(pair.Key, pair.Value); await Sql(Owner, sql); }
        }
        private static async Task Sql(string connection, string sql) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
        public static async Task<long> Scalar(string connection, string sql) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); return (long)(await command.ExecuteScalarAsync())!; }
        public async ValueTask DisposeAsync() { NpgsqlConnection.ClearAllPools(); await container.DisposeAsync(); }
    }
}
