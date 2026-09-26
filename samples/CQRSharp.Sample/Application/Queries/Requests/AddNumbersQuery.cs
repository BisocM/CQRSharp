namespace CQRSharp.Sample.Application.Queries.Requests;

/// <summary>
///     A query with a value-type result. Under Native AOT the container cannot close an open-generic behavior over it,
///     so this request proves the generated closed behaviors work in the published binary. Nothing in it depends on the
///     caller, so it keeps the default request context.
/// </summary>
public sealed class AddNumbersQuery(int left, int right) : QueryBase<int>
{
    public int Left { get; } = left;
    public int Right { get; } = right;
}
