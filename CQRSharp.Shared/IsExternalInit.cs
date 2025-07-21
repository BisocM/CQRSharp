namespace CQRSharp.Shared;

/// <summary>
///     This class is required to support init-only setters and record types in C# 9.
///     It acts as a marker to signal that a member is init-only.
/// </summary>
internal static class IsExternalInit
{
}