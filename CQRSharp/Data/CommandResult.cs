namespace CQRSharp.Data
{
    /// <summary>
    /// Represents the result of a command execution.
    /// Commands typically represent an intent to modify the system state, which might not return a meaningful value.
    /// This is a common return type for middleware to interact with the command state with no issue.
    /// </summary>
    /// <remarks>
    /// This structure provides predefined instances to indicate success and failure outcomes.
    /// </remarks>
    public readonly struct CommandResult
    {
        public static readonly CommandResult Success = new();
        public static readonly CommandResult Fail = new();
    }
    
    //TODO: Expand this with more usage.
    //This struct could contain more information about command execution, which could help. Things like error message, error code, timestamps for execution start & end, etc.
}