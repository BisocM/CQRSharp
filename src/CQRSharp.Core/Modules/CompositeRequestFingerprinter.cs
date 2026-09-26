using CQRSharp.Core.Idempotency;

namespace CQRSharp.Core.Modules;

/// <summary>
///     Asks each module's generated fingerprinter in turn. At most one module fingerprints a request type: the one that
///     declares it, or, for a request declared where the generator does not run or a closed generic one, the one that
///     handles it.
/// </summary>
internal sealed class CompositeRequestFingerprinter : IRequestFingerprinter
{
    private readonly IRequestFingerprinter[] _fingerprinters;

    public CompositeRequestFingerprinter(IEnumerable<ICqrsModule> modules)
        => _fingerprinters = modules.Select(m => m.RequestFingerprinter).OfType<IRequestFingerprinter>().ToArray();

    public bool TryFingerprint(IRequest request, out string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var fingerprinter in _fingerprinters)
            if (fingerprinter.TryFingerprint(request, out fingerprint))
                return true;

        fingerprint = string.Empty;
        return false;
    }
}
