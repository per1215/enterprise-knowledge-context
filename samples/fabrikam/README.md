# Fabrikam Insurance (sample estate)

A small, fictional insurance-claims system used by EKC's tests and demos. It is **not meant to be built or run**: EKC reads source code only.
It deliberately mixes patterns found in real estates:

| Service | Patterns |
|---|---|
| `portal-web` | minimal APIs, typed `HttpClient` to claims-api |
| `claims-api` | MVC controller, MediatR-style command + handler, typed client to policy-api, EF Core with `ToTable`, business rules (attribute + comment), integration events |
| `policy-api` | minimal APIs with `MapGroup`, EF Core |
| `fraud-worker` | event subscription (`AddSubscription<TEvent, THandler>`), publishes the result |
| `payments-api` | MassTransit-style `IConsumer<T>`, minimal API lambda reading EF |
| `notifications-worker` | event subscription |

Orchestration: .NET Aspire AppHost. Ownership: `CODEOWNERS`. Business processes/domains: `.ekc/overlay.yaml`.
