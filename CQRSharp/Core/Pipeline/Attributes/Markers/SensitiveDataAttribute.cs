namespace CQRSharp.Core.Pipeline.Attributes.Markers
{
    /// <summary>
    /// Specialized attribute for marking certain properties as sensitive data. Used for built-in logger.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SensitiveDataAttribute : Attribute { }
}