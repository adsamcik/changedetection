# Installation

ChangeDetection supports a production container and a framework-dependent .NET installation. Docker Compose is the simplest supported path.

## Docker Compose

### Requirements

- Docker Engine 24 or later
- Docker Compose v2
- At least 2 GB of free memory; browser-based checks are more reliable with 4 GB or more

Clone the repository and start the service:

```sh
git clone https://github.com/adsamcik/changedetection.git
cd changedetection
docker compose up --build -d
docker compose ps
```

Open <http://localhost:5171>. The image health check calls `/health`; wait for the container to become `healthy` before troubleshooting the UI.

The default configuration is intentionally loopback-only. Do not change the port binding to `0.0.0.0` while using `Authentication__Mode=SingleUser`: that mode has no login screen and treats every caller as the same administrator.

The Compose project creates three named volumes:

| Volume | Container path | Purpose |
| --- | --- | --- |
| `changedetection-data` | `/data` | LiteDB application database |
| `changedetection-keys` | `/keys` | keys used to encrypt stored LLM credentials |
| `changedetection-backups` | `/app/backups` | application-created database backups |

To stop the application without deleting data:

```sh
docker compose down
```

Do not add `--volumes` unless you deliberately intend to delete all three persistent stores.

## Install from source

### Requirements

- .NET 10 SDK
- PowerShell 7 (`pwsh`)
- A Chromium-compatible host supported by Microsoft Playwright

From the repository root:

```powershell
dotnet restore ChangeDetection.slnx
dotnet build ChangeDetection.slnx
pwsh ./src/ChangeDetection/ChangeDetection/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
dotnet run --project src/ChangeDetection/ChangeDetection/ChangeDetection.csproj
```

On Windows, omit `--with-deps` if the Playwright installer reports that operating-system dependency installation is unsupported. The development profile listens on <http://localhost:5171>.

## Install a published build

Create a framework-dependent release directory:

```powershell
dotnet publish src/ChangeDetection/ChangeDetection/ChangeDetection.csproj --configuration Release --output .artifacts/publish -p:RunAOTCompilation=false
pwsh ./.artifacts/publish/playwright.ps1 install --with-deps chromium
```

Run it from a directory writable by the service account and keep the database and key ring in persistent, access-controlled locations:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5171"
$env:LiteDb__Path = "C:\ProgramData\ChangeDetection\changedetection.db"
$env:Security__DataProtectionKeyRingPath = "C:\ProgramData\ChangeDetection\keys"
dotnet ./.artifacts/publish/ChangeDetection.dll
```

The account running the application must also be the account for which Playwright installed Chromium, unless `PLAYWRIGHT_BROWSERS_PATH` points to a shared browser installation.

## LLM and search providers

The application can auto-detect Ollama. From a container, configure its endpoint as `http://host.docker.internal:11434`; the Compose file provides that host alias on Linux as well as Docker Desktop.

Hosted LLM credentials entered in the settings UI are encrypted in LiteDB. Search-provider credentials must come from configuration. The Compose file maps `CHANGEDETECTION_GOOGLE_CSE_API_KEY`, `CHANGEDETECTION_BRAVE_API_KEY`, and `CHANGEDETECTION_NEWSDATA_API_KEY` into the corresponding ASP.NET configuration keys. See [Security configuration](security.md) before supplying production credentials.

For reverse-proxy, SSO, backup, upgrade, and rollback guidance, continue with [Deployment and operations](deployment.md).
