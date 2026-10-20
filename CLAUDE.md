# WorkUp: working notes for agents

Reply to the user in Rioplatense Spanish (voseo). Keep answers short.

There are **two codebases** of the same product. This repository is the full version. The user is also a student on a team building a smaller academic version, and much of the work happens there.

| | Full version (this repo) | Academic version |
|---|---|---|
| Remote | `tomasherrlein/workup`, branch `main` | `ErikaRomeo/WorkUp`, branch `master` |
| Local folder | `workup/` | `workup-academico/` (clone it next to this repo) |
| Code | `WorkUp/WorkUp.sln`: `WorkUp` (web), `WorkUp.BusinessLogic`, `WorkUp.Data`, `WorkUp.Domain`, `WorkUp.Tests` | `workup-academico/WorkUp/`: one project with `Controllers/`, `Servicios/`, `Datos/`, `Models/`, `Models/ViewModels/`, `Views/` |
| Names | English | Spanish |
| Database | `WorkUp.Data/Sql/*.sql`, applied at startup by `WorkUp.Data/DatabaseMigrator.cs` | `BaseDeDatos/WorkUp.sql`, run by hand. Local DB: `DB_WorkUp` on `.\SQLEXPRESS` |
| Dependencies | Everything goes through DI with interfaces | Services create `Datos` classes with `new` |

For the full version, read `WorkUp/README.md` before touching code: it has how to run it, the allowed dependencies between layers and the conventions.

## Using the full version as a reference

The full version solves the same business problems with more structure. When working on the academic version, read the equivalent code here to compare, then write the academic code in its own simpler style. Never copy the full version's architecture into the academic repo.

| Concept | Academic | Full |
|---|---|---|
| Result of a business operation | `Servicios/Resultado.cs` | `WorkUp.BusinessLogic/Common/Result.cs` |
| Transaction shared by several data classes | `Datos/UnidadDeTrabajo.cs` | `WorkUp.BusinessLogic/Abstractions/IUnitOfWork.cs`, `WorkUp.Data/DbSession.cs`, `WorkUp.Data/SqlRepositoryBase.cs` |
| Register business and owner account together | `Servicios/CuentaPropietarioServicio.cs` (`RegistrarNegocio`) | `WorkUp.BusinessLogic/Businesses/RegisterBusinessService.cs` |
| Owner login, cookie and claims | `Controllers/CuentaController.cs` (`CrearSesion`) | `WorkUp/Infrastructure/Auth/OwnerSignIn.cs`, `WorkUp/Areas/Owner/Controllers/AccountController.cs` |
| Cookie setup | `Program.cs` (one cookie, roles `Propietario` / `Cliente`) | `WorkUp/Infrastructure/Auth/AuthenticationSetup.cs` (two schemes) |
| Base class that protects the owner panel | `Controllers/PanelControllerBase.cs` | `WorkUp/Areas/Owner/Controllers/OwnerControllerBase.cs` |

Paths are relative to each version's code folder. They can drift; search by name if one is missing.

## Working on the academic version

**Task plan.** The team follows the guide `Guia-de-tareas-WorkUp-academico.pdf` (tasks T01 to T33). It is not committed in the academic repo; a copy lives in this repo at `docs/academico/Guia-de-tareas-WorkUp-academico.pdf`. Part 2 of the guide is the full recipe, using T09 as the example. To read it without Python, use `pdftotext`.

**Before starting.** `git fetch` and `git pull` on `master`, then check open PRs and branches (`gh pr list -R ErikaRomeo/WorkUp`) to see who is doing what. Never touch another person's branch.

**Recipe for each task** (same order as the guide):

1. `Datos/`: check which methods exist and add the missing ones. Every query filters by `idNegocio`.
2. `Servicios/`: one method per user action. Check the rules first, then save. Return `Resultado.Ok()` or `Resultado.Error(mensaje, campo)`.
3. `Controllers/`: inherit from `PanelControllerBase` and use its `IdNegocio`. GET to show, POST with `[ValidateAntiForgeryToken]` for each form.
4. Register the service in `Program.cs` with `builder.Services.AddScoped<...>()`.
5. `Views/`: small forms inside a list use `TempData` and redirect. Bigger forms use a ViewModel, `ModelState.AddModelError` and `View(modelo)`. Always redirect after a successful POST.

**Rules that have already caused bugs:**

- `idNegocio` comes from the session, never from a form, not even a hidden field.
- Duplicate checks on edit exclude the row being edited: `AND id <> @excluirId` (T06 had `=` and inverted the behavior).
- Child tables with no `idNegocio` column (for example `HorariosTrabajadores`) insert with `INSERT ... SELECT ... FROM Trabajadores WHERE idNegocio = @idNegocio`, so another business's id inserts nothing.
- With `<Nullable>enable</Nullable>`, non-nullable `string` properties get an implicit `[Required]`. When a page binds a nested ViewModel, bind only the part the form sends (`[Bind(Prefix = "Nuevo")]`).
- To check whether a slot is free before saving a turno (T13 and later), use `DisponibilidadServicio.EstaLibre`, not `TurnoDatos.ExisteSuperposicion`: the latter counts cancelled turnos as busy.

**Branches, commits and PRs:**

- Branch from `master` as `feature/tNN-short-name`, for example `feature/t09-tipos-gasto`.
- Conventional commits written in Spanish, one per reviewable unit (data first, then service, controller and views): `feat(tipos-gasto): ...`, `fix(servicios): ...`.
- Never add `Co-Authored-By` or any AI attribution.
- PRs target `master`. The title is `TNN: name`; the description is one or two vague sentences. Another team member merges.

**Checking a task.** The academic repo has no test project. Build to a separate folder so a running app does not lock the files (`dotnet build -o <temp folder>`), run it on a free port, and test with HTTP requests against two businesses: each rule the task names, plus another business's ids returning 404 or changing nothing. Check the stored data with `sqlcmd -S ".\SQLEXPRESS" -E -C -d DB_WorkUp`.

## Working on Linux

The commands above assume Windows. On Linux, .NET, Razor, git and gh work the same; SQL Server is the difference.

- **SQL Server.** LocalDB and `.\SQLEXPRESS` exist only on Windows. Run SQL Server in Docker and use SQL authentication:

  ```bash
  docker run -d --name workup-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<password>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
  ```

- **Connection strings.** Do not edit `appsettings.json`; override them with environment variables:
  - Full version: `ConnectionStrings__WorkUp="Server=localhost,1433;Database=WorkUp;User Id=sa;Password=<password>;TrustServerCertificate=True"`. The migrator creates the database on first start.
  - Academic version: `ConnectionStrings__CadenaConexion="Server=localhost,1433;Database=DB_WorkUp;User Id=sa;Password=<password>;TrustServerCertificate=True"`. Create the database once with `sqlcmd -S localhost -U sa -P '<password>' -C -i BaseDeDatos/WorkUp.sql`.
- **sqlcmd.** `-E` (Windows authentication) does not work; use `-S localhost -U sa -P '<password>' -C`.
- **What does not run on Linux in the full version, by design:**
  - The demo seed (`--seed-demo-data`) only accepts LocalDB (`WorkUp.Data/DemoData/DemoDataSeeder.cs`).
  - The isolated DataProtection keyring of T12 requires Windows DPAPI (`WorkUp/Infrastructure/ServiceRegistration.cs`). It only fails when that option is configured.
  - Several opt-in SQL and HTTP integration tests assume LocalDB or Windows. The default `dotnet test` run does not need them.

  Do not weaken these guards to make them run on Linux.
- **Case-sensitive paths.** A view name, folder or file reference with the wrong case works on Windows and fails on Linux. Match the exact case of the file on disk.
- **Line endings.** Use `git config core.autocrlf input`.

## Study notes

The user studies MVC with an Obsidian vault in the private repo `tomasherrlein/workup-notas` (branch `main`). After a task that introduces a new concept, add a note there:

- Name it `NN Título.md` with the next number, in neutral Spanish, using the existing sections: Objetivo, Antes de leer, Código para leer, Explicación, Experimento, Preguntas (answers folded in `> [!question]-` callouts), Lo que recuerdo, Dudas.
- Never write in **Lo que recuerdo** or **Dudas**. The user writes those.
- Cite code as `path:line` checked against the current code, and link the note from `00 Inicio.md`, `Registro de repasos.md` and the previous note.
- The vault has its own `CLAUDE.md` with the teaching rules; follow it when working there.
