using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Builds the one validation ProblemDetails shape the package writes, whether the failures came from a
///     <see cref="RequestValidationException" /> or a <see cref="CommandErrorKind.Validation" /> result.
/// </summary>
internal static class ValidationProblemDetailsFactory
{
    /// <summary>The ProblemDetails extension member carrying the validation failure codes, grouped like <c>errors</c>.</summary>
    internal const string ErrorCodesExtensionName = "errorCodes";

    /// <summary>
    ///     The detail of a validation problem raised by the pipeline: the summary <c>CommandResult.Invalid(...)</c>
    ///     carries when a handler gives none.
    /// </summary>
    internal const string DefaultDetail = "Validation failed.";

    public static HttpValidationProblemDetails Create(IReadOnlyList<ValidationFailure> failures, int status, string? detail)
    {
        // Failures that name no member are grouped under the empty key, the ASP.NET Core convention for
        // request-level errors.
        var messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var codes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var failure in failures)
        {
            var member = failure.MemberName ?? string.Empty;
            if (!messages.TryGetValue(member, out var memberMessages))
            {
                messages[member] = memberMessages = [];
                codes[member] = [];
            }

            memberMessages.Add(failure.Message);
            codes[member].Add(failure.Code);
        }

        var problemDetails = new HttpValidationProblemDetails(
            messages.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal))
        {
            Status = status,
            Detail = detail
        };

        problemDetails.Extensions[ErrorCodesExtensionName] = ToJsonElement(codes);
        // The same shape whether the pipeline or the handler rejected the input: the kind is on both.
        problemDetails.Extensions[CommandResultHttpExtensions.ErrorKindExtensionName] = nameof(CommandErrorKind.Validation);
        return problemDetails;
    }

    // Extension members are serialized as object. A JsonElement is one of the few shapes the framework's
    // source-generated ProblemDetails JSON context can write without reflection, which a Dictionary<string, string[]>
    // is not - so the codes are pre-rendered to keep the response working under Native AOT.
    private static JsonElement ToJsonElement(Dictionary<string, List<string>> codes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (member, memberCodes) in codes)
            {
                writer.WriteStartArray(member);
                foreach (var code in memberCodes)
                    writer.WriteStringValue(code);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length));
        return document.RootElement.Clone();
    }
}
