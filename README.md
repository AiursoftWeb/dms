# DMS

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fdms)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fdms&scope=baseline&ecosystem=all)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.html)

DMS is a server operations platform for humans and AI, built on Aiursoft Template. Request an operation, approve its scope through Lockbox, and follow its execution across your machines.

![DMS local preview](./screenshot.png)

## What is DMS?

- A web console for machine inventory, approvals and workflow history.
- A CLI and reusable SDK for requesting operations and inspecting their results.
- Linux Agents that connect outward to the Controller over WebSocket. Managed machines need access to the Controller, without requiring internet access or an SSH service.
- Cluster permissions with independent request, automatic approval and approval rights for five risk levels.
- Persistent workflow state, parent/child callstacks and ClickHouse trace archives.
- Shared Aiursoft accounts, OIDC, roles, themes and localization.

The current workflows cover Agent status, performance information, service status and restart, block devices, local filesystem capacity, machine inventory and registration. Operational commands submit a Lockbox; inspection and approval commands use authenticated management APIs. Arbitrary shell commands are not exposed.

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

The defaults use SQLite and local accounts. Open **Cluster access** to configure the first cluster and its permissions, then create a personal key under **API Keys**. See [CLI and Agent setup](./docs/cli-and-agent.md) for registration and the first operation.

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

The Controller image does not install Agents on managed machines. Build and distribute the Agent separately using the [Agent setup guide](./docs/cli-and-agent.md#run-an-agent).

## Configuration

Run one Controller with a persistent business database and workflow state directory. Configure ClickHouse for detailed workflow traces before using the service for ongoing operations. Local accounts are the development default; OIDC is an explicit deployment setting.

See [configuration and persistence](./docs/configuration.md) for database settings, trace storage, authentication, recovery and cancellation behavior.

## How to contribute

Report bugs and feature requests through the [issue tracker](https://github.com/AiursoftWeb/dms/issues). Use a feature branch or fork and submit a merge request.

Build and test from the repository root:

```sh
dotnet build Aiursoft.DMS.sln
dotnet test Aiursoft.DMS.sln
./lint.sh
```

NugetNinja maintains the shared Template files and dependency conventions through `ninja.yaml`. External contributors use the checked-in public NuGet/npm sources; company CI selects the internal build sources separately.

## Credits

Built on [Aiursoft Template](https://github.com/aiursoftweb/template). Licensed under the [MIT License](./LICENSE).
