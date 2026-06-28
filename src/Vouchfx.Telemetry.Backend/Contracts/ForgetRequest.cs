using System.Text.Json.Serialization;

namespace Vouchfx.Telemetry.Backend.Contracts;

/// <summary>The forget request body: <c>{ "installId": "&lt;guid&gt;" }</c>.</summary>
public sealed record ForgetRequest(
    [property: JsonPropertyName("installId")] string InstallId);
