using CQRSharp.Core.Options;
using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Interfaces.Markers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CQRSharp.Helpers
{
    public static class CommandSanitizer
    {
        /// <summary>
        /// Sanitizes the command object by redacting sensitive data, determining whether or not to display the command context.
        /// </summary>
        /// <param name="request">The request which needs sanitization.</param>
        /// <param name="options">The library options object.</param>
        /// <returns></returns>
        public static string Sanitize(IRequest request, DispatcherOptions options)
        {
            //If the command context logging is not enabled, just return immediately.
            if (!options.EnableExecutionContextLogging)
                return string.Empty;

            var properties = request.GetType().GetProperties();
            var sanitizedCommand = new Dictionary<string, object?>();

            foreach (var property in properties)
            {
                var value = property.GetValue(request);
                var isSensitive = property.GetCustomAttributes(typeof(SensitiveDataAttribute), false).Length != 0;

                //If the sensitive logging is enabled, redact the sensitive data.
                sanitizedCommand[property.Name] = isSensitive && !options.EnableSensitiveDataLogging ? "***REDACTED***" : value;
            }

            return $"Execution Context: {JsonSerializer.Serialize(sanitizedCommand, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            })}";
        }
    }
}