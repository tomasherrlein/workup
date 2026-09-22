# WorkUp MVP: plan de implementación

**Objetivo.** Construir una versión completa y usable de WorkUp que cubra todos los requerimientos de `docs/srs.md` v3.0 (RF-01 a RF-30) para probarla con el equipo y decidir qué conservar y qué cambiar.

**Problema.** Hay SRS, requerimientos funcionales, diseño de producto (`PRODUCT.md`) y sistema visual (`DESIGN.md`, paleta verde en la rama `design/paleta-verde`), pero no hay código. El proyecto `WorkUp/` es la plantilla MVC vacía de .NET.

**Por qué ahora.** Todas las decisiones de negocio D01 a D08 están aprobadas e incorporadas al SRS v3.0; una versión navegable es la forma más rápida de validarlas.

**Fuentes de verdad, en orden:** `docs/srs.md` v3.0 → `docs/requerimientos-funcionales.md` → `docs/propuestas-resolucion-requerimientos.md` → `PRODUCT.md` → `DESIGN.md` (paleta verde) → `docs/workup-menu-and-views-design.md` (solo como inventario de pantallas: está basado en el SRS v2.2 y sus reglas de tramos, capacidad simultánea, código por teléfono y avisos por WhatsApp quedaron superadas).

---

## 1. Alcance

**Incluye:** todo RF-01 a RF-30 del SRS v3.0, más la devolución manual (D-20, RF 28 del documento narrativo), con datos de demostración de los dos casos relevados (peluquería de Marina y estudio de yoga de Lucía) y herramientas de prueba para simular el paso del tiempo y ver los emails generados.

**No incluye:** lo excluido en el SRS §1.3 (facturación, pagos en línea, WhatsApp/SMS, puestos o salas, packs, recuperación de clases, consumo automático de insumos, trabajadoras como usuarias), ni publicación en internet (tarea opcional T28, a decidir).

---

## 2. Arquitectura

MVC en capas, con inversión de dependencias entre negocio y datos para poder probar las reglas sin base de datos.

```
WorkUp/WorkUp.sln
├── WorkUp.Domain     Entidades, enums y reglas puras (sin dependencias)
├── WorkUp.BusinessLogic  Servicios de negocio (casos de uso), interfaces de repositorio y puertos (IClock, IUnitOfWork, IPasswordHasher)
├── WorkUp.Data       ADO.NET: sesión de base de datos, repositorios, migrador y scripts SQL
├── WorkUp            Presentación MVC: controladores, view models, vistas, autenticación, tareas programadas
└── WorkUp.Tests      xUnit: dominio y negocio
```

**Dependencias:** `Web → BusinessLogic, Data, Domain` · `Data → BusinessLogic, Domain` · `BusinessLogic → Domain` · `Tests → BusinessLogic, Domain`. La web solo usa `Data` para registrar dependencias en `Program.cs`.

**Stack:** ASP.NET Core MVC sobre .NET 9 · SQL Server (LocalDB o Express) · ADO.NET con `Microsoft.Data.SqlClient`, sin Entity Framework · autenticación por cookies con dos esquemas (dueña y cliente) · hash de contraseñas con `PasswordHasher<T>` de `Microsoft.Extensions.Identity.Core` · tareas programadas con `BackgroundService` · xUnit.

Las convenciones de código (nombres, capas, transacciones, vistas, CSS) están en `WorkUp/README.md` y son obligatorias para cada tarea.

### Glosario del SRS en el código

| SRS | Código |
|---|---|
| Negocio · cuenta de la dueña | `Business` · `OwnerAccount` |
| Servicio / tipo de clase (cupo máximo 1 = turno individual) | `Service` |
| Trabajadora / profesora · horario · pausa | `Worker` · `WorkerSchedule` · `WorkerBreak` |
| Clase de la grilla | `GridClass` |
| Turno (turno individual, clase de la semana, atención sin turno) | `Appointment` (`Kind`: `Individual`/`ClassSession`; `Origin`: `Reserved`/`WalkIn`) |
| `Programado` / `Suspendido` | `AppointmentStatus.Scheduled` / `Suspended` |
| Anotación del cliente en un turno, `Fija` / `Suelta` | `Booking`, `BookingModality.Fixed` / `DropIn` |
| `Pendiente` · `Confirmado` · `Atendido` · `Ausente` · `Cancelado` | `BookingStatus.Pending` · `Confirmed` · `Attended` · `Absent` · `Cancelled` |
| `Cobrado` | Derivado: la anotación tiene un `Payment` no anulado |
| Cliente (ficha) · observación | `Client` · `ClientNote` |
| Cuenta de cliente · invitación de vinculación | `ClientAccount` · `AccountInvitation` |
| Enlace privado "Ver mi turno" | `AppointmentLink` |
| Inscripción · clase fija · período de abono | `Enrollment` · `EnrollmentFixedClass` · `MembershipPeriod` |
| `Vigente` / `Vencido` / `Pendiente de renovación` | Derivados de `MembershipPeriod` (`MembershipCalendar`) |
| Cobro · anulación · devolución manual | `Payment` · `PaymentAnnulment` · `Refund` |
| Gasto · tipo de gasto · insumo | `Expense` · `ExpenseType` · `Supply` |
| Aviso (email) | `Notification` (bandeja de salida con clave de deduplicación) |

---

## 3. Supuestos de implementación

Decisiones tomadas para poder construir donde el SRS no alcanza o se contradice. Se marcan como **a validar** para revisarlas con el equipo al probar.

1. **Multinegocio.** Cada negocio tiene sus datos aislados por `BusinessId`; la demo carga la peluquería y el estudio de yoga en la misma base.
2. **Cuentas de cliente por negocio.** Una persona clienta de dos negocios tiene dos cuentas. *(a validar)*
3. **Avisos a la dueña por email.** RF-18 y RF-19 dicen "teléfono de contacto", pero la integración con WhatsApp y SMS está excluida (SRS §1.3). Se envían al email de avisos del negocio; el teléfono queda como dato de contacto. *(a validar)*
4. **Precio del abono.** El SRS no dice dónde se configura. Se agrega al tarifario una lista de precios de abono por frecuencia semanal. *(a validar)*
5. **Referencias cruzadas del SRS.** RF-11 remite a RF-26 para anular un cobro (es RF-28); RF-16 remite a RF-26 y RF-27 (anulación es RF-28 y la devolución manual no tiene RF en el SRS); RF-25 remite a RF-28 para recuperar contraseña (es RF-27). Se implementa según D-20 y el RF 28 narrativo. Corregir el SRS en una tarea de documentación aparte.
6. **Grilla horaria de reserva.** Los horarios libres se ofrecen cada 15 minutos. *(a validar)*
7. **Indicadores sin fórmula (RF-16).** Cliente recurrente: 2 o más atenciones en el período. Dejó de venir: tuvo atención en los 90 días anteriores al período y ninguna dentro. Producción por trabajadora: cantidad de atenciones y total cobrado de sus turnos. La pantalla los muestra como "definición provisoria". *(a validar)*
8. **Enlace privado.** Token aleatorio de 32 bytes; se guarda su hash SHA-256 para validarlo y una copia protegida con Data Protection para que la dueña pueda volver a copiarlo (RF-23).
9. **Generación de la semana.** Manual, desde Clases, con vista previa e idempotente (RF-06).
10. **Emails.** Por defecto se guardan en la bandeja de salida (`Notifications`) sin enviarse; se ven en Herramientas de prueba. SMTP real es opcional por configuración.
11. **Herramientas de prueba.** Reloj simulado, ejecución manual de tareas programadas y visor de emails, activables con `Features:DevTools`. Existen para probar con el equipo; se apagan en producción.
12. **Zona horaria única:** Argentina (UTC-3). Las fechas se guardan en hora local del negocio.
13. **Atención sin turno en clases.** Se incorpora como `Suelta` respetando el cupo (RF-11 del SRS). En turnos individuales nunca se bloquea (D-25).

---

## 4. Modo de trabajo

- **TDD:** estricto (configuración de la sesión). Runner: `dotnet test WorkUp/WorkUp.sln`. Toda regla de dominio o de negocio se escribe con la prueba primero (RED → GREEN → REFACTOR) y la evidencia se anota en la tarea. Controladores y vistas: verificación funcional manual con la app corriendo.
- **Verificación de cada tarea:** `dotnet build WorkUp/WorkUp.sln` sin errores ni advertencias nuevas · `dotnet test WorkUp/WorkUp.sln` en verde · recorrido manual del flujo descrito en la tarea.
- **Ruta:** T00 la hace el orquestador (pedido explícito del usuario). T01 en adelante: un agente escritor por tarea, que lee este documento y `WorkUp/README.md` antes de editar.
- **Commits:** al menos un commit por tarea en `feat/workup-mvp`, con pruebas y documentación incluidas, mensaje Conventional Commit en español como el resto del repositorio. El hash se anota en la tarea.
- **Tamaño:** unas 400 líneas por tarea es una referencia de planificación, no un límite.
- **Entrega:** estrategia `ask-on-risk` con encadenamiento `feature-branch-chain` (decisión del usuario, 2026-09-21): cada porción revisable es un PR contra `feat/workup-mvp`; al final `feat/workup-mvp` entra a `main` en un solo merge. `main` conserva solo documentación aprobada mientras el MVP se prueba. Los límites de cada PR (qué commits contiene) se anotan en la sección 6.
  - Mecánica: `feat/workup-mvp` es la rama integradora (PR borrador a `main`, sin merge hasta cerrar la cadena) y ya contiene T00. Cada tarea trabaja en una rama hija `feat/workup-mvp-NN-<tema>` creada desde la rama de la tarea anterior; su PR apunta a esa rama madre (la primera, a `feat/workup-mvp`). Push y apertura de PRs quedan a decisión del usuario.
- **Base de datos:** SQL Server confirmado por el usuario (2026-09-21), frente a MySQL. Todo el equipo usa Windows y los hostings gratuitos de .NET (MonsterASP.NET, Somee) incluyen SQL Server en el mismo lugar; Azure SQL tiene un plan gratuito sin vencimiento. Opciones para T28.
- **Revisión RDD:** desactivada (`gentle-ai review mode status`: off por defecto). No se inicia.

---

## 5. Tareas

Cada tarea indica los RF que cubre, lo que debe quedar funcionando y las reglas que no se pueden romper. Las dependencias están entre corchetes.

### Fase 0: base (orquestador)

- [x] **T00 · Estructura base.** Solución en capas, entidades y enums del dominio, interfaces de repositorio y de servicios, sesión ADO.NET con transacciones, migrador y esquema SQL completo, shell MVC (layouts de dueña, cliente y público), autenticación por cookies, tokens de diseño verdes, `README.md` con convenciones, regla de vencimiento del abono con pruebas como ejemplo de TDD.
  - Aceptación: `dotnet build` y `dotnet test` en verde; la app arranca, crea la base y muestra la portada.
  - Ruta: inline por pedido explícito del usuario ("no se lo dejes a un agente").
  - Evidencia: TDD de `MembershipCalendar`: RED observado (7 de 7 fallan con `NotImplementedException`), GREEN 7 de 7. `dotnet build WorkUp/WorkUp.sln`: 0 errores, 0 advertencias. `dotnet test`: 7 superadas. Ejecución: `dotnet run --launch-profile http` aplicó `001_schema.sql` (28 tablas en LocalDB), `/` responde 200, `/no-existe` responde 404 con la página propia, `/css/workup.css` 200.
  - Commits: `a0f68eb` librerías de la plantilla (terceros, 23.064 líneas no autorales) · `1e0c8a3` dominio (995) · `c24cd74` datos (697) · `42b8688` web (1.840). Supera la referencia de 400 líneas por ser el andamiaje completo; se dividió por capa para que cada commit compile.

### Fase 1: configuración del negocio

- [x] **T01 · Alta del negocio y acceso de la dueña** (RF-01, RNF Seguridad) [T00]. Registro de negocio con usuario y contraseña, inicio y cierre de sesión (`Areas/Owner/Controllers/AccountController`: `Login`, `Register`, `Logout`), configuración del negocio (nombre, slug público, teléfono, email de avisos, atención sin turno, anticipación del recordatorio, hora del resumen, periodicidad de revisión de existencias), tipos de gasto. Una sola cuenta por negocio. Al iniciar sesión se emiten los claims de `WorkUpClaims` (negocio, nombre, slug) y se entra a `Day`: crear un `DayController` mínimo con la jornada vacía, que T12 completa. Crear aquí `WorkUp.Tests/Fakes` con `FakeClock` y los primeros repositorios falsos.
  - Ruta: agente escritor (disparador de escritura: más de 2 archivos no triviales). Rama `feat/workup-mvp-01-acceso-duena`.
  - Evidencia: TDD RED 14/14 (`SlugFormatTests`) y 29/29 (`WorkUp.Tests.Businesses`) con `NotImplementedException`, luego GREEN; suite completa 50/50. Build 0 errores, 0 advertencias. Ejecución contra LocalDB: registro 302 a `/panel`, Jornada 200, logout 302, login con contraseña errónea muestra "Usuario o contraseña incorrectos.", login correcto 302, configuración y tipos de gasto (alta, renombrar, archivar, reactivar) 302, slug duplicado rechazado. Verificación del orquestador: `dotnet test` 50/50 y recorrido de `/`, login, registro y redirección de `/panel/Settings` al login.
  - Commits: `a8f4d9c` reglas y pruebas (942) · `1c8620f` repositorios SQL (184) · `132494e` web (617) · refactor del orquestador que renombra la capa a `WorkUp.BusinessLogic` (el espacio `WorkUp.Business` ocultaba la entidad `Business`).
- [x] **T02 · Tarifario** (RF-02) [T01]. Alta, edición y archivo de servicios y tipos de clase (precio > 0, duración > 0, cupo máximo ≥ 1). Precios de abono por frecuencia semanal (supuesto 4). Archivar conserva el historial. La navegación oculta Clases y Abonos mientras el negocio no tenga servicios con cupo mayor a 1.
  - Ruta: agente escritor. Rama `feat/workup-mvp-02-tarifario`.
  - Evidencia: TDD RED 32/33 con `NotImplementedException` (filtro `Catalog`), GREEN 33/33; suite 83/83; build 0 errores, 0 advertencias. Ejecución: menú sin Clases ni Abonos hasta crear un servicio con cupo > 1 (la barra inferior completa con Clientes), precio 0 rechazado con mensaje, edición, archivo y reactivación 302, precio de abono por frecuencia alta y baja. Verificación del orquestador: `dotnet test` 83/83.
  - Commits: `3b6e1ce` reglas (653) · `f587a58` repositorios (148) · `1a84c79` web y navegación (435).
  - Nota: la consulta `HasBookingHistoryAsync` lee `Appointments` y `GridClasses` desde el repositorio de Catalog porque sus repositorios propios llegan en T04 y T08; si esas tareas agregan reglas de archivo, revisarla.
- [x] **T03 · Trabajadoras, horarios y pausas** (RF-03) [T01]. CUIL único por negocio; fin posterior al inicio; pausas dentro del horario del día.
  - Ruta: agente escritor. Rama `feat/workup-mvp-03-trabajadoras`.
  - Evidencia: TDD RED 28/28 (`CuilFormat`, `WeeklyScheduleRules`) y 24/24 (`StaffService`) con `NotImplementedException`, luego GREEN; suite 135/135; build 0 errores, 0 advertencias. Ejecución: alta lunes a viernes 9–20 con pausa 13–16, edición, CUIL duplicado, pausa fuera de horario y fin antes del inicio rechazados con mensaje, archivo y reactivación. Verificación del orquestador: build y `dotnet test` 135/135.
  - Commits: `9fd481c` reglas y servicio (1.050) · `6f85c50` repositorio (196) · `af96f53` web (381).
  - Notas: el formulario ofrece 2 pausas por día (funciona sin JavaScript; el dominio admite cualquier cantidad). CUIL validado solo por formato de 11 dígitos. Teléfono enmascarado con `Formatting.MaskPhone` (últimos 4 dígitos). `WeeklyScheduleRules.IsAvailable` queda listo para T04 y T08.
- [x] **T04 · Grilla semanal** (RF-04) [T02, T03]. Clases de la grilla por día, horario, tipo de clase (cupo > 1) y profesora; se valida que la clase entre en el horario de la profesora y no se superponga con otra clase suya.
  - Ruta: agente escritor. Rama `feat/workup-mvp-04-grilla`.
  - Evidencia: TDD RED 33/33 con `NotImplementedException`, GREEN 33/33; suite 168/168; build 0 errores, 0 advertencias. Ejecución: clase del martes 18:00 creada y visible, clase fuera del horario de la profesora y superposición con la misma profesora rechazadas con mensaje, superposición con otra profesora aceptada, edición, archivo y reactivación, acceso "Grilla semanal" en Configuración. Verificación del orquestador: build y `dotnet test` 168/168.
  - Commits: `9ee08a5` reglas y servicio (722) · `f068647` repositorio (103) · `57dc5d9` web (444).
  - Notas: mientras una clase de la grilla tenga clases fijas vigentes no se puede cambiar su día, hora ni tipo, ni archivarla (consulta directa a `EnrollmentFixedClasses` hasta T15). Hueco detectado: si después se archiva la profesora o el tipo de clase de una clase de la grilla, su edición falla hasta reasignarlos; resolver en T27 (bloquear o advertir el archivo de profesoras y tipos de clase con clases activas en la grilla).
- [x] **T05 · Fichas de clientes** (RF 1, RF-11 observaciones) [T01]. Listado con búsqueda, alta (email único por negocio, teléfono y nombre obligatorios), detalle con observaciones de salud y notas, historial de turnos y cobros. Las observaciones nunca se muestran fuera del panel de la dueña.
  - Ruta: agente escritor en `feat/workup-mvp-05-clientes`; corrección posterior por hallazgos de verificación independiente.
  - Evidencia: TDD RED 18/18 para formatos y luego 19/19 al sumar comportamiento de clientes; regresión de privacidad RED por ausencia de `ClientReference`. GREEN focalizado 30/30 y 12/12 tras corrección. Suite completa y spot-check del orquestador: 198/198. Build: 0 errores, 0 advertencias. Ejecución contra LocalDB: alta temporal de dueña, listado vacío, creación de ficha con observación de salud y detalle con historial de cobros vacío; `WorkUp.exe` finalizado. Verificación independiente corrigió aislamiento por `BusinessId` en todos los joins, pagos referenciados por anotación con `ClientId` nulo, DTO público sin datos privados y composición DI centralizada. `git diff --check` sin errores. Limitación: consultas SQL verificadas estructuralmente porque no existe fixture de integración SQL.
  - Commits: `d7c1ef3` reglas, servicio y pruebas · `8151782` repositorios SQL · `c92f63d` panel de fichas y DI.
- [x] **T06 · Gastos e insumos** (RF-13, RF-14) [T01]. Gastos por tipo y período. Insumos con existencia declarada y mínimo; alerta de reposición visible en la lista y en la navegación. La existencia solo cambia por declaración.
  - Ruta: agente escritor en `feat/workup-mvp-06-gastos-insumos` y verificación independiente; corrección de bloqueos sobre fecha de negocio y precisión SQL.
  - Evidencia: TDD RED 10/10 para reglas iniciales y 14/32 en regresiones de escala/rango; GREEN 16/16 y luego 32/32. `dotnet build WorkUp/WorkUp.sln`: 0 errores, 0 advertencias. Suite completa y spot-check del orquestador: 230/230. Runtime LocalDB: gasto decimal 1.500,50, declaración de insumo 0,50 bajo mínimo 1,00, alerta “Reponer” y badge 1 en Insumos; proceso terminado. Verificación independiente PASS: consultas aisladas por negocio, reloj `IClock`, límites DECIMAL(12,2) y DECIMAL(10,2) en negocio antes de persistir. `git diff --check` sin errores (advertencias LF→CRLF). Limitación: sin fixture SQL de integración; consultas revisadas estructuralmente. Los datos de prueba persisten en LocalDB local.
  - Commit: `a790182` (975 líneas autorales; gasto e insumo comparten indicador en navegación y composición de servicios, una unidad funcional coherente; supera la referencia de 400 líneas sin recortar controles o pruebas).
- [x] **T07 · Datos de demostración** [T02–T06]. Semilla idempotente de la peluquería (2 trabajadoras, servicios individuales) y el estudio de yoga (2 profesoras, tipos de clase, grilla), con usuarios de prueba documentados en `WorkUp/README.md`. Se amplía en T15 y T20 con abonos y cuentas.
  - Ruta: agente escritor en `feat/workup-mvp-07-demo`, exploración previa y verificación independiente; corregidos parseo del argumento CLI, comparación sensible a duplicados y destino LocalDB dedicado.
  - Evidencia: TDD RED 7 casos y luego 11 regresiones; GREEN 20/20 focalizadas. `dotnet build WorkUp/WorkUp.sln`: 0 errores, 0 advertencias. Suite completa y spot-check del orquestador: 250/250. CLI rechazó faltas de opt-in/contraseña y flags duplicados/malformados antes de conectar. Contra `WorkUpDemoT07Verifier2` nueva, primera ejecución creó 2 negocios, la segunda reconoció 2 existentes sin duplicar; conteo: 2 cuentas, 4 trabajadoras, 5 servicios y 4 clases. Requiere flag, Development, configuración habilitada, contraseña por entorno y base LocalDB dedicada; no modifica la base predeterminada. Verificación independiente PASS sobre reglas, SQL y documentación; SQL sin fixture de integración automatizada.
  - Incidente acotado: `WorkUpT07DemoIsolation` conserva un esquema scratch parcial vacío tras `sqlcmd` sin `-I`; fue diagnosticado como separado de la base predeterminada y no se eliminó por ser destructivo. `WorkUpT07DemoSeedRun` y `WorkUpDemoT07Verifier2` conservan datos sintéticos de las pruebas. No se usó ni alteró `WorkUp` predeterminada al sembrar. Para futuros scripts filtrados usar `sqlcmd -I`.
  - Commit: `b4307aa` (931 líneas autorales: plan, protección, persistencia SQL transaccional, pruebas y documentación forman una unidad indivisible; no se recortaron controles para cumplir la referencia de 400 líneas).

### Fase 2: turnos individuales

- [ ] **T08 · Cálculo de disponibilidad** (Regla 1, D-25, RF-07) [T03]. `AvailabilityCalculator` puro con TDD: horario del día, menos pausas, menos turnos y clases de la trabajadora no cancelados, con la duración completa del servicio, cada 15 minutos; búsqueda del horario libre más cercano de la misma trabajadora.
- [ ] **T09 · Reserva pública de turno individual** (RF-07, RF-23) [T08]. `/r/{slug}`: servicio → trabajadora → día y hora → email, nombre y apellido → confirmación. Identifica o crea la ficha por email sin contraseña. Precio acordado tomado del tarifario. Revalida dentro de la transacción con bloqueo por trabajadora (dos personas no obtienen el mismo horario). Crea el enlace privado y encola el email inmediato; si la reserva queda dentro de la anticipación del recordatorio, ese email incluye el contenido del recordatorio (D-22). Pantallas grandes y simples, WCAG AA.
- [ ] **T10 · Enlace privado "Ver mi turno"** (RF-24, RF-08, RF-22, RF-12) [T09]. `/t/{token}`: muestra solo fecha, hora, servicio y trabajadora. Antes del inicio: confirmar intención y cancelar. Desde el inicio y hasta 24 h después: "No voy a asistir" registra `Ausente` con origen cliente, sin pisar `Atendido`. Enlace vencido o revocado: sin acciones, mensaje genérico. Desde el panel, la dueña copia, revoca y reemplaza el enlace sin extender el vencimiento.
- [ ] **T11 · Agenda y detalle del turno** (RF-07 asistida, RF-09, RF-22) [T09]. Agenda por día y semana con filtros (trabajadora, estado, origen). Detalle del turno con acciones válidas según estado y hora. Reserva asistida por la dueña (mismo cálculo y bloqueo que la pública). Cancelación por la dueña. Reprogramación con la misma trabajadora: el turno original se conserva hasta confirmar el nuevo horario, se revalida y el cliente queda `Confirmado`; desde el inicio no se admite.
- [ ] **T12 · Jornada, asistencia y atención sin turno** (RF-15, RF-11, RF-12) [T11]. Jornada del día con secciones Ahora, Próximo, Más tarde y Finalizado, total cobrado y pendientes de cobro. "Finalizar atención": asistencia y cobro opcional en un solo guardado, independientes (D-20). Marcar ausente. Atención sin turno (si el negocio la admite): crea turno `Sin reserva` a la hora real, avisa superposiciones y nunca bloquea. Observación para la próxima atención.

### Fase 3: clases y abonos

- [ ] **T13 · Generación de la semana** (RF-06) [T04]. Vista previa (clases nuevas, existentes, alumnos fijos) y confirmación. Idempotente: no duplica clases ni anotaciones ni borra cancelaciones o suspensiones. Anota a los alumnos fijos con inscripción pendiente de pago o activa; quien tiene el abono vencido aparece como "Pendiente de renovación".
- [ ] **T14 · Clase suelta** (RF-07 cupo > 1, RF-22, RF-11) [T13]. Reserva pública y asistida en clases generadas y programadas con lugar; revalida el cupo con bloqueo (último lugar: una sola reserva). Email inmediato solo si faltan menos de 24 h (D-22). Cancelación antes del inicio por la dueña o desde el panel del cliente; otra clase requiere una inscripción nueva.
- [ ] **T15 · Inscripción y abono** (RF-05, RF-20, RF-29) [T13]. Inscripción pendiente que retiene lugares fijos (cupo permanente por clase de la grilla). Cobro que activa el abono y calcula el vencimiento con `MembershipCalendar`. Anulación manual de la pendiente. Renovación anticipada y tardía, baja con liberación de lugares. Listado de abonos con filtros Vigentes, Vencen pronto, Vencidos y Pendientes de pago. Al activar, anular o dar de baja, se sincronizan las anotaciones fijas de las clases ya generadas.
- [ ] **T16 · Lista de la clase y suspensión** (RF-10, RF-11, RF-12, RF-22) [T14, T15]. Detalle de la clase con fijas y sueltas, ocupación, asistencia por alumno, cobro solo en sueltas, ausencia. `Pendiente de renovación` no habilita asistencia. Cancelación de una fecha de clase con abono (libera solo esa fecha). Suspensión con motivo: no altera la grilla, encola un email por alumno anotado sin duplicar.
- [ ] **T17 · Cambio de horario fijo** (RF-21) [T15]. La dueña elige el nuevo horario de la grilla y la fecha de vigencia; solo con cupo; cierra la clase fija anterior el día previo y abre la nueva; no toca fechas anteriores, vencimiento, día de referencia ni precio.

### Fase 4: dinero y reportes

- [ ] **T18 · Cobros, anulaciones y devoluciones** (RF-11, RF-28, D-20) [T12, T15]. Registro de cobros con concepto y referencia, sin duplicar el cobro de una anotación ya cobrada. Historial de cobros filtrable. Anulación con motivo, fecha y responsable (nunca editar ni borrar). Devolución manual enlazada al cobro.
- [ ] **T19 · Reportes** (RF-16) [T18, T06]. Resultado del período = cobros no anulados − devoluciones del período − gastos. Comparación con otro período. Indicadores del supuesto 7. Período posterior a hoy: no se calcula.

### Fase 5: cuentas de cliente

- [ ] **T20 · Registro, verificación, ingreso y recuperación** (RF-25, RF 15, RF-27) [T09]. Registro autónomo con email verificado; si el email ya existe en una ficha o cuenta, no se duplica. Ingreso con mensaje no enumerante. Recuperación con enlace de 30 minutos y un solo uso.
- [ ] **T21 · Panel del cliente** (RF 16, RF-08, RF-09, RF-22) [T20, T11, T14]. Mis turnos y clases. Confirmar intención, cancelar turno individual y clase suelta antes del inicio, reprogramar turno individual con la misma trabajadora. Reservar desde el panel sin volver a cargar datos.
- [ ] **T22 · Invitación de vinculación** (RF-26) [T20, T05]. Desde la ficha, invitación de 48 h, un solo uso, revocable; emitir otra invalida la anterior. Aceptación creando cuenta o iniciando sesión. Si la cuenta ya está vinculada a otra ficha, no se cambia ni se revela nada: se deriva a la dueña.

### Fase 6: avisos programados

- [ ] **T23 · Motor de tareas programadas y reloj** [T09]. `BackgroundService` que cada minuto ejecuta los trabajos con el `IClock` (real o simulado) y despacha la bandeja de salida con reintentos sin duplicar (clave de deduplicación única).
- [ ] **T24 · Recordatorios** (RF-17) [T23, T14]. Turno individual: con el enlace vigente y pedido de confirmación solo si sigue `Pendiente`. Clase suelta: sin pedido de confirmación. Nunca para clases fijas ni cancelados; sin recordatorio si la reserva se hizo dentro de la anticipación.
- [ ] **T25 · Resumen diario, revisión de existencias y vencimiento de abonos** (RF-18, RF-19, RF-30) [T23, T15, T06]. Resumen del día siguiente a la hora configurada; aviso de revisión según periodicidad; email 3 días antes del vencimiento y al vencer si no renovó, una sola vez por período.
- [ ] **T26 · Herramientas de prueba** (supuestos 10 y 11) [T23]. Visor de emails generados, ejecución manual de cada tarea, reloj simulado con avance por horas o días y vuelta al tiempo real.

### Fase 7: cierre

- [ ] **T27 · Accesibilidad y pulido** (RNF Accesibilidad, Portabilidad) [todas]. Recorrido WCAG 2.1 AA de las pantallas públicas, teclado, zoom 200 %, estados vacíos, errores y confirmaciones; responsive de 360 px a escritorio. Convertir las violaciones de restricciones únicas por carreras simultáneas (por ejemplo, dos registros con el mismo slug o usuario) en un `Result` fallido con mensaje, en lugar de la página de error (hallazgo de T01).
- [ ] **T28 · Publicación para el equipo** (opcional) [T27]. Elegir hosting con SQL Server y publicar. Requiere decisión y credenciales del usuario.
- [ ] **T29 · Corrección de referencias del SRS** (supuesto 5). Tarea de documentación independiente.

---

## 6. Progreso

- 2026-09-21: rama `feat/workup-mvp` creada desde `docs/nuevo-srs-rf` (incluye SRS v3.0). `design/paleta-verde` mergeada en la rama: `DESIGN.md` ya describe la paleta verde que usan los tokens de `wwwroot/css`.
- Espejo en Engram: tópico `odd/workup-mvp/tasks` del proyecto `workup`, guardado con `engram save` por línea de comandos (el `mem_save` por MCP fallaba por sesiones ambiguas).

## 7. Próximo paso

T08 (cálculo de disponibilidad) en una rama nueva `feat/workup-mvp-08-disponibilidad` creada desde `feat/workup-mvp-07-demo`.
