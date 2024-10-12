namespace CQRSharp.Data
{
    public readonly struct CommandResult
    {
        public static readonly CommandResult Success = new();
        public static readonly CommandResult Fail = new();
    }
}