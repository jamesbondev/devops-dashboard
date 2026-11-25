# Orders Service

Order intake API plus a Service Bus queue listener that projects order events.

## Architecture

Clean architecture: `Orders.Domain` <- `Orders.Application` <- `Orders.Infrastructure` <- `Orders.Api` / `Orders.QueueListener`.

## Running locally

```bash
dotnet build
dotnet test
skaffold dev --profile dev
```

## Deployment

The `azure-pipelines.yml` pipeline extends the shared templates; merges to `main` deploy to dev, test and prod
through AKS environments. The database schema is deployed as a dacpac from `database/Orders.Database`.
