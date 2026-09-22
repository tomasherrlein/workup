# WorkUp: código

Aplicación web MVC en capas que implementa `docs/srs.md` v3.0. El plan de tareas está en `odd/tasks/workup-mvp.md`; este archivo define **cómo** se escribe el código. Toda tarea lo sigue.

## Cómo correrlo

Requisitos: SDK de .NET 9 o superior y SQL Server (LocalDB, que viene con Visual Studio, o SQL Server Express).

```bash
dotnet run --project WorkUp/WorkUp --launch-profile https
```

- La primera vez crea la base `WorkUp` y aplica los scripts de `WorkUp.Data/Sql`. Si tu SQL Server no es LocalDB, cambiá `ConnectionStrings:WorkUp` en `WorkUp/appsettings.json` (por ejemplo `Server=.\SQLEXPRESS;Database=WorkUp;Trusted_Connection=True;TrustServerCertificate=True`).
- En desarrollo están activas las herramientas de prueba (`WorkUp:Features:DevTools`): los emails no se envían, quedan en la bandeja de salida.
- Pruebas: `dotnet test WorkUp/WorkUp.sln`.

### Semilla local de datos de demostración (T07)

La semilla es una operación independiente y explícita; **nunca** corre al iniciar la aplicación, en una migración ni por activar DevTools. Solo funciona con las tres protecciones a la vez: argumento `--seed-demo-data`, entorno `Development` y configuración `WorkUp:DemoData:Enabled=true`. También requiere `WORKUP_DEMO_PASSWORD` en el entorno del proceso; no hay una contraseña incluida en el código ni en este documento. Las dos cuentas de demo usan esa contraseña elegida localmente y se guarda únicamente su hash con el hasher de Identity.

Usá siempre una base LocalDB de desarrollo dedicada y descartable, nunca producción ni una base que contenga negocios de personas. La semilla acepta únicamente la instancia `(localdb)\MSSQLLocalDB`, autenticación integrada y un nombre de base que empiece con `WorkUpDemo` o `WorkUpT07Demo`; rechaza la base compartida predeterminada `WorkUp`. Por ejemplo, configurá `ConnectionStrings__WorkUp` como `Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=WorkUpDemoT07SeedRun;Integrated Security=True;TrustServerCertificate=True`. Para crear primero el esquema en esa base nueva, iniciá normalmente la app una vez y detenela. La semilla no ejecuta migraciones. El comando exacto de siembra es `dotnet run --project WorkUp/WorkUp -- --seed-demo-data`. En una terminal PowerShell local dedicada, el siguiente ejemplo solicita la contraseña sin mostrarla ni guardarla en el historial:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__WorkUp = 'Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=WorkUpDemoT07SeedRun;Integrated Security=True;TrustServerCertificate=True'
$env:WorkUp__DemoData__Enabled = 'true'
$securePassword = Read-Host -AsSecureString 'Contraseña local para las cuentas demo'
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $env:WORKUP_DEMO_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    dotnet run --project WorkUp/WorkUp -- --seed-demo-data
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    Remove-Item Env:WORKUP_DEMO_PASSWORD, Env:WorkUp__DemoData__Enabled, Env:ConnectionStrings__WorkUp, Env:ASPNETCORE_ENVIRONMENT
}
```

Las cuentas creadas son `marina.demo` y `lucia.demo`; la contraseña la define quien ejecuta el comando. Los negocios de Marina y Lucía, las trabajadoras/profesoras, teléfonos, CUIL, precios y horarios del plan son ficticios o sintéticos y solo para demo. Los emails terminan en `.invalid` para impedir el envío a direcciones reales. Si los identificadores demo ya existen con datos distintos o incompletos, la operación se detiene sin modificarlos; una repetición sobre los datos demo intactos no duplica filas.

## Estructura

```
WorkUp.Domain         Entidades, enums y reglas puras. Sin dependencias ni IO.
WorkUp.BusinessLogic  Servicios de negocio por módulo, interfaces de repositorio y puertos (IClock, IUnitOfWork...).
WorkUp.Data           ADO.NET: DbSession, repositorios, migrador y scripts SQL.
WorkUp                MVC: controladores, view models, vistas, autenticación, tareas programadas, composición.
WorkUp.Tests          xUnit sobre dominio y negocio, con repositorios falsos en memoria.
```

Dependencias permitidas: `WorkUp → BusinessLogic, Data, Domain` · `Data → BusinessLogic, Domain` · `BusinessLogic → Domain` · `Tests → BusinessLogic, Domain`. La capa de negocio se llama `BusinessLogic` y no `Business` para no chocar con la entidad `Business` (el negocio). Negocio no conoce SQL ni HTTP; la web no escribe SQL.

### Módulos

Cada módulo usa el mismo nombre de carpeta en `BusinessLogic`, `Data`, `WorkUp/ViewModels` y `WorkUp.Tests`.

| Módulo | Contenido | Tareas |
|---|---|---|
| `Businesses` | Negocio, cuenta de la dueña, tipos de gasto | T01 |
| `Catalog` | Servicios, tipos de clase, precios de abono | T02 |
| `Staff` | Trabajadoras, horarios, pausas | T03 |
| `Clients` | Fichas, observaciones, cuentas de cliente, tokens, invitaciones | T05, T20, T22 |
| `Scheduling` | Disponibilidad, turnos, anotaciones, enlace privado, atención sin turno, jornada | T08–T12, T14 |
| `Classes` | Grilla, generación de la semana, lista de la clase, suspensión | T04, T13, T16 |
| `Memberships` | Inscripciones, clases fijas, períodos de abono | T15, T17 |
| `Payments` | Cobros, anulaciones, devoluciones | T18 |
| `Expenses` · `Supplies` | Gastos · insumos | T06 |
| `Reports` | Resultado del período e indicadores | T19 |
| `Notifications` | Bandeja de salida, plantillas de email, tareas programadas | T23–T26 |

### Secciones del panel

La navegación ya existe (`WorkUp/Infrastructure/OwnerNavigation.cs`). Cada sección es un controlador del área `Owner` con acción `Index`, en `/panel/{Controlador}`:

`Day` (Jornada) · `Agenda` · `Classes` · `Enrollments` (Abonos) · `Clients` · `Payments` (Cobros) · `Expenses` · `Supplies` · `Reports` · `Settings` · `DevTools` · `Account` (ingreso, registro y salida; anónimo salvo `Logout`).

Rutas públicas con atributos: `/r/{slug}` reserva pública · `/t/{token}` enlace "Ver mi turno" · `/r/{slug}/cuenta/...` cuenta de cliente (`ingresar`, `salir`, `registro`, `recuperar`...) · `/r/{slug}/mis-turnos` panel del cliente.

## Convenciones

### Idioma

- Código, identificadores, comentarios y nombres de tablas: **inglés**. El mapeo con el glosario del SRS está en el plan (`odd/tasks/workup-mvp.md`, sección 2).
- Textos visibles: **español rioplatense simple** (`PRODUCT.md`), cortos y con el vocabulario cerrado del SRS §1.4: `Pendiente`, `Confirmado`, `Atendido`, `Cobrado`, `Cancelado`, `Ausente`, `Programado`, `Suspendido`, `Fija`, `Suelta`, `Vigente`, `Vencido`, `Pendiente de renovación`. No inventes sinónimos.

### Dominio (`WorkUp.Domain`)

- Entidades POCO con propiedades públicas; reglas puras como métodos o clases estáticas en `Rules/` (ejemplo: `MembershipCalendar`).
- Nada de fechas del sistema: toda regla que dependa de "ahora" recibe la fecha como parámetro.
- Los valores de los enums se guardan como `TINYINT`: nunca renumeres un miembro existente.

### Negocio (`WorkUp.BusinessLogic/<Módulo>`)

- Un servicio por módulo o caso de uso grande (`BookingService`, `AvailabilityService`), con interfaz de repositorio al lado (`IAppointmentRepository`).
- Los métodos públicos reciben primero `businessId` y devuelven `Result` o `Result<T>`. Una regla incumplida es un resultado fallido con mensaje en español (`Result.Failure("slot_taken", "Ese horario se ocupó recién. Elegí otro.")`), no una excepción. Las excepciones quedan para errores de programación o de infraestructura.
- Toda escritura con más de un paso va en una transacción:

  ```csharp
  await using var tx = await _unitOfWork.BeginAsync();
  // lecturas con bloqueo, validaciones, escrituras
  await tx.CommitAsync();
  ```

  Si una transacción ya está abierta, `BeginAsync` se une a ella; solo la más externa confirma.
- Concurrencia (último lugar, mismo horario): dentro de la transacción, bloquear la fila que actúa de candado antes de contar. Turno individual: `SELECT Id FROM Workers WITH (UPDLOCK, ROWLOCK) WHERE Id = @workerId`. Clase: la fila de `Appointments` de esa clase. Después revalidar y escribir.
- La hora actual sale siempre de `IClock`, nunca de `DateTime.Now`.
- Los emails se encolan como `Notification` con una `DedupKey` única y estable (por ejemplo `reminder:booking:{id}`); nunca se envían dentro del caso de uso.

### Datos (`WorkUp.Data/<Módulo>`)

- Cada repositorio hereda de `SqlRepositoryBase`, implementa la interfaz del módulo y guarda su SQL en constantes.
- Siempre parámetros (`p.AddValue("@id", id)`), nunca concatenación. Toda consulta de una tabla con `BusinessId` filtra por él.
- Mapear con los helpers de `SqlExtensions` (`Get<T>`, `GetNullable<T>`, `GetEnum<T>`).
- Cambios de esquema: script nuevo `Sql/00N_descripcion.sql`. Nunca edites un script ya aplicado.
- Registrar repositorio y servicio en `WorkUp/Infrastructure/ServiceRegistration.cs`, como `Scoped`.

### Web (`WorkUp`)

- Controladores finos: reciben un view model, validan con DataAnnotations (mensajes en español), llaman al servicio y, si falla, `ModelState.AddBusinessError(result.Error!)` y devuelven la vista. Si sale bien: `this.Flash("Atención registrada.")` y redirección (POST → redirect → GET).
- Los controladores del panel heredan de `OwnerControllerBase` y usan su `BusinessId`, nunca un id de negocio que venga del request. Los del panel del cliente heredan de `ClientPanelControllerBase`.
- View models en `ViewModels/<Módulo>`. Las vistas nunca reciben entidades con datos sensibles del cliente en pantallas públicas.
- Antiforgery está activo para todos los POST; usar los tag helpers de formulario.
- `/t/{token}` y cualquier token en la URL: respuesta genérica ante token inválido, vencido o revocado, sin revelar si existe.

### Vistas y diseño

- Sistema visual: `DESIGN.md` (paleta verde) implementado en `wwwroot/css/workup.css`. Sin Bootstrap, sin estilos en línea, sin colores fuera de los tokens.
- Listas como filas (`.rows` / `.row`), no tarjetas. Un solo `.btn-primary` por pantalla.
- Estados con `<partial name="_StatusTag" model="StatusTag.For(booking.Status)" />`: siempre palabra más ícono.
- Íconos con `@Icons.Svg("calendar")`, siempre junto a un texto. Si falta uno, se agrega en `Infrastructure/Icons.cs`.
- Dinero, fechas y horas con `Formatting.Money`, `Formatting.LongDate`, `Formatting.Time`. El model binding es invariante: los campos numéricos usan `type="number" step="0.01"`.
- Formularios: `.field` con `<label>`, control y `<span asp-validation-for>`; `<partial name="_ValidationScriptsPartial" />` en la sección `Scripts`.
- Pantallas públicas: WCAG 2.1 AA, texto base 18 px, botones de 56 px, una decisión por paso.
- Privacidad: observaciones de salud y notas solo en el panel de la dueña. Nunca en pantallas públicas, enlaces, emails ni reportes.

### Pruebas

- TDD estricto para reglas de dominio y servicios de negocio: primero la prueba que falla, después el código.
- Nombres en inglés que describen el comportamiento: `Late_renewal_starts_on_the_payment_day`.
- Servicios probados con repositorios falsos en memoria (`WorkUp.Tests/Fakes`) y un `FakeClock`. Sin base de datos en las pruebas unitarias.
- Cada tarea anota en el plan el comando y el resultado observado.
