using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Management.Cancellation;

public sealed class CancellationManager(ILogger<CancellationManager> logger)
{
    private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    public CancellationToken Token => _cancellationTokenSource.Token;

    public void Initialize()
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; //Prevent the process from terminating.
            Cancel();
        };
    }

    private void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Cancel();
        logger.LogInformation("Cancellation requested. Completing current operations...");
    }

    public void ResetCancellation()
    {
        if (!_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        logger.LogInformation("Cancellation reset.");
    }
}