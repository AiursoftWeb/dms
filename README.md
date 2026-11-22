# Aiursoft DMS

<a name="dms"></a>

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/dms/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/dms/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fdms)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fdms&scope=baseline&ecosystem=all)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dms.html)

DMS is a server operations platform for humans and AI, built on Aiursoft Template. Request an operation, approve its scope through Lockbox, and follow its execution across your machines.

![DMS local preview](https://raw.githubusercontent.com/aiursoftweb/dms/master/screenshot.png)

On an empty database, the default local account is `admin@default.com` with password `Admin@123456!`. Change the password before exposing the application.

## Overview

### What is DMS?

- A web console for machine inventory, approvals and workflow history.
- A CLI and reusable SDK for requesting operations and inspecting their results.
- Linux Agents that connect outward to the Controller over WebSocket. Managed machines need access to the Controller, without requiring internet access or an SSH service.
- Role permissions for six risk levels, including automatic approval for Open requests.
- Persistent workflow state, parent/child callstacks and ClickHouse trace archives.
- Shared Aiursoft accounts, OIDC, roles, themes and localization.

The current workflows cover approved Agent upgrades and status, Docker container listing, logs, execution and stopping, scoped Box Puller mirror updates, performance information, service status and restart, block devices, local filesystem capacity, machine inventory and registration. Operational commands submit a Lockbox; inspection and approval commands use authenticated management APIs. Container execution requires VeryHigh permission and approval; host shell commands are not exposed.

## CLI & SDK

### CLI

Install the CLI with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
dotnet tool install --global Aiursoft.DMS.Client
```

Update an existing installation:

```bash
dotnet tool update --global Aiursoft.DMS.Client
```

Remove the CLI:

```bash
dotnet tool uninstall --global Aiursoft.DMS.Client
```

Run `dms --help` to explore commands.

The machine Agent is also available as a .NET tool starting with `Aiursoft.DMS.Agent` 1.1.1:

```bash
dotnet tool install --global Aiursoft.DMS.Agent
dms-agent --help
```

For managed servers, use the [one-time Agent installer](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md#install-and-join-an-agent). On Ubuntu and AnduinOS, it prepares `dotnet10` through APT, installs the Tool package supplied by the Controller and registers the systemd service. The tool also supports foreground development; see the same guide for `join` and `run`.

Run the interactive login, follow its link to create a personal API key, then check your identity:

```sh
dms login
dms access whoami
dms machine list
```

The CLI uses table output by default; add `--output-format json` or `csv` for structured output. Identity and administrative inspection queries do not create Lockboxes. `machine list` requires **Run open workflows**, automatically approves its Lockbox and prints the inventory. See [CLI and Agent setup](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md) for PATH configuration, authentication, approvals and operation examples.

### SDK

Add the SDK to your .NET project after version `10.1.0` is published by the default-branch release pipeline:

```bash
dotnet add package Aiursoft.DMS.SDK
```

The SDK connects to your own DMS server; install and configure that server separately. The existing release pipeline publishes the same package to NuGet.org and the internal Nupkg registry. See the [SDK source](https://github.com/aiursoftweb/dms/tree/master/src/Aiursoft.DMS.SDK) for the available client methods and models.

## Try

Try a running DMS [here](https://dms.aiursoft.com). Sign in with an authorized company account. Role permissions determine which risk levels you can request and approve across all machines.

## Install with Docker

Run the installer with the desired port (omit it to choose an available port):

```bash
curl -fsSL https://raw.githubusercontent.com/aiursoftweb/dms/master/install.sh | sudo bash -s -- 8080
```

Installation starts after three seconds; press Ctrl+C to cancel. Then open `http://localhost:8080`.

This installs the Controller; managed machines join separately through its Agent installer.

## Run manually

Requirements about how to run

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/).
2. The .NET build automatically runs `npm ci` and `npm run build` to prepare the frontend assets. Node.js and npm must be available when building from source.
3. Execute `dotnet run --project src/Aiursoft.DMS/Aiursoft.DMS.csproj -- --urls http://localhost:5000` to run the app.
4. Use your browser to view [http://localhost:5000](http://localhost:5000).

The defaults use SQLite and local accounts. Create a personal key under **API Keys**. Open **Machines** to see registered Agents and their connection status. Use the CLI to manage inventory and issue join keys. Configure workflow risk permissions on the existing **Roles** page. See [CLI and Agent setup](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md) for registration, credential recovery and the first operation.

## Run in Microsoft Visual Studio

1. Install the requirements described above; the build prepares the frontend assets.
2. Open `Aiursoft.DMS.sln`, select `Aiursoft.DMS` as the startup project, and press `F5`.

## Run in Docker

First, install Docker [here](https://docs.docker.com/get-docker/).

Then run the following commands in a Linux shell:

```bash
image=aiursoft/dms
appName=dms
sudo docker pull $image
sudo docker run -d --name $appName --restart unless-stopped -p 5000:5000 --mount type=volume,source=$appName-data,target=/data $image
```

That will start a web server at `http://localhost:5000` and you can test the app.

The docker image has the following context:

| Properties  | Value                           |
|-------------|---------------------------------|
| Image       | aiursoft/dms               |
| Ports       | 5000                            |
| Binary path | /app                            |
| Data path   | /data                           |
| Config path | /data/appsettings.json          |

Keep `/data` persistent for configuration, the SQLite database, uploaded files and Data Protection keys. To use a host directory instead of the named volume, replace the mount argument with `-v /var/www/$appName:/data`.

The health endpoint is `/health`. The workflow state directory is `/data/dms-state`; persist it along with the business database. The shared CI validates Linux amd64 and arm64 images.

To build a local Controller image from the repository root instead of pulling the published image:

```sh
docker build --tag dms:local .
docker run -d --name dms --restart unless-stopped \
  -p 8080:5000 -v dms-data:/data dms:local
```

Open `http://localhost:8080` for this local-build example. The standard [installer](https://github.com/aiursoftweb/dms/blob/master/install.sh) installs the published `aiursoft/dms:latest` image with a chosen port:

```sh
sudo bash install.sh 8080
```

The Controller image includes the Agent Tool package and Linux x64 and arm64 upgrade helpers. Run its installation command once on each managed host, then approve registration through Lockbox. See [Agent installation](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md#install-and-join-an-agent).

## Usage

### Configure CLI access

Create a personal key under **API Keys** in the Controller, then set its URL and your key:

```bash
export DMS_SERVER=https://dms.example.com
export DMS_API_KEY='<personal-key>'
dms access whoami
dms machine list
```

`access whoami` shows your effective role permissions. `machine list` requires **Run open workflows**, automatically approves its Lockbox and prints the inventory. Other server operations follow the configured request and approval permissions for their risk level. Self approval is a separate permission. Use the returned IDs with `lockbox get`, `operation get-workflow`, `operation get-callstack` and `operation get-trace`. Supported commands use table output by default, with `--output-format json` or `csv` for export.

### Join and manage machines

An administrator creates a Controller join key; an operator runs the [one-time Agent installation](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md#install-and-join-an-agent) on each host. The service waits for registration approval, then connects outward to the Controller. Subsequent operations, including [Agent upgrades](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md#upgrade-an-installed-agent), run through approved workflows. Keep personal keys, join keys and machine credentials separate.

## Configuration

<a name="configuration-guide"></a>

The defaults run as a standalone application with SQLite, local accounts, local file storage, and no ClickHouse server. Change a setting only after preparing the dependency named in its comment.

The Docker image copies this file to `/data/appsettings.json` on its first start and keeps that copy on later starts. Mount `/data` as a persistent volume. To override a value without editing the file, use an environment variable with double underscores, such as `Storage__Path` or `AppSettings__OIDC__ClientSecret`.

```jsonc
{
  "Dms": {
    "StatePath": "dms-state",
    "Clickhouse": {
      "Enabled": false,
      "ConnectionString": "Host=localhost;Protocol=http;Port=8123;Username=default;Password=;Database=default"
    }
  },
  "ConnectionStrings": {
    // True: cache EF Core query results in this process. False: read from the database on every query.
    "AllowCache": "True",

    // Sqlite: use a local database file and no database server. MySql: use an external MySQL server.
    // When changing this value, also replace DefaultConnection with a connection string for that provider.
    "DbType": "Sqlite",
    // With Sqlite, this creates app.db in the process working directory and applies migrations at startup.
    "DefaultConnection": "DataSource=app.db;Cache=Shared"

    // MySql requires a reachable server, database, user, and password. The application applies migrations at startup.
    // sudo docker run -d --name db -e MYSQL_RANDOM_ROOT_PASSWORD=true -e MYSQL_DATABASE=template -e MYSQL_USER=template -e MYSQL_PASSWORD=template_password -p 3306:3306 hub.aiursoft.com/mysql
    //"DbType": "MySql",
    //"DefaultConnection": "Server=localhost;Database=template;Uid=template;Pwd=template_password;"
  },
  "Storage": {
    // Writable local directory for public Workspace files and token-protected private Vault files.
    // This is not an S3 or MinIO bucket.
    // The Docker image changes /tmp/data to /data; mount /data as a persistent volume in production.
    "Path": "/tmp/data",

    // Optional second hostname for this application's own /download endpoints; this is not an S3 or MinIO URL.
    // Example: set https://files.example.com, point that DNS name to this server, and add this Caddy route:
    // files.example.com {
    //     reverse_proxy 127.0.0.1:5000
    // }
    // Replace 127.0.0.1:5000 with the same upstream used by the web UI. Leave empty to use the UI hostname.
    "PublicOrigin": "",

    // False: verified images and configured media can open on the normal web hostname.
    // True: inline viewing works only through PublicOrigin; the same request on the UI hostname downloads the file.
    // True requires PublicOrigin plus DNS, TLS, and a reverse-proxy route to this same application instance.
    "RequireDedicatedInlineOrigin": false,

    // False: only verified images and SafeInlineMediaExtensions open in the browser; every other file downloads.
    // True: every file type may open through PublicOrigin, and HTML or SVG scripts may run there.
    // True requires RequireDedicatedInlineOrigin=true and a PublicOrigin that receives no application cookies.
    "AllowArbitraryInlineOnDedicatedOrigin": false,

    // File extensions allowed to open after content-signature validation. Empty means no extra audio, video, or SVG.
    // Supported values are mp3, mp4, ogg, svg, wav, and webm. This does not grant upload permission.
    "SafeInlineMediaExtensions": ["svg"],

    // SVG opens only when svg is enabled above and its Workspace path starts with an entry in this list.
    // Example: [ "markdown-images" ]. Empty means every SVG downloads.
    "SafeInlineSvgSubfolders": ["project-logo"],

    // These values are copied into newly issued signed upload tokens; existing tokens keep their old policy.
    "UploadPolicy": {
      // False: a valid signed upload token is enough. True: the uploader must also be signed in to this application.
      "RequireAuthenticatedUser": false,

      // False: preserve spaces in uploaded file names. True: save each space as a hyphen.
      "ReplaceSpacesWithHyphens": false
    }
  },
  "AppSettings": {
    // Local: use built-in accounts and passwords. OIDC: redirect sign-in to an external identity provider.
    // OIDC also requires the OIDC section below and a provider callback URL of https://<web-host>/signin-oidc.
    "AuthProvider": "Local",

    // Empty: assign no automatic role. Otherwise, assign this role to every new local or OIDC user.
    // With Local, create the role first. With OIDC, the application creates it during account synchronization.
    "DefaultRole": "",

    // False: sign-in ends when the browser session closes. True: issue a persistent sign-in cookie.
    "PersistsSignIn": false,

    // This section is used only when AuthProvider is OIDC.
    "OIDC": {
      // Provider discovery base URL. It must be reachable by the application and should use HTTPS in production.
      "Authority": "https://auth.aiursoft.com/application/o/dms",
      // Client identifier created for this application in the OIDC provider.
      "ClientId": "",
      // Client secret created by the provider. Set AppSettings__OIDC__ClientSecret instead of committing a real secret.
      "ClientSecret": "",

      // Claim containing role names. On every login, local roles are replaced by these roles plus DefaultRole.
      "RolePropertyName": "groups",
      // Required claim used as the local username when an OIDC account is first created.
      "UsernamePropertyName": "preferred_username",
      // Required claim copied to the local display name on every login.
      "UserDisplayNamePropertyName": "name",
      // Required unique email claim. The provider must keep it verified, unique, and stable.
      "EmailPropertyName": "email",
      // Required immutable account identifier. Keep this mapped to the standard OIDC sub claim.
      "UserIdentityPropertyName": "sub"
    },

    // This section is used only when AuthProvider is Local.
    "Local": {
      // True: show self-registration and allow new local accounts. False: existing users can sign in, but cannot register.
      "AllowRegister": true,
      // True: require only six characters. False: require eight characters with upper, lower, digit, and symbol.
      "AllowWeakPassword": true
    }
  },
  "GlobalSettings": {
    // Leave an entry commented to use the database value or built-in default.
    // Uncommenting an entry forces that value and overrides changes made in the administration UI.

    // True: users may change their display nickname. False: only administrators control it.
    // "Allow_User_Adjust_Nickname": "True",
    // Text shown as the brand name in the footer.
    // "BrandName": "Aiursoft",
    // URL opened when a user clicks the footer brand name.
    // "BrandHomeUrl": "https://www.aiursoft.com/",
    // Mainland China ICP license number. Empty hides it.
    // "Icp": "",
    // Application name shown in the web interface.
    // "ProjectName": "Aiursoft DMS"
  },
  "Logging": {
    "LogLevel": {
      // Minimum level for application logs. Typical values: Debug, Information, Warning, Error, or None.
      "Default": "Information",
      // Separate minimum level for ASP.NET Core framework logs; Warning reduces routine request noise.
      "Microsoft.AspNetCore": "Warning"
    },
    "Clickhouse": {
      // False: do not send logs to ClickHouse. True: create TableName at startup and send logs in batches.
      // Before enabling, provide a reachable ClickHouse server and valid credentials in ConnectionString.
      "Enabled": false,
      // ClickHouse host, protocol, port, credentials, and database. Used only when Enabled is true.
      "ConnectionString": "Host=localhost;Protocol=http;Port=8123;User=default;Password=;Database=AspNetCoreLogs",
      // ClickHouse table created and used for application logs when Enabled is true.
      "TableName": "DmsAppLogs"
    }
  },
  // ASP.NET Core Host-header allowlist; this is not a CORS setting.
  // * accepts every hostname. In production, use semicolon-separated names such as example.com;www.example.com.
  "AllowedHosts": "*"
}
```

### Shared settings

- `ConnectionStrings:AllowCache` caches EF Core query results in-process. Choose `Sqlite` for a local file or `MySql` for a prepared external database; change `DefaultConnection` with the provider. Migrations are applied at startup.
- `Storage:Path` is writable local file storage for Workspace and Vault. `PublicOrigin` is an optional second hostname routed to this same application's downloads, not an object-storage endpoint. Dedicated inline viewing requires its own DNS, TLS and reverse-proxy route; arbitrary inline content must use a cookie-free dedicated origin. Media/SVG allowlists constrain inline rendering, not upload permission. Upload token policy changes apply to newly issued tokens.
- `AppSettings:AuthProvider` selects `Local` accounts or `OIDC`. OIDC requires a reachable authority, client settings and the HTTPS `/signin-oidc` callback. Configure role, username, display-name, email and immutable `sub` claims; synchronized OIDC roles and `DefaultRole` govern local membership. `PersistsSignIn` controls persistent cookies. Local registration and password requirements follow `AppSettings:Local`.
- Uncommented `GlobalSettings` values override the database-backed administration UI; commented entries leave its values or built-in defaults in use.
- `Logging:LogLevel` controls application/framework verbosity. `Logging:Clickhouse` is optional application-log export and requires a prepared reachable database when enabled.
- `AllowedHosts` is the ASP.NET Core Host-header allowlist, not CORS. Use the intended public hostnames for a deployment.

Run one Controller with a persistent business database and workflow state directory. Configure ClickHouse for detailed workflow traces before using the service for ongoing operations. Local accounts are the development default; OIDC is an explicit deployment setting.

See [configuration and persistence](https://github.com/aiursoftweb/dms/blob/master/docs/configuration.md) for database settings, trace storage, authentication, recovery and cancellation behavior.

## Documentation

- [Issue tracker](https://github.com/AiursoftWeb/dms/issues)
- [CLI, authentication and Agent setup](https://github.com/aiursoftweb/dms/blob/master/docs/cli-and-agent.md)
- [Configuration, persistence, recovery and cancellation](https://github.com/aiursoftweb/dms/blob/master/docs/configuration.md)
- [All Workflows](https://github.com/aiursoftweb/dms/blob/master/docs/all-workflows.md)
- [Available workflows and role permissions](https://github.com/aiursoftweb/dms/blob/master/docs/workflow-discovery.md)
- [Workflow authoring](https://github.com/aiursoftweb/dms/blob/master/docs/authoring-workflows.md)

## How to contribute

There are many ways to contribute to the project: logging bugs, submitting pull requests, reporting issues, and creating suggestions.

Even if you have push rights on the repository, you should create a personal fork and create feature branches there when you need them. This keeps the main repository clean and your workflow cruft out of sight.

We're also interested in your feedback on the future of this project. You can submit a suggestion or feature request through the issue tracker. To make this process more effective, we're asking that these include more information to help define them more clearly.

Build and test from the repository root:

```sh
dotnet build Aiursoft.DMS.sln
dotnet test Aiursoft.DMS.sln
./lint.sh
```

NugetNinja maintains the shared Template files and dependency conventions through `ninja.yaml`. External contributors use the checked-in public NuGet/npm sources; company CI selects the internal build sources separately.

## License

<a name="credits"></a>

Built on [Aiursoft Template](https://github.com/aiursoftweb/template). Licensed under the [MIT License](https://github.com/aiursoftweb/dms/blob/master/LICENSE).
