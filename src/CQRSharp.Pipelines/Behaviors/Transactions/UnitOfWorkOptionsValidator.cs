using System.Data;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Rejects a <see cref="UnitOfWorkOptions.DefaultIsolationLevel" /> no data store can begin a transaction with, at host
///     start. Registered once however often the unit of work is enabled, so a failure is reported once.
/// </summary>
internal sealed class UnitOfWorkOptionsValidator : IValidateOptions<UnitOfWorkOptions>
{
    public ValidateOptionsResult Validate(string? name, UnitOfWorkOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        return options.DefaultIsolationLevel == IsolationLevel.Unspecified || UnitOfWorkSupport.IsConcrete(options.DefaultIsolationLevel)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("UnitOfWorkOptions.DefaultIsolationLevel must be IsolationLevel.Unspecified or a defined isolation level.");
    }
}
