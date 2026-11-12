# Repository Guidelines

## Project Structure & Module Organization

TimeLens is a .NET 11 solution with a React/Vite client. Backend projects live under `src/`: `TimeLens.API` is the main HTTP API, `TimeLens.Ingestion` fetches provider data and writes artifacts to SeaweedFS, and `TimeLens.Insert` consumes artifact messages from RabbitMQ and writes to ClickHouse. `TimeLens.Scheduler` queues ingestion and validation jobs. Shared domain models and interfaces are in `src/TimeLens.Domain`; PostgreSQL and ClickHouse repositories and infrastructure live in `src/TimeLens.Infrastructure`. The client app is in `src/TimeLens.Client`. Unit tests are in `tests/TimeLens.UnitTest`, with fixtures in `tests/TimeLens.UnitTest/TestData`.

## Build, Test, and Development Commands

- `dotnet restore TimeLens.slnx`: restore backend dependencies.
- `dotnet build TimeLens.slnx`: compile all .NET projects.
- `dotnet test TimeLens.slnx`: run xUnit backend tests.
- `dotnet run --project src/TimeLens.API/TimeLens.API.csproj`: run the API locally.
- `cd src/TimeLens.Client && npm install`: install client dependencies.
- `cd src/TimeLens.Client && npm start`: start Vite on `127.0.0.1`.
- `cd src/TimeLens.Client && npm test`: run Vitest.
- `docker compose up -d` or `podman compose up -d`: run the full stack from `docker-compose.yml`.

## Coding Style & Naming Conventions

C# projects use nullable reference types and implicit usings. Keep namespaces, folders, and types aligned with existing patterns such as `Repositories/*Repository.cs`, `Controllers/*Controller.cs`, and request/DTO folders. Use PascalCase for C# types and methods, camelCase for locals and parameters. The client `.editorconfig` requires UTF-8, spaces, 2-space indentation, final newlines, and single quotes in TypeScript.

## Testing Guidelines

Backend tests use xUnit, Moq, and coverlet. Name files after the class or behavior under test, for example `ProductionRepositoryTests.cs` or `CreateWindTurbineCommandHandlerTests.cs`. Keep test fixtures in `TestData` and mark them for output copying when needed. Client tests use Vitest with `*.test.ts` or `*.test.tsx` files.

## Commit & Pull Request Guidelines

Recent history uses Conventional Commit-style subjects such as `feat: Implement ingestion service`, `fix: compact forecast dataset panel`, and `chore: clean up React client`. Keep commits focused and imperative. Pull requests should describe the change, list test commands run, link issues when applicable, and include screenshots for visible UI changes.

## Security & Configuration Tips

Keep secrets out of `appsettings.json` and client source. Use environment variables for connection strings, API URLs, and credentials. Compose defaults include PostgreSQL on `localhost:5432`, ClickHouse on `localhost:8123`, and RabbitMQ management on `localhost:15672`; avoid committing local overrides.
