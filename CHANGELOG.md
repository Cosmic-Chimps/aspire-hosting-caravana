# Changelog

What changed in each release, for people who use the package in their AppHost. Newest first. A
release's notes on GitHub are its section here: `tools/release.sh notes <version>` prints it, and
the release workflow turns "Unreleased" into the version's section when it has none yet. A preview
release (a `-suffix` version such as `0.2.0-preview.1`) leaves this file alone.

Each section lists the changes that matter to a user, under `### Added`, `### Changed`,
`### Fixed` and `### Security`, only those needed, then **Upgrade** when upgrading takes more than
bumping the package version. Add to "Unreleased" as you go, in the same commit as the change; a
release renames it.

## Unreleased

### Added
- When the deployment the step waits for ends `superseded`, the step reads the app's deployments and
  says which one replaced it: its ID, job version, where it came from (the console, the API, a
  webhook, or CI with the commit) and its status. That deployment has usually only just started, so
  the step waits for it, within `DeploymentTimeout`, and follows it if something newer replaces it
  in turn. It passes with a warning when the newest is healthy, and fails when it isn't, can't be
  read or is still in progress when the wait ends. The new `FailOnSuperseded` setting (default
  `false`) fails the step at once, without waiting, whatever the newer deployment is.
- `aspire deploy` can publish an AppHost to Caravana. Add `builder.PublishToCaravana(name, ...)`
  to the AppHost and the deploy builds and pushes the images as Aspire always does, sends the
  AppHost as one Caravana app (each resource a service), and updates that same app on every
  later run.
- The deploy waits until the app is healthy, prints Caravana's warnings, and fails the step with
  Caravana's reason when it can't deploy. `WaitForDeployment` and `DeploymentTimeout` (10 minutes
  by default) control the wait.
- Deployment settings are Aspire parameters. `Url` and `Token` take a literal or a parameter
  through `.AsCaravanaValue()`, and `Project`, `Environment`, `AppName`, `DisplayName` and
  `DeployDashboard` choose where the app goes and what it is called.
- Secret parameters travel as Caravana secrets: they are moved out of the Compose file, never
  appear in a log line or the console, and the deploy refuses a secret it would otherwise send in
  the clear, naming the field.
- Per-resource calls say what Compose can't: `WithCaravanaDomain`, `WithCaravanaHealthCheck`,
  `WithCaravanaBackup` (Postgres or volume), `WithCaravanaMemory` and `WithCaravanaExclude`.
- Volumes get valid, stable Caravana names, so the same AppHost keeps its data across redeploys.
- Inside GitHub Actions the deploy tells Caravana which commit, ref, actor and run it came from, so
  Caravana's history shows the deploy as coming from CI.
- A deploy token (scoped to one app of one project) works as well as a full token. When the project
  or environment doesn't exist and the token can't create it, the step says so.

### Fixed
- The README says what to do when the app name equals one of the AppHost's resources (give the
  Caravana resource another name and set `AppName`), and its GitHub Actions example installs the
  Aspire CLI as a pinned local tool (`.config/dotnet-tools.json`, `dotnet tool restore`,
  `dotnet aspire deploy`) instead of piping a remote script into a shell.
