# Generic Harness

A standalone ASP.NET Core harness that imports OpenAPI descriptions, exposes supported operations, validates generic invocation input, executes provider requests with evidence-based retry behavior, persists execution state, and records structured evidence.

## Projects

- `Harness.Api`: harness HTTP service.
- `Harness.Api.Tests`: unit, persistence, runner, and endpoint integration tests.
- `Harness.Client`: independent command-line client that uses only the harness HTTP API.

## Build and test

From this directory:

```powershell
dotnet build GenericHarness.slnx
dotnet test GenericHarness.slnx
```

## Run the harness

```powershell
cd .\Harness.Api
dotnet run --launch-profile http
```

The HTTP profile listens on `http://localhost:5247`.

The application applies EF Core migrations at startup. The development database is `Harness.Api/harness.development.db`.

See the [harness HTTP API OpenAPI specification](./harness.openapi.yaml) for route and schema details.

## Register the sample API

From the repository root:

```powershell
$providerId = [guid]::NewGuid()
$registration = Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5247/providers/$providerId/api-descriptions" `
  -ContentType "application/json" `
  -InFile ".\sample-openapi.json"

$versionId = $registration.apiDescription.id
$registration.operations | Format-Table key, supportStatus
```

## Execute through the independent client

```powershell
dotnet run `
  --project .\Harness.Client\Harness.Client.csproj `
  -- `
  request `
  http://localhost:5247 `
  $providerId `
  $versionId `
  "GET:/get" `
  "@.\get-input.json"
```

For the JSON body operation:

```powershell
dotnet run `
  --project .\Harness.Client\Harness.Client.csproj `
  -- `
  request `
  http://localhost:5247 `
  $providerId `
  $versionId `
  "POST:/post" `
  "@.\post-input.json"
```

Use the returned execution ID with:

```powershell
dotnet run --project .\Harness.Client\Harness.Client.csproj -- inspect http://localhost:5247 $executionId
dotnet run --project .\Harness.Client\Harness.Client.csproj -- evidence http://localhost:5247 $executionId
dotnet run --project .\Harness.Client\Harness.Client.csproj -- attempts http://localhost:5247 $executionId
```

The `inspect` command returns the execution record. For the combined inspection view, replay evaluation, and other HTTP endpoints, see the [harness HTTP API OpenAPI specification](./harness.openapi.yaml).

See [Supported Scope](./docs/supported-scope.md) for current import, invocation, credential, and retry limitations.
