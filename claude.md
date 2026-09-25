\# Contexto del Proyecto: API REST .NET Core (C#)



Este proyecto es una Web API RESTful desarrollada en .NET 8 / C#. Está diseñada bajo principios de \*\*Clean Architecture\*\* (Capas: Domain, Application, Infrastructure, Api) y \*\*CQRS\*\* para garantizar alta escalabilidad, desacoplamiento y testabilidad.



\---



\## 🛠️ Comandos Frecuentes



\- \*\*Compilar solución:\*\* `dotnet build`

\- \*\*Ejecutar API localmente:\*\* `dotnet run --project src/Api/Api.csproj`

\- \*\*Ejecutar todos los tests:\*\* `dotnet test`

\- \*\*Ejecutar tests unitarios:\*\* `dotnet test tests/UnitTests/UnitTests.csproj`

\- \*\*Agregar migración EF Core:\*\* `dotnet ef migrations add <NombreMigracion> --project src/Infrastructure --startup-project src/Api`

\- \*\*Actualizar base de datos:\*\* `dotnet ef database update --project src/Infrastructure --startup-project src/Api`



\---



\## 🏛️ Reglas de Arquitectura y Estructura



1\. \*\*Domain (`src/Domain`)\*\*:

&#x20;  - Contiene entidades de negocio, Value Objects, Excepciones de Dominio y Eventos de Dominio.

&#x20;  - \*\*Sin dependencias externas\*\*: No debe hacer referencia a Entity Framework, ASP.NET o librerías de terceros.

&#x20;  - Usar `records` para Value Objects y clases selladas (`sealed`) para Entidades cuando sea posible.



2\. \*\*Application (`src/Application`)\*\*:

&#x20;  - Contiene la lógica de aplicación usando CQRS (MediatR / FastEndpoints).

&#x20;  - Define interfaces de repositorios, DTOs (Data Transfer Objects), Validators (FluentValidation) y Handlers.

&#x20;  - Todo flujo de entrada debe ser validado antes de procesar.



3\. \*\*Infrastructure (`src/Infrastructure`)\*\*:

&#x20;  - Implementa interfaces de Application: DbContext (EF Core), acceso a datos, llamadas a APIs externas, servicios de Email/S3, etc.

&#x20;  - Mapeos de base de datos explicitos mediante `IEntityTypeConfiguration<T>`.



4\. \*\*Api (`src/Api`)\*\*:

&#x20;  - Capa de presentación HTTP (Controllers o Minimal APIs).

&#x20;  - \*\*Gordos en contratos, delgados en lógica\*\*: Los controladores solo reciben request, delegan a Application via MediatR/Services y retornan `IResult` o `ActionResult`.

&#x20;  - Manejo global de excepciones mediante middleware (`ProblemDetails` RFC 7807).



\---



\## 📝 Buenas Prácticas y Estilo de Código (C#)



\### Principios Generales

\- \*\*SOLID\*\*: Respetar estrictamente Single Responsibility e Inversion of Control.

\- \*\*Inyección de Dependencias\*\*: Usar constructor injection siempre. Evitar el patrón Service Locator.

\- \*\*Asincronía (`async`/`await`)\*\*:

&#x20; - Todo I/O (I/O bound) debe ser asíncrono.

&#x20; - Pasar `CancellationToken` a lo largo de toda la cadena de llamadas asíncronas.

&#x20; - Evitar `.Result` o `.Wait()` para no bloquear hilos.



\### Manejo de Errores y Validaciones

\- Usar el patrón \*\*Result Pattern\*\* (`Result<T>` / `OneOf`) o Excepciones de Dominio para errores de negocio.

\- Retornar respuestas estándar utilizando la especificación \*\*ProblemDetails\*\* (`RFC 7807`).

\- Usar `FluentValidation` para validar los DTOs de entrada.



\### Naming Conventions \& Types

\- `PascalCase` para métodos, clases, propiedades y namespaces.

\- `camelCase` para variables locales y parámetros de métodos.

\- `\_camelCase` para campos privados de clase.

\- Utilizar \*\*Nullable Reference Types\*\* activados (`<Nullable>enable</Nullable>`).

\- Preferir tipos explícitos en APIs públicas y `var` cuando el tipo sea evidente.



\---



\## 🧪 Estrategia de Testing



\- \*\*Tests Unitarios\*\*: Probar lógica de dominio y comandos/consultas de Application usando `xUnit`, `FluentAssertions` y `NSubstitute` / `Moq`.

\- \*\*Tests de Integración\*\*: Usar `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`) y `Testcontainers` (si se usa SQL Server / PostgreSQL).

\- Nomenclatura de tests: `NombreMetodo\_Escenario\_ResultadoEsperado`.

