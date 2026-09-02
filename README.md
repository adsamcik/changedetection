# ChangeDetection

ChangeDetection is a self-hosted web application that turns natural-language monitoring requests into repeatable website checks. It can use hosted LLM providers or a local Ollama instance and stores its state in an embedded LiteDB database.

## Quick start with Docker

Prerequisites: Docker Engine 24+ with the Compose plugin.

```sh
git clone https://github.com/adsamcik/changedetection.git
cd changedetection
docker compose up --build -d
```

Open <http://localhost:5171>. The supplied Compose configuration binds only to loopback because the default `SingleUser` mode has no login authentication.

For other installation methods and production configuration, see:

- [Installation](docs/installation.md)
- [Deployment and operations](docs/deployment.md)
- [Security configuration](docs/security.md)
- [Release process](docs/releasing.md)

## Development

The repository requires the .NET 10 SDK and PowerShell 7. Restore and build the solution, install the Playwright browser, then run the application:

```powershell
dotnet restore ChangeDetection.slnx
dotnet build ChangeDetection.slnx
pwsh ./src/ChangeDetection/ChangeDetection/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet run --project src/ChangeDetection/ChangeDetection/ChangeDetection.csproj
```

All tests must run through the repository wrapper:

```powershell
./test.ps1
./test.ps1 -Project tests/ChangeDetection.Core.Tests
./test.ps1 -Project tests/ChangeDetection.UITests
```

Pass `-Configuration Release` when validating an existing Release build.

## License

ChangeDetection is available under the [MIT License](LICENSE).
