using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Caravana;

internal enum DeploymentOutcomeKind
{
    Healthy,
    Failed,
    Reverted,
    Superseded,
    TimedOut,
}

internal sealed record DeploymentOutcome(DeploymentOutcomeKind Kind, DeploymentInfo? Deployment);

/// <summary>
/// Follows a deployment until its status is final: <c>healthy</c>, <c>failed</c>, <c>reverted</c> or
/// <c>superseded</c>, or the timeout passes. <c>POST .../compose</c> only returns once Nomad has accepted
/// the job; without this wait a deploy that never becomes healthy would leave the step green.
/// </summary>
internal sealed class CaravanaDeploymentWaiter(CaravanaApiClient api, ILogger logger, TimeSpan pollInterval)
{
    internal async Task<DeploymentOutcome> WaitAsync(
        string location,
        DeploymentInfo accepted,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var last = accepted;
        var lastStatus = "";

        while (true)
        {
            if (last.IsFinal)
                return new DeploymentOutcome(KindOf(last.Status), last);

            if (stopwatch.Elapsed >= timeout)
                return new DeploymentOutcome(DeploymentOutcomeKind.TimedOut, last);

            await Task.Delay(pollInterval, ct);

            try
            {
                last = await api.GetDeploymentAsync(location, ct);
                if (last.Status != lastStatus)
                {
                    lastStatus = last.Status;
                    logger.LogInformation("Deployment {Id} is {Status}", last.Id, last.Status);
                }
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                // One failed poll is not a failed deploy; a persistent fault still ends at the timeout.
                logger.LogWarning("Polling the deployment failed ({Error}); trying again", ex.Message);
            }
        }
    }

    private static DeploymentOutcomeKind KindOf(string status) =>
        status switch
        {
            "healthy" => DeploymentOutcomeKind.Healthy,
            "reverted" => DeploymentOutcomeKind.Reverted,
            "superseded" => DeploymentOutcomeKind.Superseded,
            _ => DeploymentOutcomeKind.Failed,
        };

    /// <summary>Transport failures and 5xx are retried; a 4xx (bad token, gone app) is not.</summary>
    internal static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex switch
        {
            CaravanaApiException api => api.Status >= 500,
            HttpRequestException => true,
            OperationCanceledException => !ct.IsCancellationRequested,
            _ => false,
        };
}
