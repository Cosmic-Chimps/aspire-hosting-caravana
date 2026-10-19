using CosmicChimps.Aspire.Hosting.Caravana;

var builder = DistributedApplication.CreateBuilder(args);

// Everything the deploy needs is an Aspire parameter: prompted for when missing, secret when marked,
// different per environment. Supply them the usual ways, e.g.
//   dotnet user-secrets set Parameters:caravana-url https://caravana.example.com
//   Parameters__caravana-token=cvat_... aspire deploy
var caravanaUrl = builder.AddParameter("caravana-url");
var caravanaToken = builder.AddParameter("caravana-token", secret: true);
var shopDomain = builder.AddParameter("shop-domain");

// Where `aspire deploy` pushes the images. The push is authenticated by the ambient `docker login`
// (docker login ghcr.io), not by anything in this file. Caravana pulls from the same registry, so
// add its credentials in Caravana under Settings > Registries if the images are private.
var registryEndpoint = builder.AddParameter("registry-endpoint");
var registryRepository = builder.AddParameter("registry-repository");
builder.AddContainerRegistry("registry", registryEndpoint, registryRepository);

var caravana = builder.PublishToCaravana("shop", s =>
{
    s.Url = caravanaUrl.AsCaravanaValue();
    s.Token = caravanaToken.AsCaravanaValue();
    s.Project = "shop";
    s.Environment = "production";
});

// A secret parameter: its value reaches Caravana as a secret, never inside the Compose file.
var redisPassword = builder.AddParameter("redis-password", secret: true);
var cache = builder.AddRedis("cache", password: redisPassword);

// A container with a data volume: it becomes a Caravana volume that survives redeploys.
var files = builder
    .AddContainer("files", "docker.io/filebrowser/filebrowser", "v2")
    .WithHttpEndpoint(targetPort: 80)
    .WithVolume("files-data", "/srv")
    .WithCaravanaHealthCheck(caravana, port: 80)
    .WithCaravanaBackup(caravana, CaravanaBackupEngine.Volume);

builder
    .AddProject<Projects.CaravanaExample_Web>("web")
    .WithExternalHttpEndpoints()
    .WithReference(cache)
    .WaitFor(cache)
    .WithCaravanaDomain(caravana, shopDomain)
    .WithCaravanaHealthCheck(caravana, "/health");

builder.Build().Run();
