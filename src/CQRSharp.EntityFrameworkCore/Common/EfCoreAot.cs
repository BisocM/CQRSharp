namespace CQRSharp.EntityFrameworkCore;

/// <summary>The one reason every EF Core-backed member is marked as incompatible with Native AOT and trimming.</summary>
internal static class EfCoreAot
{
    public const string Message = "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";
}
