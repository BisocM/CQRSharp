namespace CQRSharp.Abstractions.Interfaces.Markers.Command;

/// <summary>
///     Non-generic marker shared by <see cref="ICommand" /> and <see cref="ICommand{TResult}" />, so command-flavored
///     tooling can recognize "this is a command" whether or not it returns a value. (The two cannot share a generic
///     base because their <c>IRequest&lt;T&gt;</c> response types differ.)
/// </summary>
public interface ICommandMarker;
