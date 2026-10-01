# WorkUp: código

Aplicación web MVC en capas que implementa `docs/srs.md` v3.0. El plan de tareas está en `odd/tasks/workup-mvp.md`; este archivo define **cómo** se escribe el código. Toda tarea lo sigue.

## Cómo correrlo

Requisitos: SDK de .NET 9 o superior y SQL Server (LocalDB, que viene con Visual Studio, o SQL Server Express).

```bash
dotnet run --project WorkUp/WorkUp --launch-profile https
```

- La primera vez crea la base `WorkUp` y aplica los scripts de `WorkUp.Data/Sql`. Si tu SQL Server no es LocalDB, cambiá `ConnectionStrings:WorkUp` en `WorkUp/appsettings.json` (por ejemplo `Server=.\SQLEXPRESS;Database=WorkUp;Trusted_Connection=True;TrustServerCertificate=True`).
- En desarrollo los emails no se envían: quedan en la bandeja de salida. Para verlos y para simular el paso del tiempo están las herramientas de prueba de `/panel/DevTools`, que tienen su propia guardia (ver «Herramientas de prueba»).
- Configurá `WorkUp:PublicBaseUrl` con la URL absoluta y confiable que se incluirá en los enlaces privados. Fuera de `Development` debe ser HTTPS y no puede apuntar a loopback; por ejemplo, definí `WorkUp__PublicBaseUrl=https://reservas.example.com` en el entorno del despliegue. En `Development` se permite `localhost` por HTTP o HTTPS. La aplicación valida esto al iniciar y nunca deriva el dominio del encabezado `Host` de la solicitud.
- Pruebas: `dotnet test WorkUp/WorkUp.sln`.

#### Aislamiento del llavero de DataProtection para verificación local (T12)

Por defecto la aplicación usa `services.AddDataProtection()` sin cambios: el llavero sale del perfil compartido del usuario (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` en Windows) y ninguna configuración lo altera. Eso es lo correcto para correr la app, pero una verificación HTTP sintética necesita su propio llavero para no leer ni escribir las claves compartidas.

Solo para desarrollo local existe una opción explícita: la clave de configuración `WorkUp:Development:DataProtectionKeysPath`. Cuando está ausente, el comportamiento es exactamente el de siempre. Cuando está presente, el arranque valida la ruta y **falla si algo no se cumple**, sin leer, crear ni escribir nada:

- el entorno tiene que ser `Development`;
- la ruta tiene que ser absoluta y quedar dentro del directorio temporal del sistema operativo;
- el último segmento tiene que empezar con `WorkUpDemoT12Keys`, para que nunca apunte al llavero compartido ni a una ruta pública.

En Windows las claves se protegen en reposo con DPAPI al persistirlas. Fuera de Windows la opción se rechaza al iniciar en lugar de guardar claves sin cifrar. El directorio se conserva: **no borres claves anteriores**. Los dos procesos del recorrido HTTP comparten el llavero durante **una** corrida; para repetir la prueba opt-in se exige un directorio y una base **nuevos**. Usá siempre destinos exclusivos de la verificación sintética.

Ejemplo en PowerShell, desde la raíz del repositorio:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:WorkUp__Development__DataProtectionKeysPath = Join-Path ([IO.Path]::GetTempPath()) 'WorkUpDemoT12KeysLocal'
try {
    dotnet run --project WorkUp/WorkUp --launch-profile https
}
finally {
    Remove-Item Env:WorkUp__Development__DataProtectionKeysPath, Env:ASPNETCORE_ENVIRONMENT
}
```

El valor también se puede dejar en `WorkUp/appsettings.Development.json` para una máquina de desarrollo dedicada. No lo configures fuera de `Development`: el arranque lo rechaza.

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

### Datos variados para la demo local

Después de crear el esquema y sembrar los dos negocios de T07, podés cargar ejemplos operativos **de forma explícita** desde la raíz del repositorio:

```powershell
sqlcmd -I -b -f 65001 -S '(localdb)\MSSQLLocalDB' -E `
  -d WorkUpDemoLocal1222f629b401 `
  -i .\WorkUp\scripts\LocalDemoEnrichment.sql
```

Ese catálogo es el de la demo manual preparada en esta máquina; en otra máquina reemplazalo por tu catálogo demo dedicado, ya migrado y sembrado. El lote rechaza servidores que no sean LocalDB y catálogos fuera de `WorkUpDemo*`/`WorkUpT07Demo*`. **No apunta a la base compartida `WorkUp`.**

- Agrega clientes ficticios, turnos individuales pasados y futuros, reservas fijas y sueltas de yoga, cobros, gastos, insumos con existencias bajas, abonos y períodos.
- Solo inserta: no cambia contraseñas de dueña ni actualiza o borra filas previas. Ambos negocios se cargan en una transacción; ante una colisión incompatible, se rechaza todo el lote.
- Los emails reservados `.invalid` identifican la carga. Una repetición no genera nuevas fechas ni duplica registros; no es un generador de datos renovados para cada semana. Si faltan partes de una carga previa, se detiene en lugar de repararlas silenciosamente.
- Crea cuentas verificadas de clienta: `demo.client.m1@demo.invalid` y `demo.client.m4@demo.invalid` para Marina; `demo.client.y1@demo.invalid` y `demo.client.y3@demo.invalid` para Lucía. Cada una usa la contraseña de demo que tenía su dueña **al cargar el lote**, copiada solo como hash; nunca se incluye una contraseña en el script.
- Para probarlas: `/r/peluqueria-marina-demo/cuenta/ingresar` o `/r/estudio-yoga-lucia-demo/cuenta/ingresar`, y luego «Mis turnos». Las fichas están vinculadas por ID y no por coincidencia de email.
- No fabrica tokens, invitaciones, enlaces privados ni avisos. Los avisos que produzca la app quedan retenidos si se conserva `WorkUp:Email:Mode=Outbox`.

Es una carga de datos de prueba, **no** una migración ni un cambio al contrato de `--seed-demo-data`; nunca se ejecuta automáticamente al iniciar WorkUp.

### Herramientas de prueba (T26)

La sección **Herramientas de prueba** (`/panel/DevTools`) existe solo si se cumplen **las tres** condiciones al arrancar: entorno `Development`, `WorkUp:Features:DevTools=true` y modo de email que retiene los avisos (`WorkUp:Email:Mode=Outbox`). Si falta cualquiera, la sección responde 404 aunque se entre por URL directa: la guardia falla cerrada y no activa el visor, las tareas ni el reloj. Además exige dueña autenticada y solo muestra los avisos del negocio con el que se ingresó.

- **Visor de avisos generados.** Lista los emails del negocio sin el cuerpo, porque un cuerpo puede traer el enlace privado de un turno o un token de un solo uso; el contenido completo se abre de a un aviso. Las dos páginas van con `no-store` y `Referrer-Policy: no-referrer`.
- **Ejecutar tareas programadas.** Cada tarea registrada tiene su formulario POST con antiforgery y su clave estable; una ejecución manual no se solapa con otra ni con el ciclo automático. En modo `Outbox` la tarea `outbox` no envía nada: los avisos quedan retenidos y visibles.
- **Reloj simulado.** Avanza por horas o por días y vuelve al tiempo real. Es **único para toda la instalación**: moverlo afecta a **ambos negocios de la demo a la vez**, y cada avance pide una confirmación explícita. El adelanto vive solo en memoria, así que **reiniciar el proceso devuelve el reloj al tiempo real**; mientras está adelantado, las tareas programadas pueden generar avisos `Pendientes`.
- **Nunca SMTP real.** Con tiempo simulado no se envía a ningún proveedor: la guardia exige el modo `Outbox`, así que las herramientas solo están disponibles cuando los emails quedan retenidos. No configures `WorkUp:Email:Mode=Smtp` esperando usarlas.

La suite predeterminada incluye pruebas SMTP con servidor loopback local, pero **no ejecuta** las integraciones SQL, HTTP ni de host sin sus variables opt-in: esas pruebas quedan **N/A** aunque xUnit las cuente aprobadas. Para las corridas reales usá bases y llaveros nuevos y exclusivos; retenelos como evidencia, sin reutilizarlos ni tocar la base compartida o el llavero del perfil.

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
