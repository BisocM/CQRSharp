namespace CQRSharp.Data.Commands
{
    /// <summary>
    /// Represents the result of a command execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands typically represent an intent to modify the system state, which might not
    /// return a meaningful value beyond success or failure. This struct is used to unify
    /// the representation of the outcome, providing a stable interface for middleware or
    /// other handlers to inspect.
    /// </para>
    /// <para>
    /// This is a <see langword="readonly"/> struct, which helps ensure immutability
    /// and guard against unintended modifications or copying behaviors in more complex
    /// scenarios.
    /// </para>
    /// </remarks>
    public readonly struct CommandResult
    {
        /// <summary>
        /// Gets a value indicating whether the command succeeded.
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// Gets a message describing the error, if any.
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// Gets a numeric code representing the error type, if applicable.
        /// </summary>
        public int? ErrorCode { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandResult"/> struct.
        /// </summary>
        /// <param name="isSuccess">Indicates whether the command succeeded.</param>
        /// <param name="errorMessage">Optional error message if the command failed.</param>
        /// <param name="errorCode">Optional error code if the command failed.</param>
        private CommandResult(bool isSuccess, string? errorMessage = null, int? errorCode = null)
        {
            IsSuccess = isSuccess;
            ErrorMessage = errorMessage;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Creates a <see cref="CommandResult"/> indicating that the command was successful.
        /// </summary>
        /// <returns>A success result.</returns>
        public static CommandResult FromSuccess() =>
            new CommandResult(isSuccess: true);

        /// <summary>
        /// Creates a <see cref="CommandResult"/> indicating that the command failed.
        /// </summary>
        /// <param name="errorMessage">A description of why the command failed.</param>
        /// <param name="errorCode">An optional code describing the nature of the error.</param>
        /// <returns>A failure result.</returns>
        public static CommandResult FromError(string errorMessage, int? errorCode = null) =>
            new CommandResult(isSuccess: false, errorMessage, errorCode);

        /// <inheritdoc />
        public override string ToString()
            => IsSuccess
                ? "Command succeeded."
                : $"Command failed: {ErrorMessage} (Code: {ErrorCode})";
    }
}