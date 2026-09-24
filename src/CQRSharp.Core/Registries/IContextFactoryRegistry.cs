namespace CQRSharp.Core.Registries;

/// <summary>
///     The <see cref="RequestContextSource" /> of every context type the application's requests use, merged from every
///     source-generated module.
/// </summary>
internal interface IContextFactoryRegistry
{
    /// <summary>The source of the contexts of <paramref name="contextType" />, or <see langword="null" /> when no request uses it.</summary>
    /// <param name="contextType">The context type.</param>
    /// <returns>The source, or <see langword="null" />.</returns>
    RequestContextSource? TryGetSource(Type contextType);
}
