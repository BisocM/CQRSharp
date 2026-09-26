using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Deletes what a retention rule selects one bounded page at a time, oldest first, until a page comes back short. One
///     statement over a whole backlog would hold its row locks for as long as it runs - and on SQL Server, once it holds a
///     few thousand, escalate them to a lock on the whole table, blocking every producer's insert - and a statement the
///     command timeout cuts off would roll back everything it deleted, so a backlog too large for one statement would
///     never drain. A page is its own short statement: a failure loses that page only.
/// </summary>
internal static class PagedDelete
{
    /// <summary>
    ///     Rows per statement: well under SQL Server's lock-escalation threshold (about 5,000 locks on one table), and
    ///     large enough that a steady-state purge is a handful of round trips.
    /// </summary>
    public const int PageSize = 1000;

    /// <summary>
    ///     Deletes every row of <paramref name="oldestFirst" /> in pages of <see cref="PageSize" /> and returns how many it
    ///     deleted. The query orders by the column its rule filters on, so each page is an index range.
    /// </summary>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static async Task<int> RunAsync<TEntity>(IOrderedQueryable<TEntity> oldestFirst, CancellationToken cancellationToken)
        where TEntity : class
    {
        var page = oldestFirst.Take(PageSize);
        var total = 0;
        int deleted;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            deleted = await page.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
        } while (deleted == PageSize);

        return total;
    }
}
