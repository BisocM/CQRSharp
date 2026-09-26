using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>What became of one received notification; see <see cref="INotificationIntake.AcceptAsync" />.</summary>
/// <param name="Outcome">What the intake did.</param>
/// <param name="StoredCount">The outbox messages stored, one per local handler; zero unless <see cref="IntakeOutcome.Stored" />.</param>
/// <param name="Detail">Why, for an outcome other than <see cref="IntakeOutcome.Stored" />; <see langword="null" /> otherwise.</param>
/// <param name="RetryAfter">
///     For <see cref="IntakeOutcome.UnknownNotification" />, how long to hold it before trying again, while it is young
///     enough that an instance which knows it may still take it; <see langword="null" /> once that grace period is over,
///     and for every other outcome.
/// </param>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public readonly record struct IntakeResult(IntakeOutcome Outcome, int StoredCount, string? Detail, TimeSpan? RetryAfter);
