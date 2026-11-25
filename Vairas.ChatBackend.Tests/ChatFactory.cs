using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Vairas.ChatBackend.Business;
using Xunit;

namespace Vairas.ChatBackend.Tests;

public sealed class ChatFactory : WebApplicationFactory<Program>
{
    private readonly RSA rsa = RSA.Create(2048);
    public TestWorld World { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing").UseContentRoot(Path.Combine(ChatPostgres.Root, "Vairas.ChatBackend"));
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Chat"] = "Host=localhost;Database=unused", ["AllowedHosts"] = "localhost",
            ["Authentication:WorkloadClients:mcp-fixture:0"] = "confirmations.validate"
        }));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatStore>(World.Store); services.AddSingleton<IChatOwners>(World.Owners); services.AddSingleton<IModelAdapter>(World.Model); services.AddSingleton<TimeProvider>(World.Clock);
            var key = new RsaSecurityKey(rsa) { KeyId = "fixture" };
            foreach (var scheme in new[] { "User", "Workload" }) services.Configure<JwtBearerOptions>(scheme, options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new() { Issuer = "https://localhost:7100", SigningKeys = { key } }));
        });
    }
    public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost:7800"), AllowAutoRedirect = false });
    public string Token(string audience = "vairas-chat", string? subject = null, string? session = null, string? scope = null)
    {
        var claims = new List<Claim> { new("sub", subject ?? World.Actor.IdentityId.ToString()), new("sid", session ?? World.Actor.SessionId.ToString()) };
        if (scope is not null) claims.Add(new("scope", scope));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://localhost:7100", audience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new RsaSecurityKey(rsa) { KeyId = "fixture" }, SecurityAlgorithms.RsaSha256)));
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) rsa.Dispose(); }
}
