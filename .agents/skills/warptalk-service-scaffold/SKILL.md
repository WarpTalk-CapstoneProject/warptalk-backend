---
name: warptalk-service-scaffold
description: Initialize a new WarpTalk microservice with Clean Architecture, EF Core scaffold, Generic Repository, and Unit of Work.
---

# WarpTalk Service Scaffold

This skill automates the process of creating a new backend microservice for the WarpTalk Capstone Project. It enforces the following architecture:
- **Clean Architecture**: Domain, Application, Infrastructure, API layers.
- **Entity Framework Core**: Database-first scaffold using Docker PostgreSQL and an init-db.sql schema.
- **Repository Pattern**: Extends `IGenericRepository<T>` and `IUnitOfWork` for clean data access.

## Prerequisites
1. Name of the service (e.g., `MeetingService`, `TranscriptService`).
2. A PostgreSQL database defined in the project's init schema (e.g. `init-db.sql`).

## Step-by-Step Instructions

1. **Verify Database Schema**
   Ensure `warptalk-backend/database/init-db.sql` matches the structure needed for the target service (e.g., specific schema like `meeting`, `transcript`).

2. **Scaffold Folder Structure & Projects**
   Create a folder `warptalk-backend/<service-name>` and set up the .sln and core projects:
   ```bash
   dotnet new slnx -n WarpTalk.<ServiceName>
   dotnet new classlib -n WarpTalk.<ServiceName>.Domain -o src/WarpTalk.<ServiceName>.Domain
   dotnet new classlib -n WarpTalk.<ServiceName>.Application -o src/WarpTalk.<ServiceName>.Application
   dotnet new classlib -n WarpTalk.<ServiceName>.Infrastructure -o src/WarpTalk.<ServiceName>.Infrastructure
   dotnet new webapi -n WarpTalk.<ServiceName>.API -o src/WarpTalk.<ServiceName>.API
   dotnet new xunit -n WarpTalk.<ServiceName>.Tests -o tests/WarpTalk.<ServiceName>.Tests
   ```
   *Delete any auto-generated `.cs` files (like `Class1.cs` and `WeatherForecast.cs`).*

3. **Wire Up Project References**
   - Application depends on Domain and `WarpTalk.Shared`.
   - Infrastructure depends on Domain and Application.
   - API depends on all.
   - Tests depend on API, App, Infra, Domain.
   *(Make sure to add project references using `dotnet add reference` commands)*

4. **Add NuGet Dependencies**
   - **API**: `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design` (all v10.0.5 or latest consistent version).
   - **Infrastructure**: `Microsoft.EntityFrameworkCore` (v10.0.5).

5. **Entity Framework Schema Scaffolding**
   - Spin up a temporary postgres container using port 5432.
   - Run the `init-db.sql` script (using `docker exec -i <container-id> psql ...`).
   - Run `dotnet ef dbcontext scaffold` against the temporary database to generate Entities under the `Domain/Entities` folder and `DbContext` under `Infrastructure/Persistence`.
   - Destroy the docker container.

6. **Implement Pattern: Generic Repository and UnitOfWork**
   - **Domain Layer**: Create `IGenericRepository<T>.cs` and `IUnitOfWork.cs`. Note: The generic repository MUST support include properties mapping:
     `Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);`
   - **Infrastructure Layer**: Develop `GenericRepository<T>.cs` using `IQueryable<T>.Include` for nested loading.
   - **Infrastructure Layer**: Develop `UnitOfWork.cs` maintaining the `DbContext` lifetime and exposing `IGenericRepository<T>` instances for all scaffolded entities.

7. **Implement Application Layer**
   - Create Service Interface (e.g. `IMeetingService`).
   - Create Service Implementations invoking the `UnitOfWork` instances injected.
   - Create DTO schemas/records.

8. **Implement API Controller & DI Setup**
   - Mount endpoints using traditional Controllers with correct attributes.
   - Add implementations into `Program.cs`. Example Setup:
     `builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();`
     `builder.Services.AddScoped<IServiceName, ServiceNameImpl>();`
   - Ensure `appsettings.json` connection strings refer correctly to the shared or isolated database logic.

## Recommended Code Snippets

**IGenericRepository.cs Example:**
```csharp
using System.Linq.Expressions;
namespace WarpTalk.ServiceName.Domain.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(string includeProperties = "", CancellationToken ct = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
    IQueryable<T> Query();
}
```

By adhering properly, developers save ~2h of baseline architectural boilerplate each time they initialize a new sub-service inside the WarpTalk suite.
