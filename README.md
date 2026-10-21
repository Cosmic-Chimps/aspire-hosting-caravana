# CosmicChimps.Aspire.Hosting.Caravana

Publish an [Aspire](https://aspire.dev) AppHost to [Caravana](https://github.com/Cosmic-Chimps/caravana),
a self-hosted PaaS on Nomad. Add one line to your AppHost and `aspire deploy` builds and pushes your
images (as Aspire always does), turns the AppHost into **one Caravana app** (each resource a service),
deploys it, waits until it is healthy and prints Caravana's warnings, or fails with Caravana's reason.
Run it again and the same app is updated.

- One AppHost, one app. Services reach each other by name, as they do in Aspire's Compose output.
- Secret parameters travel as Caravana secrets: never in the Compose file, a log line or the console.
- Volumes get valid, stable names, so data survives redeploys.
- What Compose can't say (domain, health check, backup, memory) is a per-resource call.
- No hand-written definition next to your AppHost.

## Requirements

- .NET 10, Aspire 13.6 (this package builds on `Aspire.Hosting.Docker` 13.6.1).
- Caravana 0.9.0 or later (its API 0.21 accepts what Aspire writes: a network and volumes with
  `driver`, and `entrypoint`).
- A container registry both `aspire deploy` can push to and Caravana can pull from.

## Install

```bash
dotnet add package CosmicChimps.Aspire.Hosting.Caravana
```

## Quick start

```csharp
using CosmicChimps.Aspire.Hosting.Caravana;

var builder = DistributedApplication.CreateBuilder(args);

// Deployment settings are Aspire parameters: prompted for when missing, secret when marked.
var caravanaUrl = builder.AddParameter("caravana-url");                  // https://caravana.example.com
var caravanaToken = builder.AddParameter("caravana-token", secret: true); // cvat_...
var shopDomain = builder.AddParameter("shop-domain");                    // shop.example.com

// Where `aspire deploy` pushes the images. The push uses your ambient `docker login`.
var registryEndpoint = builder.AddParameter("registry-endpoint");        // ghcr.io
var registryRepository = builder.AddParameter("registry-repository");    // acme
builder.AddContainerRegistry("registry", registryEndpoint, registryRepository);

var caravana = builder.PublishToCaravana("shop", s =>
{
    s.Url = caravanaUrl.AsCaravanaValue();
    s.Token = caravanaToken.AsCaravanaValue();
    s.Project = "shop";            // default: "default"
    s.Environment = "production";  // default: "production"
});

var db = builder.AddPostgres("db")
    .WithDataVolume()
    .WithCaravanaBackup(caravana, CaravanaBackupEngine.Postgres, schedule: "0 3 * * *", keep: 7);

builder.AddProject<Projects.Shop_Web>("web")
    .WithExternalHttpEndpoints()
    .WithReference(db)
    .WaitFor(db)
    .WithCaravanaDomain(caravana, shopDomain)          // or a literal: "shop.example.com"
    .WithCaravanaHealthCheck(caravana, "/health");

builder.Build().Run();
```

Then:

```bash
docker login ghcr.io
dotnet user-secrets set Parameters:caravana-url https://caravana.example.com   # or answer the prompts
aspire deploy
```

`example/` is a complete AppHost (a web project, Redis with a password parameter and a container with a
data volume) that builds in CI.

### Settings

`PublishToCaravana(name, configure)` returns an `IResourceBuilder<CaravanaResource>`. In `configure`:

| Setting | Default | |
| --- | --- | --- |
| `Url` | required | Caravana's address. Literal or parameter (`.AsCaravanaValue()`). |
| `Token` | required | An API token, `cvat_...`. Literal or parameter. |
| `Project` | `default` | Created if it doesn't exist (full token). |
| `Environment` | `production` | Created if it doesn't exist (full token). |
| `AppName` | the `name` argument | The app's name in its environment. |
| `DisplayName` | the app ID | What Caravana's console shows. |
| `DeployDashboard` | `false` | Deploy the Aspire dashboard too. It has no sign-in unless you configure one, so it is off. |
| `WaitForDeployment` | `true` | Wait until healthy and fail the step otherwise. |
| `DeploymentTimeout` | 10 minutes | How long to wait. |
| `FailOnSuperseded` | `false` | When a newer deployment replaced this one (a deploy from the console, say), the step names it and passes with a warning if it is healthy. `true` fails the step in that case too. A newer deployment that isn't healthy, or can't be read, always fails the step. |

The app's ID in Caravana is its name in `default`/`production`, otherwise
`<project>--<environment>--<name>`. Names are lower-case letters, numbers and single dashes.

### Per-resource calls

All take the `caravana` builder and go on any resource (`T : IResource`).

| Call | Does |
| --- | --- |
| `WithCaravanaDomain(caravana, host, port?)` | Routes a public host over HTTPS. `host` is a string or a parameter. Leave `port` out when the service has one port. |
| `WithCaravanaHealthCheck(caravana, path?, port?)` | The check Nomad runs: HTTP on `path`, or TCP without it. |
| `WithCaravanaBackup(caravana, engine, schedule?, keep?)` | `Postgres` (a dump) or `Volume` (the volumes archived while the service is stopped for a few seconds). Needs a backup destination in Caravana. |
| `WithCaravanaMemory(caravana, reservedMB, maxMB?)` | Written as `mem_reservation` / `mem_limit`. |
| `WithCaravanaExclude(caravana)` | Leaves the resource out of the app (and its `depends_on` entries). |

A service with more than one port (the dashboard has three) needs to say which one is its front: the
publisher uses its first published port (else the first exposed one) for the health check and for a
domain without `port`.

## How secrets travel

Every parameter marked `secret: true` is a secret. When `aspire deploy` runs, the publisher resolves each
one and moves every environment entry whose value contains it (`REDIS_PASSWORD`,
`ConnectionStrings__cache: "cache:6379,password=..."`, `CACHE_URI`) out of the Compose file and into the
request's per-service `secrets`. Caravana stores those only in Nomad Variables, and the container receives
them as environment variables. The Compose file Caravana keeps has no secret value in it.

- A secret inside a `command`, a label or any other field would travel in the clear, so the publisher
  refuses it, naming the field (not the value).
- The token only goes in the `Authorization` header. Error bodies are redacted before they are logged or
  thrown.
- `ToString()` of a value prints `<parameter:name>`, never the content.

## Volumes

Aspire names a volume `<apphost>-<hash>-<name>` (`cosmicchimps.aspire.apphost-a6dba33c27-seq-data`).
Caravana's volume names are lower case, without dots. The publisher keeps the part after the hash
(`seq-data`), lowercases it, replaces other characters with `-`, trims to 63 characters and keeps names
unique, so the same AppHost gives the same volume on every run. On the host it is the Nomad volume
`<app-id>_<name>`. Renaming a volume in the AppHost makes a new, empty volume.

## Tokens

- A **full token** can create the project and environment when they don't exist.
- A **deploy token** (made with a scope in Caravana) can only deploy one app name of one project. It can't
  read or create projects, so create the project and environment first. The publisher notices (403),
  carries on, and if Caravana then says the project or environment doesn't exist, tells you to create
  them or use a full token.

## GitHub Actions

```yaml
name: deploy
on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: '10.x'
      - name: Install the Aspire CLI
        run: curl -sSL https://aspire.dev/install.sh | bash
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - name: Deploy
        env:
          Parameters__caravana-url: ${{ secrets.CARAVANA_URL }}
          Parameters__caravana-token: ${{ secrets.CARAVANA_TOKEN }}
          Parameters__shop-domain: shop.example.com
          Parameters__registry-endpoint: ghcr.io
          Parameters__registry-repository: ${{ github.repository_owner }}
          Parameters__db-password: ${{ secrets.DB_PASSWORD }}
        run: aspire deploy
```

Inside GitHub Actions the publisher sends the commit, ref, actor, run address and repository as
`Caravana-Origin-*` headers, so Caravana's history shows "from CI" with a link to the run.

If a newer deployment replaces the one the step waits for (the owner deployed from the console during the
rollout), the step says which one it is, for example `superseded by deployment d2 (v7, from the console),
now healthy`. It passes with a warning when that deployment is healthy, and fails when it isn't, unless
`FailOnSuperseded` is `true`.

If the images are private, Caravana pulls them with credentials added under Settings > Registries. The
step says so when Caravana answers `image_not_found` or `registry_auth_failed`.

## Limitations

- One AppHost is one Caravana app. Caravana's rules for an app apply (one volume belongs to one service,
  service names are DNS labels, and so on); anything it can't honor fails the deploy naming the field.
- Images are built and pushed by Aspire, not by Caravana.
- Service references work as Aspire writes them (`http://<service>:<port>`), which resolve by name inside
  one Caravana app. Aspire's service discovery isn't rendered from Nomad.
- The Aspire dashboard isn't deployed unless asked, and the `OTEL_*` entries that point at it are removed
  from the other services when it isn't.
- Secrets are environment variables. Values that come from parameters not marked secret are plain
  environment values in the Compose file.
- Compose features Caravana warns about (networks, `depends_on` conditions) arrive as warnings in the
  step's output.
- The AppHost is the source of truth: each deploy sends the whole definition, so what someone changed in
  Caravana's console for things the AppHost describes is replaced on the next deploy.

## License

MIT.
