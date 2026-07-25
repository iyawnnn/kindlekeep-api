// Core/DTOs/SecurityAuditResponse.cs
using System;
using System.Text.Json.Serialization;

namespace KindleKeep.Api.Core.DTOs;

public record SecurityAuditResponse(
    [property: JsonPropertyName("hasCsp")] bool HasCsp,
    [property: JsonPropertyName("hasHsts")] bool HasHsts,
    [property: JsonPropertyName("hasXfo")] bool HasXfo,
    [property: JsonPropertyName("hasNosniff")] bool HasNosniff,
    [property: JsonPropertyName("sslIssuer")] string? SslIssuer,
    [property: JsonPropertyName("sslExpiryAt")] DateTime? SslExpiryAt,
    [property: JsonPropertyName("rawHeaders")] string? RawHeaders,
    [property: JsonPropertyName("tlsVersion")] string? TlsVersion,
    [property: JsonPropertyName("detectedPlatform")] string? DetectedPlatform,
    [property: JsonPropertyName("remediationSnippet")] string? RemediationSnippet
);