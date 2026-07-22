using KindleKeep.Api.API.Endpoints;
using KindleKeep.Api.API.Hubs;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

var listeningUrl = builder.Configuration["WebHost:Url"] ?? Environment.GetEnvironmentVariable("KK_WEBHOST_URL") ?? "http://localhost:5247";
builder.WebHost.UseUrls(listeningUrl);

builder.Services.AddHttpClient("GitHub", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "KindleKeep-Auth-Agent");
});

builder.Services.AddHttpClient("WatcherClient", client =>
{
    // Ceiling for per-monitor RequestTimeout (validated/clamped to 1-60s in MonitorEndpoints);
    // must be >= that clamp or a user-configured timeout gets silently truncated here instead.
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("User-Agent", "KindleKeep-Sentinel/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // A monitor must still reach sites with expired/invalid certs to report on them: an expired
    // cert should read as "up but graded F", not a misleading "down". Certificate quality is judged
    // by WatcherEngine's own inspection/grade, not by refusing the probe. Scoped to this client only.
    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, _, _, _) => true
    }
});

builder.Services.AddHttpClient("DiscordClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("ResendClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        var configOrigins = builder.Configuration["AllowedOrigins"]?.Split(',') ?? Environment.GetEnvironmentVariable("KK_ALLOWED_ORIGINS")?.Split(',') ?? Array.Empty<string>();
        
        var origins = new List<string>(configOrigins);
        if (!origins.Contains("http://localhost:5173"))
        {
            origins.Add("http://localhost:5173");
        }
        
        policy.WithOrigins(origins.ToArray())
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? Environment.GetEnvironmentVariable("KK_DATABASE_URL") 
    ?? throw new InvalidOperationException("Database connection string is missing.");

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
var dataSource = dataSourceBuilder.Build();

builder.Services.AddSingleton(dataSource);

builder.Services.AddDbContextPool<KindleDbContext>(options =>
{
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.CommandTimeout(15);
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null);
    });

    options.UseModel(KindleKeep.Api.Infrastructure.Data.CompiledModels.KindleDbContextModel.Instance);
});

builder.Services.AddSingleton<KindleKeep.Api.Infrastructure.Identity.TokenService>();
builder.Services.AddSingleton<KindleKeep.Api.Infrastructure.Alerting.AlertManager>();
builder.Services.AddHostedService<KindleKeep.Api.Infrastructure.BackgroundServices.WatcherEngine>();
builder.Services.AddHostedService<KindleKeep.Api.Infrastructure.BackgroundServices.PruningService>();
builder.Services.AddExceptionHandler<KindleKeep.Api.Infrastructure.Exceptions.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var jwtKey = builder.Configuration["Jwt:Key"] 
    ?? Environment.GetEnvironmentVariable("KK_JWT_KEY") 
    ?? throw new InvalidOperationException("JWT Secret Key is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = "KindleKeep-Auth",
            ValidateAudience = true,
            ValidAudience = "KindleKeep-Dashboard",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/pulse"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSignalR()
    .AddJsonProtocol(options => 
    {
        options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    });

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRouting();

app.UseCors("FrontendPolicy");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/stay-awake", () => TypedResults.Ok(new StayAwakeResponse("awake", DateTime.UtcNow))).WithName("StayAwake");
app.MapGet("/health", () => TypedResults.Ok("Healthy")).WithName("GetHealthStatus");

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapMonitorEndpoints();
app.MapIncidentEndpoints();
app.MapVaultEndpoints();
app.MapPublicEndpoints();
app.MapHub<PulseHub>("/hubs/pulse");

app.Run();

[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(MonitorTarget))]
[JsonSerializable(typeof(UptimeLog))]
[JsonSerializable(typeof(SecurityAudit))]
[JsonSerializable(typeof(AlertIncident))]
[JsonSerializable(typeof(GithubTokenResponse))]
[JsonSerializable(typeof(GithubUserResponse))]
[JsonSerializable(typeof(GoogleTokenResponse))]
[JsonSerializable(typeof(GoogleUserResponse))]
[JsonSerializable(typeof(GitlabTokenResponse))]
[JsonSerializable(typeof(GitlabUserResponse))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(PulseUpdate))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(KindleKeep.Api.Infrastructure.Alerting.DiscordPayload))]
[JsonSerializable(typeof(KindleKeep.Api.Infrastructure.Alerting.ResendPayload))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(UserProfileResponse))]
[JsonSerializable(typeof(CreateMonitorRequest))]
[JsonSerializable(typeof(MonitorResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<MonitorResponse>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.SecurityAuditResponse))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.UserUsageResponse))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.UpdateSettingsRequest))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.UptimeLogResponse))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<KindleKeep.Api.Core.DTOs.UptimeLogResponse>))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.UserSettingsResponse))]
[JsonSerializable(typeof(IncidentResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<IncidentResponse>))]
[JsonSerializable(typeof(StayAwakeResponse))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.VaultTargetResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<KindleKeep.Api.Core.DTOs.VaultTargetResponse>))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.VaultAuditDetail))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.UpdateMonitorRequest))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.MonitorDetailResponse))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.SetPublicStatusRequest))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.PublicStatusResponse))]
[JsonSerializable(typeof(KindleKeep.Api.Core.DTOs.PublicMonitorResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<KindleKeep.Api.Core.DTOs.UptimeLogResponse>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}