using Aspire.Hosting.Pipelines;

namespace CosmicChimps.Aspire.Hosting.Caravana;

internal static class CaravanaDeployer
{
    // Replaced by the transform (A002) and the deploy flow (A003).
    internal static Task RunStepAsync(CaravanaResource resource, PipelineStepContext ctx) =>
        throw new NotImplementedException("The Caravana deploy flow is not wired yet.");
}
