using CQRSharp.Core.Options;
using CQRSharp.Core.Pipeline.Attributes.Markers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CQRSharp.Helpers
{
    public static class CommandSanitizer
    {
        /// <summary>
        /// Sanitizes the command object by redacting sensitive data, determining whether or not to display the command context.
        /// </summary>
        /// <param name="command">The command which needs sanitization.</param>
        /// <param name="options">The library options object.</param>
        /// <returns></returns>
        public static string Sanitize(object command, CQRSharpOptions options)
        {
            //If the command context logging is not enabled, just return immediately.
            if (!options.EnableExecutionContextLogging)
                return string.Empty;

            var properties = command.GetType().GetProperties();
            var sanitizedCommand = new Dictionary<string, object?>();

            foreach (var property in properties)
            {
                var value = property.GetValue(command);
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