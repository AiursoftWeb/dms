# DMS

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fdms)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fdms&scope=baseline&ecosystem=all)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.html)

DMS is a server operations platform for humans and AI, built on Aiursoft Template. Request an operation, approve its scope through Lockbox, and follow its execution across your machines.

![DMS local preview](./screenshot.png)

## Overview

### What is DMS?

- A web console for machine inventory, approvals and workflow history.
- A CLI and reusable SDK for requesting operations and inspecting their results.
- Linux Agents that connect outward to the Controller over WebSocket. Managed machines need access to the Controller, without requiring internet access or an SSH service.
- Cluster permissions with independent request, automatic approval and approval rights for five risk levels.
- Persistent workflow state, parent/child callstacks and ClickHouse trace archives.
- Shared Aiursoft accounts, OIDC, roles, themes and localization.

The current workflows cover approved Agent upgrades and status, scoped Box Puller mirror updates, performance information, service status and restart, block devices, local filesystem capacity, machine inventory and registration. Operational commands submit a Lockbox; inspection and approval commands use authenticated management APIs. Arbitrary shell commands are not exposed.

## CLI & SDK

The CLI currently runs from a source publish with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root:

```bash
dotnet publish src/Aiursoft.DMS.Client/Aiursoft.DMS.Client.csproj -c Release \
  --no-self-contained -p:UseAppHost=false -o ./bin/client
dotnet ./bin/client/dms.dll --help
```

Distribute the complete publish directory to CLI hosts with the .NET 10 runtime. The CLI is not yet published as an installable .NET tool. The [SDK source](src/Aiursoft.DMS.SDK) is included in this repository. See [CLI and Agent setup](docs/cli-and-agent.md) for authentication, management commands and operation examples.

## Try

Open [DMS](https://dms.aiursoft.com) with an authorized company account. Cluster permissions determine which machines, requests and workflows you can access.

## Install with Docker

Install the Controller with the desired port (omit it to choose an available port):

```bash
curl -fsSL https://raw.githubusercontent.com/aiursoftweb/dms/master/install.sh | sudo bash -s -- 8080
```

Installation starts after three seconds; press Ctrl+C to cancel. Then open `http://localhost:8080`. This installs the Controller; managed machines join separately through its Agent installer.

## Run manually

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/).

```sh
cd src/Aiursoft.DMS/wwwroot
npm ci
npm run build
cd ..
dotnet run --project Aiursoft.DMS.csproj -- --urls http://localhost:5000
```

Open `http://localhost:5000`. The default local administrator is `admin@default.com` with password `Admin@123456!`. Change this password before exposing a deployment.

The defaults use SQLite and local accounts. Create a personal key under **API Keys**. Use the CLI to create clusters, configure their permissions and issue cluster join keys; **Cluster access** provides the same management operations in the web UI. See [CLI and Agent setup](./docs/cli-and-agent.md) for registration and the first operation.

## Run in Microsoft Visual Studio

Open `Aiursoft.DMS.sln`, select `Aiursoft.DMS` as the startup project, and press **F5**. Install the frontend dependencies as described above first.

## Run in Docker

Build a Controller image from the repository root:

```sh
docker build --tag dms:local .
docker run -d --name dms --restart unless-stopped \
  -p 8080:5000 -v dms-data:/data dms:local
```

Open `http://localhost:8080`. Persist `/data` for the default SQLite database, configuration, uploaded files and workflow state. The health endpoint is `/health`.

| Property | Value |
| --- | --- |
| Container port | `5000` |
| Binary directory | `/app` |
| Persistent data | `/data` |
| Configuration | `/data/appsettings.json` |
| Workflow state | `/data/dms-state` |

The shared CI validates Linux amd64 and arm64 images. Once a successful master pipeline publishes `aiursoft/dms:latest`, the standard [installer](./install.sh) can install that image with a chosen port:

```sh
sudo bash install.sh 8080
```

The Controller image includes self-contained Linux x64 and arm64 Agent bundles. Run its installation command once on each managed host, then approve registration through Lockbox. See [Agent installation](./docs/cli-and-agent.md#install-and-join-an-agent).

## Usage

### Configure CLI access

Create a personal key under **API Keys** in the Controller, then set its URL and your key:

```bash
export DMS_SERVER=https://dms.example.com
export DMS_API_KEY='<personal-key>'
dotnet ./bin/client/dms.dll access whoami
dotnet ./bin/client/dms.dll cluster list
```

These identity and management queries create no Lockbox. Server operations submit a Lockbox and require the selected Cluster's request permission plus automatic or human approval for the workflow's risk level. Self approval is a separate permission. Use the returned IDs with `lockbox get`, `operation get-workflow`, `operation get-callstack` and `operation get-trace`. Supported commands use table output by default, with `--output-format json` or `csv` for export.

### Join and manage machines

An administrator creates a Cluster join key; an operator runs the [one-time Agent installation](docs/cli-and-agent.md#install-and-join-an-agent) on each host. The service waits for registration approval, then connects outward to the Controller. Subsequent operations, including [Agent upgrades](docs/cli-and-agent.md#upgrade-an-installed-agent), run through approved workflows. Keep personal keys, join keys and machine credentials separate.

## Configuration

Run one Controller with a persistent business database and workflow state directory. Configure ClickHouse for detailed workflow traces before using the service for ongoing operations. Local accounts are the development default; OIDC is an explicit deployment setting.

See [configuration and persistence](./docs/configuration.md) for database settings, trace storage, authentication, recovery and cancellation behavior.

## Documentation

- [CLI, authentication, Cluster management and Agent setup](docs/cli-and-agent.md)
- [Configuration, persistence, recovery and cancellation](docs/configuration.md)
- [Available workflows and Cluster permissions](docs/workflow-discovery.md)
- [Workflow authoring](docs/authoring-workflows.md)

## How to contribute

Report bugs and feature requests through the [issue tracker](https://github.com/AiursoftWeb/dms/issues). Use a feature branch or fork and submit a merge request.

Build and test from the repository root:

```sh
dotnet build Aiursoft.DMS.sln
dotnet test Aiursoft.DMS.sln
./lint.sh
```

NugetNinja maintains the shared Template files and dependency conventions through `ninja.yaml`. External contributors use the checked-in public NuGet/npm sources; company CI selects the internal build sources separately.

## License

<a id="credits"></a>

Built on [Aiursoft Template](https://github.com/aiursoftweb/template). Licensed under the [MIT License](./LICENSE).
