using System.Text.Json.Serialization;

namespace KindleKeep.Api.Core.DTOs;

public record CopilotResponse(
    [property: JsonPropertyName("explanation")] string? Explanation,
    [property: JsonPropertyName("detectedPlatform")] string? DetectedPlatform,
    [property: JsonPropertyName("remediationSnippet")] string? RemediationSnippet
);
