extern alias ChatApi;
extern alias ChatTests;
extern alias SchoolTests;
extern alias SchedulingApi;
extern alias GatewayApi;
extern alias McpApi;
extern alias VehicleApi;
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
using Microsoft.OpenApi;
using NUnit.Framework;
using Testcontainers.PostgreSql;
using Vairas.ChatBackend.Business;
using Vairas.Scheduling.Domain;
using Vairas.Scheduling.Persistance;
using Vairas.VehicleManagement.Persistance;
using VehicleDb = Vairas.VehicleManagement.Persistance.VehicleDbContext;
using OwnersFixture = SchoolTests::Vairas.SchoolManagement.Tests.IntegrationEnvironment;
using PgFixture = ChatTests::Vairas.ChatBackend.Tests.ChatPostgres;
using ModelFixture = ChatTests::Vairas.ChatBackend.Tests.TestModel;
using SchedulingFixture = Vairas.Scheduling.Tests.TestWorld;
namespace Vairas.ChatBackend.ServiceTests;

public sealed partial class ServiceSmokeTests
{
    [Test]
    public async Task Confirmed_booking_and_move_use_real_owners_and_recover_lost_commit_acknowledgement()
    {
        await using var owners=new OwnersFixture();var chatDb=new PgFixture();await chatDb.InitializeAsync();
        var bridges=new List<Microsoft.AspNetCore.Builder.WebApplication>();
        try
        {
            owners.ConfigureAuth=c=>
            {
                c["Authentication:Delegation:Enabled"]="true";c["Authentication:Delegation:ConfirmedLessonActionsEnabled"]="true";
                var clients=new Dictionary<string,string[]> {
                    ["chat-backend"]=["identity.validate","school.assistant.read","mcp.lesson-actions.reconcile"],
                    ["mcp-server"]=["identity.validate","school.assistant.read","scheduling.lesson-actions.reconcile"],
                    ["scheduling"]=["identity.validate","profiles.binding.read","school.scheduling.read","school.coordination.read","vehicles.eligibility.read","chat.lesson-actions.authorize"],
                    ["vehicle-management"]=["identity.validate","profiles.status.read","profiles.binding.read","school.vehicle-authority.read","scheduling.vehicle-gate"] };
                foreach(var pair in clients) { c[$"Authentication:WorkloadClients:{pair.Key}:SecretHash"]=c["Authentication:WorkloadClients:school-management:SecretHash"];for(var i=0;i<pair.Value.Length;i++)c[$"Authentication:WorkloadClients:{pair.Key}:Scopes:{i}"]=pair.Value[i]; }
                c["Authentication:WorkloadClients:chat-backend:AllowTokenExchange"]="true";c["Authentication:WorkloadClients:mcp-server:AllowTokenExchange"]="true";
                c["Authentication:WorkloadClients:school-management:Scopes:3"]="scheduling.school-gate";
            };
            owners.ConfigureSchool=c=>
            {
                c["SchedulingDependency:Enabled"]="true";c["Dependencies:Scope"]="identity.validate profiles.status.read profiles.binding.read scheduling.school-gate";
                c["Authentication:WorkloadClients:chat-backend:0"]="school.assistant.read";c["Authentication:WorkloadClients:mcp-server:0"]="school.assistant.read";
                c["Authentication:WorkloadClients:scheduling:2"]="school.scheduling.read";c["Authentication:WorkloadClients:scheduling:3"]="school.coordination.read";
            };
            owners.ConfigureUsers=s=>s.Configure<Vairas.UserManagement.Configuration.Options.JwtOptions>(o=>{o.WorkloadClients["scheduling"]=["profiles.binding.read"];o.WorkloadClients["vehicle-management"]=["profiles.binding.read","profiles.status.read"];});
            await owners.StartAsync();var root=OwnersFixture.Backend;var school=await owners.SeedSchool();var token=owners.Administrator.Tokens.AccessToken;
            await using var schedule=new SchedulingDatabase(root);await schedule.Start();
            var fixture=new SchedulingFixture();fixture.Clock.Now=DateTimeOffset.UtcNow;
            await using(var db=new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseScheduling(schedule.Owner).Options))
            {foreach(var rule in fixture.Store.Rows.OfType<SchedulingRuleVersion>()){rule.SchoolId=null;rule.CreatedAt=fixture.Clock.Now;rule.DecidedAt=fixture.Clock.Now;db.Add(rule);}await db.SaveChangesAsync();}
            var routing=new OwnerRouting(owners);
            await using var vehicleDb=new VehicleDatabase(root);await vehicleDb.Start();
            await using var vehicleFactory=new ActualVehicleFactory(root,owners,vehicleDb,routing);
            using var vehicle=vehicleFactory.CreateClient(new(){BaseAddress=new("https://localhost:7400")});routing.Vehicle=vehicle;
            await using var schedulingFactory=new ConfirmedSchedulingFactory(root,owners,schedule,routing);
            using var scheduling=schedulingFactory.CreateClient(new(){BaseAddress=new("https://localhost:7300")});routing.Scheduling=scheduling;owners.Scheduling=scheduling;
            await using var mcpFactory=new ConfirmedMcpFactory(root,owners,routing);
            using var mcp=mcpFactory.CreateClient(new(){BaseAddress=new("https://localhost:7900")});routing.Mcp=mcp;
            var model=new ModelFixture();await using var chatFactory=new ConfirmedChatFactory(root,owners,chatDb,model,routing);
            using var chat=chatFactory.CreateClient(new(){BaseAddress=new("https://localhost:7800")});routing.Chat=chat;
            var destinations=new Dictionary<string,string>();
            foreach(var (name,target) in new[]{("authentication",owners.Auth),("user-management",owners.Users),("school-management",owners.Client),("scheduling",scheduling),("vehicle-management",vehicle),("chat",chat)})
            {var bridge=await Bridge(target);bridges.Add(bridge);destinations[name]=Address(bridge)+"/";}
            var gatewayRoot=Path.Combine(root,"Vairas.Gateway","Vairas.Gateway");
            await using var gateway=GatewayApi::Vairas.Gateway.GatewayApplication.Build(["--environment","Development"],b=>
            {
                b.Configuration.Sources.Clear();b.Configuration.AddJsonFile(Path.Combine(gatewayRoot,"appsettings.json")).AddJsonFile(Path.Combine(gatewayRoot,"routes.json"));
                foreach(var p in destinations)b.Configuration[$"Gateway:Services:{p.Key}:Address"]=p.Value;
                b.Configuration["AllowedHosts"]="127.0.0.1";b.WebHost.UseUrls("http://127.0.0.1:0");
                foreach(var service in GatewayApi::Vairas.Gateway.Configuration.ProxyConfiguration.ServiceNames)b.Services.Configure<JwtBearerOptions>(service,o=>o.ConfigurationManager=Metadata(owners.Auth));
            });
            await gateway.StartAsync();using var client=new HttpClient{BaseAddress=new(Address(gateway))};routing.Gateway=client;

            if(Environment.GetEnvironmentVariable("VAIRAS_CHAT_EXPORT_CONTRACTS")=="on")
            {
                foreach(var (service,stem,target) in new[]{("Authentication","authentication",owners.Auth),("UserManagement","user-management",owners.Users),("SchoolManagement","school-management",owners.Client),("VehicleManagement","vehicle-management",vehicle),("Scheduling","scheduling",scheduling),("ChatBackend","chat",chat)})
                {
                    using var response=await target.GetAsync("/openapi/v1.json");var json=await response.Content.ReadAsStringAsync();Assert.That(response.IsSuccessStatusCode,Is.True,json);
                    var parsed=OpenApiDocument.Parse(json,"json");Assert.That(parsed.Diagnostic!.Errors,Is.Empty);Assert.That(parsed.Document!.Validate(ValidationRuleSet.GetDefaultRuleSet()),Is.Empty);
                    var directory=Path.Combine(root,"Vairas."+service,"api");Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory,stem+".openapi.json"),json);
                    await File.WriteAllTextAsync(Path.Combine(directory,stem+".openapi.yaml"),await parsed.Document!.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi3_1));
                }
            }
            var invitation=await Json(owners.Client,HttpMethod.Post,$"/api/v1/schools/{school}/invitations",token,new {targetUserId=owners.Target.UserId,role="Teacher"},HttpStatusCode.Created);
            var invitationId=invitation.GetProperty("resource").GetProperty("invitationId").GetGuid();
            await Json(owners.Users,HttpMethod.Post,"/api/v1/users/me/teacher-profile",owners.Target.Tokens.AccessToken,new{schoolId=school,invitationId},HttpStatusCode.Created);
            await Json(owners.Client,HttpMethod.Post,$"/api/v1/schools/{school}/invitations/{invitationId}/accept",owners.Target.Tokens.AccessToken,new{expectedRevision=1});
            var teacher=(await Json(owners.Users,HttpMethod.Get,"/api/v1/users/me",owners.Target.Tokens.AccessToken)).GetProperty("teacherId").GetGuid();
            await Json(owners.Users,HttpMethod.Post,"/api/v1/users/me/student-profile",owners.Outsider.Tokens.AccessToken,new{},HttpStatusCode.Created);
            var student=(await Json(owners.Users,HttpMethod.Get,"/api/v1/users/me",owners.Outsider.Tokens.AccessToken)).GetProperty("studentId").GetGuid();
            var enrollment=(await Json(owners.Client,HttpMethod.Post,$"/api/v1/schools/{school}/enrollments",token,new{studentId=student,licenseCategory="B"})).GetProperty("resultId").GetGuid();
            var branch=(await Json(owners.Client,HttpMethod.Post,$"/api/v1/schools/{school}/branches",token,new{name="Synthetic booking branch"},HttpStatusCode.Created)).GetProperty("resource").GetProperty("branchId").GetGuid();
            var createdVehicle=await Json(vehicle,HttpMethod.Post,$"/api/v1/schools/{school}/vehicles",token,new{branchId=branch,registrationCountryCode="LT",registrationNumber="SYN"+Guid.NewGuid().ToString("N")[..8],manufacturer="Synthetic",model="Training",manufactureYear=2020,transmission="Manual",categories=new[]{"B"}},HttpStatusCode.Created);
            var vehicleId=createdVehicle.GetProperty("resource").GetProperty("vehicleId").GetGuid();
            await Json(vehicle,HttpMethod.Put,$"/api/v1/schools/{school}/vehicles/{vehicleId}/teacher-assignments",token,new{expectedRevision=1,teacherIds=new[]{teacher}});
            var activation=await Json(vehicle,HttpMethod.Post,$"/api/v1/vehicles/{vehicleId}/state-change-operations?schoolId={school}",token,new{operationType="Reactivate",expectedVehicleRevision=2,reason="Synthetic initial activation"},HttpStatusCode.Accepted);
            var activationId=activation.GetProperty("operationId").GetGuid();
            for(var i=0;i<6;i++){using var scope=vehicleFactory.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<Vairas.VehicleManagement.Business.IStateChangeProcessor>().ProcessAsync(new(school,activationId),default);}
            var currentVehicle=await Json(vehicle,HttpMethod.Get,$"/api/v1/schools/{school}/vehicles/{vehicleId}",token);Assert.That(currentVehicle.GetProperty("status").GetString(),Is.EqualTo("Active"));

            var conversation=(await Json(client,HttpMethod.Post,"/api/v1/chat/sessions",token,new{schoolId=school,executionProfile="ConfirmedLessonActionsV1"},HttpStatusCode.Created)).GetProperty("sessionId").GetGuid();
            async Task<JsonElement> Preview(string tool,object args)
            {
                model.Turns.Enqueue(new(null,[new("preview",tool,Vairas.ChatBackend.Business.Wire.Element(args))]));
                var reply=await Json(client,HttpMethod.Post,$"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}",token,new{text="Prepare exact action"});
                return await Json(client,HttpMethod.Get,$"/api/v1/chat/action-previews/{reply.GetProperty("previewId").GetGuid()}?schoolId={school}",token);
            }
            async Task<JsonElement> Execute(JsonElement preview,HttpStatusCode expected)
            {
                var id=preview.GetProperty("previewId").GetGuid();var operation=preview.GetProperty("operationId").GetGuid();
                return await Json(client,HttpMethod.Post,$"/api/v1/chat/action-previews/{id}/executions?schoolId={school}",token,new{operationId=operation,argumentsFingerprint=preview.GetProperty("argumentsFingerprint").GetString(),approved=true},expected,operation,"https://localhost:4200");
            }
            var start=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(9),TimeSpan.Zero);
            var booking=await Preview("preview_lesson_booking",new{branchId=branch,enrollmentId=enrollment,studentId=student,teacherId=teacher,vehicleId,category="B",startsAt=start,endsAt=start.AddMinutes(45)});
            var booked=await Execute(booking,HttpStatusCode.Created);Assert.That(booked.GetProperty("status").GetString(),Is.EqualTo("Committed"));
            var lesson=booked.GetProperty("lessonId").GetGuid();Assert.That((await Execute(booking,HttpStatusCode.Created)).GetProperty("lessonId").GetGuid(),Is.EqualTo(lesson));
            var move=await Preview("preview_lesson_rescheduling",new{lessonId=lesson,enrollmentId=enrollment,expectedVersion=1,startsAt=start.AddHours(3),endsAt=start.AddHours(3).AddMinutes(45),reason="Exact human-confirmed move"});
            routing.DropExecutionResponse=true;
            var uncertain=await Execute(move,HttpStatusCode.Accepted);Assert.That(uncertain.GetProperty("status").GetString(),Is.EqualTo("Unknown"));
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner,$"SELECT revision::bigint FROM lesson WHERE id='{lesson}'"),Is.EqualTo(2L));
            using(var scope=chatFactory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<ChatService>().ReconcileExecutionsAsync(default);
            var status=await Json(client,HttpMethod.Get,$"/api/v1/chat/action-previews/{move.GetProperty("previewId").GetGuid()}/execution?schoolId={school}",token);
            Assert.That(status.GetProperty("status").GetString(),Is.EqualTo("Committed"));Assert.That(status.GetProperty("version").GetInt32(),Is.EqualTo(2));
            await Execute(move,HttpStatusCode.OK);
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner,"SELECT count(*) FROM lesson"),Is.EqualTo(1L));
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner,"SELECT count(*) FROM scheduling_operation WHERE status='Committed' AND user_confirmed_at IS NOT NULL"),Is.EqualTo(2L));
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner,"SELECT count(*) FROM outbox_message"),Is.EqualTo(2L));
            // Vehicle's actual gate must reject deactivation while this future booking exists.
            var deactivation=await Json(vehicle,HttpMethod.Post,$"/api/v1/vehicles/{vehicleId}/state-change-operations?schoolId={school}",token,new{operationType="Deactivate",expectedVehicleRevision=currentVehicle.GetProperty("revision").GetInt64(),reason="Synthetic gate check"},HttpStatusCode.Accepted);
            for(var i=0;i<3;i++){using var scope=vehicleFactory.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<Vairas.VehicleManagement.Business.IStateChangeProcessor>().ProcessAsync(new(school,deactivation.GetProperty("operationId").GetGuid()),default);}
            Assert.That(await SchedulingDatabase.Scalar(schedule.Owner,"SELECT count(*) FROM gate_command WHERE result_json LIKE '%FUTURE_LESSONS_EXIST%'"),Is.EqualTo(1L));
            TestContext.Out.WriteLine("Real Auth/User/School/Vehicle/Scheduling/Chat/MCP/Gateway pipelines and six PostgreSQL databases; creation, exact replay, move, lost commit acknowledgement recovery and Vehicle gate rejection. Only the language model and legal-rule seed data are synthetic fixtures.");
        }
        finally {foreach(var bridge in bridges)await bridge.DisposeAsync();await chatDb.DisposeAsync();}
    }
    private sealed class ConfirmedChatFactory(string root,OwnersFixture owners,PgFixture pg,ModelFixture model,OwnerRouting routing):WebApplicationFactory<ChatApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            var directory=Path.Combine(root,"Vairas.ChatBackend","Vairas.ChatBackend");b.UseEnvironment("Testing").UseContentRoot(directory);
            b.ConfigureAppConfiguration((_,c)=>{c.Sources.Clear();c.AddJsonFile(Path.Combine(directory,"appsettings.json")).AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Chat"]=pg.Runtime,["Store:DeploymentId"]=pg.Deployment.ToString(),["Dependencies:ClientSecret"]=owners.Secret,["Chat:ConfirmedLessonActionsEnabled"]="true",["Chat:RecoveryWorkerEnabled"]="false",["Authentication:WorkloadClients:scheduling:0"]="chat.lesson-actions.authorize"});});
            b.ConfigureServices(s=>{s.AddSingleton(new ChatApi::Vairas.ChatBackend.Configuration.Options.McpOptions{Enabled=true});s.AddSingleton<IModelAdapter>(model);s.AddHttpClient("owners").ConfigurePrimaryHttpMessageHandler(()=>routing);s.AddHttpClient("mcp").ConfigurePrimaryHttpMessageHandler(()=>routing);foreach(var name in new[]{"User","Workload"})s.Configure<JwtBearerOptions>(name,o=>o.ConfigurationManager=Metadata(owners.Auth));});
        }
    }
    private sealed class ConfirmedMcpFactory(string root,OwnersFixture owners,OwnerRouting routing):WebApplicationFactory<McpApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root,"Vairas.McpServer","Vairas.McpServer"));
            b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["Dependencies:ClientSecret"]=owners.Secret,["Mcp:ConfirmedLessonActionsEnabled"]="true"}));
            b.ConfigureServices(s=>{s.AddHttpClient("owners").ConfigurePrimaryHttpMessageHandler(()=>routing);foreach(var name in new[]{JwtBearerDefaults.AuthenticationScheme,"Recovery"})s.Configure<JwtBearerOptions>(name,o=>o.ConfigurationManager=Metadata(owners.Auth));});
        }
    }
    private sealed class ConfirmedSchedulingFactory(string root,OwnersFixture owners,SchedulingDatabase pg,OwnerRouting routing):WebApplicationFactory<SchedulingApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root,"Vairas.Scheduling","Vairas.Scheduling"));
            b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Scheduling"]=pg.Runtime,["ConnectionStrings:ReservationAuthority"]=pg.Authority,["Scheduling:SyntheticMode"]="true",["Scheduling:ConfirmedLessonActionsEnabled"]="true",["Dependencies:VehicleBaseUrl"]="https://localhost:7400/",["Dependencies:ChatBaseUrl"]="https://localhost:7800/",["Dependencies:ClientSecret"]=owners.Secret,["Dependencies:Scope"]="identity.validate profiles.binding.read school.scheduling.read school.coordination.read vehicles.eligibility.read chat.lesson-actions.authorize",["Authentication:WorkloadClients:school-management:0"]="scheduling.school-gate",["Authentication:WorkloadClients:vehicle-management:0"]="scheduling.vehicle-gate",["Authentication:WorkloadClients:mcp-server:0"]="scheduling.lesson-actions.reconcile"}));
            b.ConfigureServices(s=>{s.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(()=>routing);foreach(var name in new[]{"User","Workload"})s.Configure<JwtBearerOptions>(name,o=>o.ConfigurationManager=Metadata(owners.Auth));});
        }
    }
    private sealed class ActualVehicleFactory(string root,OwnersFixture owners,VehicleDatabase pg,OwnerRouting routing):WebApplicationFactory<VehicleApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing").UseContentRoot(Path.Combine(root,"Vairas.VehicleManagement","Vairas.VehicleManagement"));
            b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:VehicleManagement"]=pg.Runtime,["Dependencies:ClientSecret"]=owners.Secret,["Dependencies:SchedulingBaseUrl"]="https://localhost:7300/",["Dependencies:Scope"]="identity.validate profiles.status.read profiles.binding.read school.vehicle-authority.read scheduling.vehicle-gate",["Scheduling:Enabled"]="true",["Workers:Enabled"]="false",["Catalogue:Path"]=Path.Combine(root,"Vairas.VehicleManagement","Vairas.VehicleManagement","catalogue","demo-categories.json"),["Authentication:WorkloadClients:scheduling:0"]="vehicles.eligibility.read"}));
            b.ConfigureServices(s=>{s.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(()=>routing);foreach(var name in new[]{"User","Workload"})s.Configure<JwtBearerOptions>(name,o=>o.ConfigurationManager=Metadata(owners.Auth));});
        }
    }
    private sealed class VehicleDatabase(string root):IAsyncDisposable
    {
        private readonly PostgreSqlContainer container=new PostgreSqlBuilder("postgres:17").Build();
        public string Owner {get;private set;}="";public string Runtime {get;private set;}="";
        public async Task Start()
        {
            await container.StartAsync();var admin=container.GetConnectionString();var secret=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            await OwnersFixture.Sql(admin,$"CREATE ROLE cv_owner LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;CREATE ROLE cv_runtime LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;");
            await OwnersFixture.Sql(admin,"CREATE DATABASE chat_vehicle OWNER cv_owner");
            Owner=new NpgsqlConnectionStringBuilder(admin){Database="chat_vehicle",Username="cv_owner",Password=secret}.ConnectionString;Runtime=new NpgsqlConnectionStringBuilder(Owner){Username="cv_runtime"}.ConnectionString;
            await using(var db=new VehicleDb(new DbContextOptionsBuilder<VehicleDb>().UseVehicle(Owner).Options)){await db.Database.MigrateAsync();await db.Database.OpenConnectionAsync();await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();}
            await OwnersFixture.Sql(Owner,(await File.ReadAllTextAsync(Path.Combine(root,"Vairas.VehicleManagement","database","runtime-grants.sql"))).Replace("v_vehicle_management_runtime","cv_runtime").Replace("v_vehicle_management_migration","cv_owner"));
        }
        public async ValueTask DisposeAsync(){NpgsqlConnection.ClearAllPools();await container.DisposeAsync();}
    }
}
