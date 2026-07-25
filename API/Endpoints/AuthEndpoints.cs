using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Identity;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.API.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/login/github", (IConfiguration configuration, HttpContext context) =>
        {
            var clientId = configuration["Authentication:GitHub:ClientId"]
                ?? throw new InvalidOperationException("GitHub ClientId is missing.");

            var redirectUri = $"{GetPublicApiUrl(configuration)}/api/auth/callback/github";
            var queryParams = new Dictionary<string, string?>
            {
                { "client_id", clientId },
                { "redirect_uri", redirectUri },
                { "scope", "read:user user:email" },
                { "state", IssueOAuthState(context) }
            };

            return TypedResults.Redirect(QueryHelpers.AddQueryString("https://github.com/login/oauth/authorize", queryParams));
        });

        group.MapGet("/callback/github", async (
            [FromQuery] string code,
            [FromQuery] string state,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            [FromServices] NpgsqlDataSource dataSource,
            TokenService tokenService,
            HttpContext context) =>
        {
            return await ProcessOAuthCallbackAsync(
                code, state, AuthProvider.GitHub, "Authentication:GitHub",
                "https://github.com/login/oauth/access_token", "https://api.github.com/user",
                configuration, httpClientFactory, dataSource, tokenService, context);
        });

        group.MapGet("/login/google", (IConfiguration configuration, HttpContext context) =>
        {
            var clientId = configuration["Authentication:Google:ClientId"]
                ?? throw new InvalidOperationException("Google ClientId is missing.");

            var redirectUri = $"{GetPublicApiUrl(configuration)}/api/auth/callback/google";
            var queryParams = new Dictionary<string, string?>
            {
                { "client_id", clientId },
                { "redirect_uri", redirectUri },
                { "response_type", "code" },
                { "scope", "openid email profile" },
                { "state", IssueOAuthState(context) }
            };

            return TypedResults.Redirect(QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", queryParams));
        });

        group.MapGet("/callback/google", async (
            [FromQuery] string code,
            [FromQuery] string state,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            [FromServices] NpgsqlDataSource dataSource,
            TokenService tokenService,
            HttpContext context) =>
        {
            return await ProcessOAuthCallbackAsync(
                code, state, AuthProvider.Google, "Authentication:Google",
                "https://oauth2.googleapis.com/token", "https://www.googleapis.com/oauth2/v2/userinfo",
                configuration, httpClientFactory, dataSource, tokenService, context);
        });

        group.MapGet("/login/gitlab", (IConfiguration configuration, HttpContext context) =>
        {
            var clientId = configuration["Authentication:GitLab:ClientId"]
                ?? throw new InvalidOperationException("GitLab ClientId is missing.");

            var redirectUri = $"{GetPublicApiUrl(configuration)}/api/auth/callback/gitlab";
            var queryParams = new Dictionary<string, string?>
            {
                { "client_id", clientId },
                { "redirect_uri", redirectUri },
                { "response_type", "code" },
                { "scope", "read_user" },
                { "state", IssueOAuthState(context) }
            };

            return TypedResults.Redirect(QueryHelpers.AddQueryString("https://gitlab.com/oauth/authorize", queryParams));
        });

        group.MapGet("/callback/gitlab", async (
            [FromQuery] string code,
            [FromQuery] string state,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            [FromServices] NpgsqlDataSource dataSource,
            TokenService tokenService,
            HttpContext context) =>
        {
            return await ProcessOAuthCallbackAsync(
                code, state, AuthProvider.GitLab, "Authentication:GitLab",
                "https://gitlab.com/oauth/token", "https://gitlab.com/api/v4/user",
                configuration, httpClientFactory, dataSource, tokenService, context);
        });

        return endpoints;
    }

    private const string OAuthStateCookieName = "kk_oauth_state";

    // Deliberately NOT the same key as Program.cs's WebHost:Url/KK_WEBHOST_URL, which is Kestrel's own
    // bind address (e.g. http://0.0.0.0:$PORT behind Render's proxy) - that's not reachable from the
    // public internet, so GitHub/Google/GitLab could never redirect back to it. This is the externally
    // reachable URL of this API, only used to build the redirect_uri OAuth providers call back to.
    private static string GetPublicApiUrl(IConfiguration configuration) =>
        configuration["PublicApiUrl"] ?? Environment.GetEnvironmentVariable("KK_PUBLIC_API_URL") ?? "http://localhost:5247";

    private static string GetFrontendUrl(IConfiguration configuration) =>
        configuration["Frontend:Url"] ?? Environment.GetEnvironmentVariable("KK_FRONTEND_URL") ?? "http://localhost:5173";

    // Generates a random per-attempt CSRF token, stashes it in a short-lived cookie the browser will
    // echo back on the callback redirect, and returns it for use as the OAuth `state` param.
    private static string IssueOAuthState(HttpContext context)
    {
        var state = RandomNumberGenerator.GetHexString(32);

        context.Response.Cookies.Append(OAuthStateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        return state;
    }

    // Compares the `state` the provider echoed back against the cookie set in IssueOAuthState.
    // A mismatch means either the cookie expired/was never set, or this callback wasn't initiated
    // by our own /login redirect (the CSRF case the `state` param exists to catch).
    private static bool ValidateOAuthState(HttpContext context, string state)
    {
        context.Request.Cookies.TryGetValue(OAuthStateCookieName, out var expected);
        context.Response.Cookies.Delete(OAuthStateCookieName);

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(state))
        {
            return false;
        }

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(state);

        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static async Task<IResult> ProcessOAuthCallbackAsync(
        string code,
        string state,
        AuthProvider provider,
        string configSection,
        string tokenUrl,
        string userUrl,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        NpgsqlDataSource dataSource,
        TokenService tokenService,
        HttpContext context)
    {
        if (!ValidateOAuthState(context, state))
        {
            return Results.BadRequest("Invalid or expired OAuth state.");
        }

        var clientId = configuration[$"{configSection}:ClientId"]
            ?? throw new InvalidOperationException($"{provider} ClientId is missing.");
        var clientSecret = configuration[$"{configSection}:ClientSecret"] 
            ?? throw new InvalidOperationException($"{provider} ClientSecret is missing.");

        var redirectUri = $"{GetPublicApiUrl(configuration)}/api/auth/callback/{provider.ToString().ToLower()}";
        var client = httpClientFactory.CreateClient(provider.ToString());

        var tokenPayload = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "code", code },
            { "redirect_uri", redirectUri },
            { "grant_type", "authorization_code" }
        };

        var tokenResponseMsg = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(tokenPayload));
        tokenResponseMsg.EnsureSuccessStatusCode();

        string accessToken;
        if (provider == AuthProvider.GitHub)
        {
            var result = await tokenResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GithubTokenResponse);
            accessToken = result?.AccessToken ?? throw new InvalidOperationException("Failed to retrieve GitHub access token.");
        }
        else if (provider == AuthProvider.Google)
        {
            var result = await tokenResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GoogleTokenResponse);
            accessToken = result?.AccessToken ?? throw new InvalidOperationException("Failed to retrieve Google access token.");
        }
        else
        {
            var result = await tokenResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GitlabTokenResponse);
            accessToken = result?.AccessToken ?? throw new InvalidOperationException("Failed to retrieve GitLab access token.");
        }

        var userRequest = new HttpRequestMessage(HttpMethod.Get, userUrl);
        userRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        
        var userResponseMsg = await client.SendAsync(userRequest);
        userResponseMsg.EnsureSuccessStatusCode();

        string externalId, email, displayName, avatarUrl;

        if (provider == AuthProvider.GitHub)
        {
            var profile = await userResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GithubUserResponse)
                ?? throw new InvalidOperationException("Failed to retrieve GitHub user profile.");
            externalId = profile.Id.ToString();
            email = profile.Email ?? $"{profile.Login}@users.noreply.github.com";
            displayName = profile.Name ?? profile.Login;
            avatarUrl = profile.AvatarUrl;
        }
        else if (provider == AuthProvider.Google)
        {
            var profile = await userResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GoogleUserResponse)
                ?? throw new InvalidOperationException("Failed to retrieve Google user profile.");
            externalId = profile.Id;
            email = profile.Email ?? $"google_{profile.Id}@users.noreply.google.com";
            displayName = profile.Name ?? "Google User";
            avatarUrl = profile.AvatarUrl;
        }
        else
        {
            var profile = await userResponseMsg.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.GitlabUserResponse)
                ?? throw new InvalidOperationException("Failed to retrieve GitLab user profile.");
            externalId = profile.Id.ToString();
            email = profile.Email ?? $"{profile.Username}@users.noreply.gitlab.com";
            displayName = profile.Name ?? profile.Username;
            avatarUrl = profile.AvatarUrl;
        }

        var newUserId = Guid.NewGuid();
        Guid finalUserId;

        var defaultMonitorLimit = configuration.GetValue<int>("Users:DefaultMonitorLimit", 5);

        // Native AOT compliant PostgreSQL Upsert utilizing strictly typed parameters to bypass EF Core dynamic evaluation.
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ""Users"" (""Id"", ""ExternalId"", ""AuthProvider"", ""Email"", ""DisplayName"", ""AvatarUrl"", ""MonitorLimit"")
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (""ExternalId"") 
            DO UPDATE SET 
                ""DisplayName"" = EXCLUDED.""DisplayName"",
                ""AvatarUrl"" = EXCLUDED.""AvatarUrl"",
                ""Email"" = EXCLUDED.""Email""
            RETURNING ""Id"";";

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = newUserId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = externalId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (int)provider });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = displayName });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = string.IsNullOrEmpty(avatarUrl) ? string.Empty : avatarUrl });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = defaultMonitorLimit });

        var returnedIdObj = await command.ExecuteScalarAsync();
        finalUserId = returnedIdObj != null ? (Guid)returnedIdObj : newUserId;

        var user = new User
        {
            Id = finalUserId,
            ExternalId = externalId,
            AuthProvider = provider,
            Email = email,
            DisplayName = displayName,
            AvatarUrl = string.IsNullOrEmpty(avatarUrl) ? string.Empty : avatarUrl
        };

        var jwtToken = tokenService.GenerateToken(user);
        
        return Results.Redirect($"{GetFrontendUrl(configuration)}/auth-callback?token={jwtToken}");
    }
}
