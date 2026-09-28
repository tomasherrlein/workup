# WORKUP
## Especificaciones de Requerimientos de Software

**Versión:** 3.0 — **Fecha:** 21-09-2026
**Equipo:** Bapton Solutions
**Documentos fuente:** `Relevamiento WorkUp.md` · `relevamiento.md` v5.2 · `docs/propuestas-resolucion-requerimientos.md`

**Objeto de este documento.** Especifica **qué necesita** el usuario y qué debe hacer el sistema. El
funcionamiento actual se describe en `Relevamiento WorkUp.md` y se analiza en `relevamiento.md`; el modelado
(Lista de Eventos, DFD, DD, DER) irá en `analisis.md`. Todo requerimiento es trazable a un identificador `{R#}`.

> **Versión 3.0.** Incorpora las decisiones D01 a D08 aprobadas en `docs/propuestas-resolucion-requerimientos.md`:
> cuenta de cliente opcional con registro autónomo, invitación de vinculación de ficha y recuperación de
> contraseña; enlace privado "Ver mi turno" para turnos individuales, con cancelación directa antes del inicio y
> aviso confirmado de inasistencia desde el inicio; reprogramación individual autenticada que conserva el turno
> original hasta confirmar el nuevo; reglas específicas de cancelación para clases con abono y clases sueltas;
> cambio de horario fijo del abono desde una fecha de vigencia; cobro y asistencia independientes, con anulación
> de cobros y devoluciones manuales; inscripción pendiente con liberación manual de cupos, sin vencimiento
> automático al inicio de la primera clase; vencimiento y renovación mensual del abono con el estado "Pendiente
> de renovación" y liberación manual del lugar; consecuencias financieras de una cancelación gestionadas por la
> dueña fuera de WorkUp; y disponibilidad simple sin tramos de aplicación y espera, con registro de atenciones
> sin turno no bloqueante. Estas decisiones sustituyen el modelo de identificación exclusivamente por teléfono y
> sin cuenta de la decisión D-02 de la versión 2.2, superada por `Requerimientos Funcionales WorkUp.md` vigente y
> por la intención confirmada de autogestión del cliente, y sustituyen el modelo de tramos de servicio de la
> decisión D-08 de la versión 2.2. Este documento reemplaza a `srs.md` (raíz), versión 2.2, que se conserva sin
> modificar como antecedente histórico.
>
> **Cambios de la versión 2.2.** Se incorpora la reprogramación de turnos individuales (RF-09) y se renumeran los
> requerimientos siguientes.
>
> **Cambios de la versión 2.1.** El estudio usa un abono mensual con clases fijas y sin recuperación:
> las clases perdidas por falta o por suspensión no se recuperan ni se devuelve el dinero. Se retiran la
> anticipación mínima, el ajuste manual de cupos y la decisión D-14. Los packs de clases quedan como evolución
> futura en `evoluciones/pack-de-clases.md` y no forman parte de este documento.

---

# 1. INTRODUCCIÓN

## 1.1 Propósito

Definir los requerimientos funcionales y no funcionales de WorkUp, un sistema web de gestión para emprendimientos
de servicios prestados por turno.

## 1.2 Unidad de negocio

Un emprendimiento de servicios que se prestan **en un turno con un trabajador asignado**, con uno o más
trabajadores, donde un turno puede recibir uno o varios clientes según el cupo máximo del servicio, y donde el
cliente paga por atención, por clase suelta o mediante un abono mensual con clases fijas. El cliente se relaciona
con el negocio sin cuenta obligatoria, asistido por la dueña o mediante el enlace privado de su turno; puede
además optar por una cuenta propia para autogestionar sus reservas desde un panel.

## 1.3 Alcance

**Comprende:** alta del negocio con su cuenta única y sus parámetros; tarifario con precio, duración y cupo
máximo; trabajadores con horario y pausas; grilla semanal recurrente y generación de las clases de cada semana;
abonos mensuales con clases fijas, inscripción pendiente con liberación manual de cupos, vencimiento y renovación
mensual; cambio de horario fijo del abono; reserva autogestionada con elección de trabajador; enlace privado
"Ver mi turno" para turnos individuales, con confirmación de intención de asistir, cancelación directa antes del
inicio y aviso confirmado de inasistencia desde el inicio; cuenta de cliente opcional con registro autónomo,
invitación de vinculación de ficha y recuperación de contraseña; reprogramación autenticada de turnos
individuales; avisos de asistencia y de vencimiento de abono; suspensión de clases con aviso enviado por email;
registro de atención, cobro y ausencia; cobro y asistencia independientes, con anulación de cobros y
devoluciones manuales; atenciones sin turno; vista de la jornada; gastos por tipo; existencias de insumos con
alerta de mínimo; avisos automáticos; y reportes del negocio.

**No comprende:**

| Exclusión | Justificación |
|---|---|
| Comercios de venta de productos | Sin turno, trabajador asignado ni duración: no comparten ninguna regla relevada (D-04) |
| Trabajadores como usuarios del sistema | Solo las dueñas registran (`R14`); agregaría una regla de autorización a cada requerimiento (D-06) |
| Fusión automática de historiales de cliente | Una invitación de vinculación no fusiona fichas ni transfiere acceso automáticamente; un conflicto se deriva a revisión de la dueña (D-18) |
| Cálculo automático de consumo de insumos | `R9`: el consumo no se registra y varía entre atenciones (D-03) |
| Comprobantes y facturación fiscal | `R5`: la factura se hace solo si el cliente la pide, por fuera del sistema (D-07) |
| Cobro en línea, seña y pasarela de pagos | No planteado por las usuarias; las señas y los acuerdos económicos externos los gestiona la dueña fuera de WorkUp (D-24) |
| Cálculo automático de devoluciones, créditos o penalizaciones | Cancelar no genera consecuencias económicas automáticas; una devolución se registra manualmente y queda enlazada al cobro original (D-20, D-24) |
| Recuperación automática de clases del abono | Cancelar una clase con abono antes del inicio libera solo esa fecha, sin recuperación ni devolución automáticas (D-19) |
| Packs de clases y pase libre | Evolución futura, documentada aparte en `evoluciones/pack-de-clases.md` |
| Integración automática de mensajería (WhatsApp, SMS) | Los avisos se envían por email; la dueña puede compartir manualmente un enlace por el canal que elija, sin integración obligatoria (D-18, D-22) |
| Identificación y asignación de puestos o salas, y límite de capacidad simultánea del negocio | El sistema no modela cuántos puestos físicos tiene el negocio ni identifica cuál ocupa cada cliente; no valida un límite de clientes simultáneos (D-25) |
| Trabajos en múltiples sesiones | Requiere vincular turnos entre sí y administrar pagos parciales |

## 1.4 Glosario

Vocabulario cerrado. Ningún documento del proyecto puede usar otro término para estos conceptos.

| Término | Aplica a | Significado |
|---|---|---|
| `Pendiente` | Cliente en turno | Se anotó pero todavía no confirmó su intención de asistir |
| `Confirmado` | Cliente en turno individual | Avisó, antes del inicio, que tiene intención de asistir; no equivale a asistencia real |
| `Atendido` | Cliente en turno | Se presentó y recibió el servicio o asistió a la clase |
| `Cobrado` | Cliente en turno | Se registró el importe percibido por esa atención o clase suelta |
| `Cancelado` | Cliente en turno | Avisó, antes del inicio, que no asiste; su lugar se liberó |
| `Ausente` | Cliente en turno | No se presentó desde el inicio del turno o la clase, sea porque no avisó o porque lo confirmó él mismo desde el enlace privado una vez iniciado |
| `Programado` | Turno | Existe en la agenda. Admite anotaciones mientras no alcance su cupo máximo |
| `Suspendido` | Turno | Clase de una semana puntual que no se dicta; no admite anotaciones |
| `Reservado` | Origen del turno | Se creó porque un cliente lo pidió por anticipado |
| `Sin reserva` | Origen del turno | Se creó al atender a quien se presentó sin turno |
| `Fija` / `Suelta` | Modalidad de la anotación | Anotación automática de un alumno con abono en su clase fija · clase paga al llegar |
| Cupo máximo | Servicio | Cantidad máxima de clientes que admite un turno de ese servicio |
| Tipo de clase | Servicio | Servicio del estudio con precio, duración y cupo máximo, como hatha o vinyasa |
| Clase de la grilla | Grilla | Tipo de clase fijado en un día de la semana, un horario y una profesora, que se repite cada semana |
| Clase de la semana | Turno | Clase de la grilla concretada en una fecha, donde se anotan alumnos, se toma asistencia o se suspende |
| Abono | Alumno | Pago mensual que reserva al alumno un lugar en sus clases fijas todas las semanas |
| Clase fija | Abono | Clase de la grilla elegida por el alumno con abono, en la que queda anotado cada semana |
| `Vigente` / `Vencido` | Abono | Dentro o fuera del mes que cubre desde la fecha de alta o de renovación |
| `Inscripción pendiente` | Alumno | Inscripción a clases fijas registrada antes de pagar; ocupa los lugares elegidos hasta que la dueña registra el cobro o anula la inscripción |
| `Pendiente de renovación` | Alumno en clase fija | Conserva su lugar con el abono vencido; no puede registrar asistencia hasta renovar |
| Fecha de vigencia | Cambio de horario fijo | Fecha desde la que rige un cambio de horario fijo del abono, sin alterar las fechas anteriores |
| Cuenta | Cliente | Acceso opcional al panel de cliente, con email y contraseña, obtenido por registro autónomo o por invitación de vinculación a una ficha existente |
| Enlace privado | Turno individual | Enlace reutilizable enviado por email al reservar, vigente hasta 24 horas después del inicio del turno, que permite consultar el turno exacto, confirmar la intención de asistir, cancelarlo antes del inicio o confirmar la inasistencia desde el inicio, sin cuenta |
| Invitación de vinculación | Ficha de cliente | Enlace emitido por la dueña, válido por 48 horas, de un solo uso y revocable, que asocia una cuenta a una ficha existente sin fusionar historiales |
| Anulación de cobro | Cobro | Registro que deja sin efecto un cobro por error, con motivo, fecha y responsable, sin editar ni borrar el cobro original |
| Devolución manual | Cobro | Registro de un reintegro realizado fuera del sistema, con importe, fecha y motivo, enlazado al cobro original |

---

# 2. NECESIDADES MANIFESTADAS

Origen de los requerimientos. Son las necesidades expresadas por las usuarias y conservan la numeración `{R#}`.

**`{R15}` Reserva autogestionada.** Contestar mensajes para coordinar turnos les interrumpe el trabajo, y muchas
consultas llegan fuera del horario de atención. Quieren que sus clientes puedan ver la disponibilidad real y
anotarse por su cuenta. El cliente informa sus datos de contacto al reservar y la reserva queda pendiente hasta
que se confirma. Desde la misma reserva, el cliente también debería poder pasar su turno a otro horario sin
escribirles. Marina insiste en que la clienta pueda elegir con quién se atiende, porque "vienen por vos, no
por el local".

**`{R16}` Avisos automáticos.** Las ausencias les generan pérdidas y los recordatorios se mandan a mano. Necesitan
que el aviso al cliente salga solo **24 horas antes del turno** y recibir ellas un resumen de su agenda del día
siguiente **a las 21:00**. Ambos tiempos deben poder ajustarse.

**`{R17}` Control de existencias de insumos.** Necesitan saber cuánto queda de cada insumo antes de quedarse sin
stock y que el sistema avise cuando algo esté por debajo del mínimo que ellas definan. Marina pide que se le
recuerde revisar el stock **una vez por semana**, porque si no se lo piden no lo actualiza.

**`{R18}` Información del negocio.** Necesitan el resultado del período calculado solo, y poder responder qué
servicio deja más, qué clientes vuelven, cuáles dejaron de venir, cuántos turnos se cayeron por ausencia y cuánto
trabajó cada trabajadora.

**`{R19}` Uso desde el celular y clientas mayores.** Ambas trabajan de pie y usan exclusivamente el celular.
Alrededor de un tercio de la cartera de Marina son clientas mayores de 65 años; si la pantalla de reserva no es
simple y con letra grande, van a seguir escribiendo por WhatsApp.

**`{R22}` Vista de la jornada.** Marina pide ver el día completo de un vistazo: quién viene, quién fue atendida,
quién falta cobrar y cuánto lleva cobrado. Quiere poder anotar una atención sin turno sin salir de esa pantalla, y
que el sistema **no le impida** registrar algo porque "no entra" en el horario: "si yo lo hice, lo hice; el
sistema me tiene que dejar anotarlo, no discutirme".

**`{R23}` Reserva sin bloquear el tiempo de espera.** Si el sistema le bloquea la coloración completa, pierde los
turnos que hoy intercala. Necesita que los horarios ofrecidos tengan en cuenta que durante la espera está libre.

**`{R35}` Aviso de suspensión preparado.** Lucía quisiera que, al suspender una clase, se le indique qué alumnos
estaban anotados y que el mensaje de aviso le quede escrito y listo para enviarlo desde su WhatsApp, sin buscar
cada teléfono ni redactar cada mensaje.

> **Nota de la versión 3.0.** `R23` motivó el modelo de tramos de aplicación y espera de la versión 2.2 (D-08).
> Ese modelo quedó superado por D-25: el servicio ocupa su duración completa sin dividirse en tramos, y la dueña
> puede registrar una atención sin turno aunque se superponga con otra. `R35` motivó que el aviso de suspensión
> quedara preparado para enviarse a mano; D-22 lo actualiza a un envío automático inmediato por email. Ambas
> necesidades se conservan como evidencia histórica; su resolución vigente está en la Sección 3.

---

# 3. DECISIONES ADOPTADAS

Resolución de las ambigüedades de `relevamiento.md`, Sección 4, y decisiones que condicionan los requerimientos.
Las decisiones D-18 a D-25 incorporan D01 a D08 de `docs/propuestas-resolucion-requerimientos.md` y actualizan o
sustituyen a las decisiones de la versión 2.2 que quedaron superadas.

| ID | Decisión | Fundamento |
|---|---|---|
| **D-01** | El cliente elige con qué trabajador se atiende antes de ver horarios; en una clase, la profesora queda determinada por la clase elegida | `R2` ofrece "el horario libre más cercano que tenga esa misma persona"; `R15` lo confirma; `R12` asigna profesora a cada clase |
| ~~**D-02**~~ | ~~La reserva no requiere cuenta: el cliente se identifica por su teléfono~~ — **superada por D-18** | Superada por `Requerimientos Funcionales WorkUp.md` vigente (email, contraseña y cuenta opcional) y por D01 |
| **D-03** | El sistema registra existencias declaradas, no calcula consumo | `R9`: el consumo no se registra y varía; `R26`: los elementos se reponen al verlos gastados |
| **D-04** | Alcance multi-rubro acotado a servicios prestados por turno con trabajador asignado | La variabilidad (`relevamiento.md` §5.1) muestra que cambian los valores, no las reglas |
| **D-05** | La grilla es recurrente; el sistema genera las clases de cada semana | `R12`: la grilla se repite todas las semanas; `R34`: las suspensiones son por semana puntual |
| **D-06** | Cuenta única por negocio; trabajadoras y profesoras son datos, no usuarios | `R5` registra "trabajador que atendió" como dato; `R14`: la profesora entrega lista y cobro y registra la dueña |
| **D-07** | El sistema no emite comprobantes fiscales | `R5`: no se entrega comprobante salvo que el cliente pida factura |
| ~~**D-08**~~ | ~~El servicio se define en tramos; la disponibilidad bloquea solo los tramos activos~~ — **superada por D-25** | Superada por `docs/propuestas-resolucion-requerimientos.md`, D08: el servicio ocupa su duración completa, sin tramos de aplicación y espera |
| **D-09** | La atención sin turno **es** un turno, creado al atender, con origen `Sin reserva` | `R21`: todo lo posterior es idéntico y hoy quedan cobros sin turno |
| **D-10** | El sistema valida reservas; no impide registros | `R22`: "si yo lo hice, lo hice" |
| ~~**D-11**~~ | ~~Capacidad simultánea del negocio: un número, sin identificar puestos~~ — **superada por D-25** | Superada por `docs/propuestas-resolucion-requerimientos.md`, D08: no se modela un límite de clientes simultáneos ni una capa de capacidad física; `R24` queda sin validar en el sistema |
| **D-13** | El abono es mensual y reserva un lugar en las clases fijas; sus clases no se recuperan | `R30`, `R31`, `R32` |
| **D-15** | Suspender una clase no la recupera ni devuelve dinero automáticamente: el alumno con abono la pierde como una falta y la clase suelta no se cobra | `R34`, `R13` |
| **D-16** | Una clase fija es una anotación automática que se hace al generar la semana; sin abono vigente o pendiente de renovación, no habilita asistencia | `R31`, `R30` |
| **D-17** | Reprogramar es mover un turno individual a otro horario libre de la misma trabajadora, a pedido del cliente o de la dueña, antes del inicio; las clases grupales no se reprograman | `R36`, `R15`, `R32`, ampliada por D-19 |
| **D-18** | Acceso a un turno individual mediante enlace privado, reutilizable y acotado, sin cuenta obligatoria. Cuenta de cliente opcional: registro autónomo, invitación de vinculación de ficha (48 h, un solo uso, revocable) y recuperación de contraseña (30 min, un solo uso). El cliente se identifica por su email; el teléfono es un dato de contacto adicional. Sin fusión automática de fichas | D01 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-19** | Cancelación y reprogramación revisadas: turno individual cancelable directamente por enlace antes del inicio, con aviso confirmado de inasistencia desde el inicio; reprogramación autenticada que conserva el turno original hasta confirmar el nuevo; clase con abono cancelable por fecha sin afectar los horarios fijos; clase suelta cancelable antes del inicio, sin traslado automático a otra clase; cambio de horario fijo del abono desde una fecha de vigencia y con cupo disponible | D02 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-20** | Cobro y asistencia son datos independientes; cada cobro identifica su concepto y su referencia concreta; se respeta el precio acordado; los descuentos se registran como precio acordado con motivo opcional; un cobro no se edita ni se borra, se anula con motivo; las devoluciones se registran manualmente, enlazadas al cobro original | D03 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-21** | La inscripción pendiente de pago ocupa los lugares fijos elegidos; el cobro la activa; si no se paga o no se presenta, la dueña la anula y libera los lugares. Sin vencimiento automático al inicio de la primera clase | D04 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-22** | Matriz de notificaciones aprobada: aviso inmediato y recordatorio con deduplicación para turnos individuales; recordatorio sin confirmación para clases sueltas; sin recordatorio para clases con abono; aviso inmediato de suspensión; avisos de vencimiento de abono (3 días antes y al vencer) | D05 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-23** | El abono vence un mes después de su alta o de su renovación tardía, en el mismo número de día o el último día del mes si no existe; la fecha de vencimiento es el primer día sin cobertura; el lugar se conserva pendiente de resolución manual y las listas muestran "Pendiente de renovación" | D06 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-24** | Cancelar no genera devoluciones, créditos ni penalizaciones automáticos ni borra los cobros; las señas y los acuerdos económicos externos los gestiona la dueña fuera de WorkUp | D07 (`docs/propuestas-resolucion-requerimientos.md`) |
| **D-25** | Disponibilidad simple: cada trabajadora tiene un turno a la vez, sin dividir el servicio en tramos de aplicación y espera; cupo máximo por clase con protección del último lugar; la dueña puede registrar una atención sin turno aunque se superponga, sin que el registro se bloquee. No se modela un límite de capacidad simultánea ni de puestos físicos del negocio: los cuatro puestos relevados en `R24` no se validan en el sistema; se mantiene el alcance simple | D08 (`docs/propuestas-resolucion-requerimientos.md`) |

**Decisiones retiradas.** **D-12**: fechar la atención independientemente del momento de carga no tiene respaldo
en el relevamiento unificado. **D-14**: la anticipación mínima y el ajuste manual de cupos pertenecían a los packs
de clases, que pasaron a evolución futura.

**Decisiones superadas.** **D-02** y **D-08** de la versión 2.2 quedan superadas por **D-18** y **D-25**
respectivamente, incorporadas en esta versión a partir de `docs/propuestas-resolucion-requerimientos.md`.
**D-11** también queda superada por **D-25**: la decisión del usuario de esta versión retira por completo el
límite de capacidad simultánea del negocio; las reservas solo validan disponibilidad de la trabajadora y cupo
máximo de la clase. Se conservan tachadas en la tabla como antecedente; ningún requerimiento vigente las aplica.

---

# 4. REGLAS DE NEGOCIO

1. **Disponibilidad por trabajador.** El horario libre se calcula sobre el horario de atención del trabajador,
   descontando sus pausas no laborables, sus turnos ya tomados y la duración completa del servicio, sin dividirlo
   en tramos. Dos turnos pueden coincidir si corresponden a trabajadores distintos. *(`R2`, `R28`, D-25)*
2. **Cupo máximo por servicio.** No se puede anotar en un turno más clientes que su cupo máximo. Si dos personas
   intentan reservar la última vacante, solo una obtiene la reserva. *(`R11`, `R13`, D-25)*
3. **El estado corresponde al cliente, no al turno.** En un turno de cupo máximo mayor a 1, cada cliente tiene su
   propia confirmación, asistencia o ausencia. El importe se registra por atención, por clase suelta o por abono.
   *(`R5`, `R14`, `R30`)*
4. **Momento de creación del turno.** Cupo máximo 1: se crea al pedir el turno. Cupo máximo mayor a 1: se genera
   desde la grilla y los clientes se anotan sobre uno existente. Sin reserva: se crea al atender.
   *(`R2`, `R12`, `R21`)*
5. **Liberación de lugar.** Al cancelar antes del inicio, el lugar vuelve a estar disponible dentro del mismo
   turno. *(`R4`, `R32`, D-19)*
6. **Un servicio pertenece a un negocio.** Precio, duración y cupo máximo se definen a nivel del negocio.
   *(`R1`, `R11`)*
7. **El cliente se identifica por su email.** El teléfono es un dato de contacto adicional. La cuenta con
   contraseña es opcional. *(`R3`, `R29`, D-18)*
8. **La existencia de insumos es un valor declarado.** El sistema nunca la modifica por sí mismo.
   *(`R9`, `R26`, D-03)*
9. **Suspender una clase no altera la grilla.** Afecta solo a esa semana. *(`R12`, `R34`, D-05)*
10. **Retirada.** El tramo de espera dejó de aplicar al eliminarse los tramos de aplicación y espera del
    servicio. *(D-25)*
11. **La validación de superposición y de cupo máximo solo se aplica al reservar.** Al registrar una atención sin
    turno, el sistema informa el conflicto pero no lo impide: el registro nunca se bloquea. *(`R22`, D-10, D-25)*
12. **Toda atención genera un turno.** No existe cobro de atención sin un turno que lo explique.
    *(`R21`, D-09)*
13. **Retirada.** El límite de capacidad simultánea del negocio dejó de aplicar al eliminarse por completo ese
    concepto: las reservas solo validan disponibilidad de la trabajadora (Regla 1) y cupo máximo de la clase
    (Regla 2); el registro de una atención sin turno nunca se bloquea (Regla 11). *(D-25)*
14. **Retirada.** La falta de lugar por capacidad simultánea que motivaba "ofrecer volver más tarde" dejó de
    aplicar junto con la Regla 13: el registro de una atención sin turno nunca se bloquea (Regla 11). *(D-25)*
15. **La atención sin turno se habilita por negocio.** Un negocio que publica su oferta en una grilla puede no
    admitirla. *(`R13`, `R21`)*
16. **Abono mensual.** El abono cubre un mes desde la fecha de alta o de renovación y reserva el lugar del alumno
    en sus clases fijas todas las semanas. *(`R30`, `R31`, D-13, D-23)*
17. **Las clases del abono no se recuperan.** Si el alumno falta sin haber cancelado antes del inicio, o si la
    clase se suspende, pierde esa clase y no se le devuelve dinero automáticamente. *(`R32`, `R34`, D-13, D-15,
    D-24)*
18. **Cancelar antes del inicio libera el lugar; faltar sin avisar se registra como ausencia.** En un turno
    individual, el cliente también puede confirmar "No voy a asistir" desde el enlace privado una vez iniciado el
    turno, lo que registra Ausente con origen en el cliente. *(`R32`, D-18, D-19)*
19. **Vencimiento del abono.** Si el alumno no renueva al vencimiento, su lugar se conserva pendiente de
    resolución manual, sin habilitar asistencia, hasta que la dueña lo renueve o dé de baja. *(`R31`, D-23)*
20. **Solo se reprograman turnos individuales, antes de su inicio.** Un turno de cupo máximo 1 puede pasarse a
    otro horario libre de la misma trabajadora, conservando la reserva original hasta confirmar el nuevo horario;
    las clases grupales no se reprograman. *(`R36`, `R32`, D-17, D-19)*
21. **Cobro y asistencia son independientes.** Cada cobro identifica su concepto y su referencia concreta (turno,
    atención sin turno, clase suelta o abono); ninguno modifica automáticamente al otro. *(D-20)*
22. **Un cobro no se edita ni se borra.** Se corrige mediante una anulación con motivo, fecha y responsable,
    enlazada al cobro original; una devolución se registra manualmente enlazada a ese cobro. *(D-20)*
23. **La inscripción pendiente retiene sus lugares.** Los ocupa hasta que la dueña registre el cobro o anule la
    inscripción; el inicio de la primera clase no libera el lugar por sí solo. *(D-21)*
24. **Cancelar no genera consecuencias económicas automáticas.** No genera devoluciones, créditos ni
    penalizaciones automáticos ni borra los cobros registrados; los acuerdos económicos externos los gestiona la
    dueña fuera de WorkUp. *(D-24)*

---

# 5. REQUERIMIENTOS FUNCIONALES

**Prioridad:** E = Esencial · C = Condicional · O = Opcional · D = Deseable

Los requerimientos se agrupan por su momento en el ciclo de negocio: **configuración** (RF-01 a RF-04),
**operación** (RF-05 a RF-14), **consulta** (RF-15 y RF-16), **avisos programados** (RF-17 a RF-19) y **cuenta,
enlace privado y ciclo de vida del abono** (RF-20 a RF-30, incorporados en la versión 3.0). La numeración no
indica orden de ejecución.

---

> ### RF-01: DAR DE ALTA Y CONFIGURAR EL NEGOCIO — `E`
>
> **Descripción:** Registrar o modificar el negocio, su única cuenta de acceso, el email por el que recibe sus
> avisos y los parámetros de operación que consume el resto de los requerimientos.
>
> **Inputs:** Nombre del negocio · Usuario · Contraseña · Teléfono de contacto del negocio · Email de avisos del
> negocio (opcional) · Tipos de gasto que usa el negocio · Admite atención sin turno, sí o no (sí por defecto) ·
> Anticipación del recordatorio de turno (24 horas por defecto) · Hora de emisión del resumen de agenda (21:00 por
> defecto) · Periodicidad del aviso de revisión de existencias (semanal por defecto).
>
> **Proceso:**
> 1. Recibir los datos del negocio.
> 2. Registrar la cuenta única de acceso al panel de gestión. *(Restricción 5)*
> 3. Registrar el email de avisos al que se dirigen el resumen de agenda y los avisos de existencias.
> 4. Registrar los parámetros asociados al negocio.
>
> **Outputs:** Negocio registrado con su cuenta de acceso, su teléfono de contacto, su email de avisos y sus
> parámetros.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R7`, `R12`, `R16`, `R17`, `R21`, `R27`, D-06, RNF Seguridad, RNF Mantenibilidad.
>
> **Nota:** la cuenta de acceso no proviene de un hecho relevado —los casos operan en papel— sino de D-06 y del
> RNF de Seguridad. Sin este requerimiento la Restricción 5 no tendría quién la produzca y RF-18 y RF-19 no
> tendrían a qué email enviar sus avisos.

---

> ### RF-02: DEFINIR SERVICIO DEL TARIFARIO — `E`
>
> **Descripción:** Registrar o modificar un servicio o tipo de clase, con su precio, su duración y su cupo
> máximo. El servicio ocupa su duración completa al reservarse, sin dividirse en tramos. *(D-25)*
>
> **Inputs:** Nombre · Precio · Duración · Cupo máximo.
>
> **Proceso:**
> 1. Recibir los datos del servicio.
> 2. Registrar el servicio asociado al negocio, con su duración total.
>
> **Outputs:** Servicio registrado en el tarifario.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R1`, `R11`, D-25.

---

> ### RF-03: DEFINIR TRABAJADOR Y SU HORARIO DE ATENCIÓN — `E`
>
> **Descripción:** Registrar o modificar una trabajadora o profesora con sus datos, los días y horas en que
> atiende y sus pausas no laborables.
>
> **Inputs:** Nombre · Apellido · Teléfono · CUIL · Días de trabajo · Hora de inicio y de fin por día · Pausas no
> laborables (hora de inicio y de fin).
>
> **Proceso:**
> 1. Recibir los datos de la trabajadora.
> 2. Registrar su horario de atención por día.
> 3. Registrar sus pausas no laborables.
>
> **Outputs:** Trabajadora registrada, con su horario disponible para calcular disponibilidad y armar la grilla.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R25`, `R28`.

---

> ### RF-04: DEFINIR CLASE EN LA GRILLA SEMANAL — `C`
>
> **Descripción:** Registrar o modificar en la grilla recurrente qué tipo de clase se dicta, qué día de la semana,
> a qué horario y con qué profesora. Aplica a servicios de cupo máximo mayor a 1.
>
> **Inputs:** Tipo de clase · Día de la semana · Horario · Profesora.
>
> **Proceso:**
> 1. Recibir los datos de la clase.
> 2. Registrar la clase en la grilla del negocio.
>
> **Outputs:** Clase incorporada a la grilla semanal publicada.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R12`, D-05. Un cambio de horario fijo de un alumno ya inscripto se registra mediante RF-21,
> sujeto a que el nuevo horario exista en esta grilla con cupo disponible.

---

> ### RF-05: REGISTRAR INSCRIPCIÓN Y ABONO — `C`
>
> **Descripción:** Registrar la inscripción pendiente de pago de un alumno a sus clases fijas y, al registrarse
> el cobro, activar el abono mensual. La inscripción pendiente y el cobro son dos momentos: los lugares elegidos
> quedan retenidos desde la inscripción, y el abono se activa recién con el cobro. *(D-21)*
>
> **Inputs:** Email del alumno · Nombre, apellido y observaciones de salud (si es su primera inscripción) ·
> Frecuencia semanal · Clases fijas elegidas · Fecha de pago (al activar) · Monto acordado y motivo si difiere
> del tarifario (opcional).
>
> **Proceso:**
> 1. Identificar al alumno por su email; si no existe, registrarlo con sus datos y observaciones de salud, sin
>    exigir contraseña. *(Regla 7)*
> 2. Registrar las clases fijas elegidas entre las clases de la grilla que tienen lugar. *(D-16)*
> 3. Registrar la inscripción en estado pendiente de pago; los lugares elegidos quedan ocupados desde este
>    momento y no se ofrecen a otro alumno, sin vencer por el solo inicio de la primera clase. *(Regla 23, D-21)*
> 4. Al registrar el cobro del abono, calcular la fecha de vencimiento a un mes de la fecha de alta. *(Regla 16,
>    D-23, RF-29)*
> 5. Registrar el cobro del abono, con el monto acordado y el motivo si difiere del tarifario. *(D-20)*
> 6. Registrar el abono en estado `Vigente`, con alumno, frecuencia, clases fijas, fecha de alta, monto y fecha
>    de vencimiento; incorporar al alumno a las listas fijas.
>
> **Outputs:** Inscripción pendiente registrada con sus lugares retenidos y, al registrarse el cobro, abono
> registrado en la planilla de abonos, alumno incorporado a las listas fijas y cobro del abono registrado.
>
> **Error Handling:** Si el alumno no paga o no se presenta a su primera clase, la inscripción permanece
> pendiente y sus lugares retenidos hasta que la dueña la anule (RF-20); el sistema no la vence ni libera los
> lugares automáticamente. *(D-21)*
>
> **Trazabilidad:** `R29`, `R30`, `R31`, D-16, D-18, D-20, D-21, D-23.

---

> ### RF-06: GENERAR LAS CLASES DE LA SEMANA — `C`
>
> **Descripción:** Crear las clases de la semana a partir de la grilla y anotar en ellas a los alumnos con clases
> fijas, según el estado de su abono.
>
> **Inputs:** Grilla semanal del negocio · Fecha de la semana a generar · Clases fijas de los alumnos · Abonos.
>
> **Proceso:**
> 1. Recorrer las clases de la grilla.
> 2. Crear un turno en estado `Programado` por cada una, con su fecha, su profesora y su cupo máximo.
> 3. Anotar en cada turno, con modalidad `Fija`, a los alumnos que tienen esa clase fija: habilitados para
>    registrar asistencia si el abono está `Vigente`, o como `Pendiente de renovación` si está `Vencido` y el
>    lugar todavía no fue liberado por la dueña. *(Regla 16, D-23)*
> 4. Regenerar una semana ya generada no duplica anotaciones ni borra el historial, las cancelaciones o las
>    suspensiones existentes.
>
> **Outputs:** Lista de cada clase de la semana, con fecha y alumnos fijos anotados, incluidos los marcados
> `Pendiente de renovación`.
>
> **Error Handling:** Un alumno `Pendiente de renovación` conserva su lugar pero no habilita registrar asistencia
> hasta renovar (RF-29). Si la dueña dio de baja la inscripción, no se lo anota y su lugar queda disponible.
> *(`R31`, D-23)*
>
> **Trazabilidad:** `R12`, `R30`, `R31`, D-05, D-16, D-23.
>
> **Nota:** la generación de clases concretas no es un proceso relevado —hoy la grilla y las listas se llevan en
> papel— sino una consecuencia de D-05: las anotaciones, avisos, suspensiones y asistencias necesitan una fecha
> concreta a la que asociarse.

---

> ### RF-07: RESERVAR TURNO — `E`
>
> **Descripción:** Permitir que el cliente elija un servicio o una clase suelta, consulte la disponibilidad real y
> se anote, informando sus datos de contacto. Si el servicio es de cupo máximo 1, se envía al cliente el enlace
> privado de su turno.
>
> **Inputs:** Servicio o tipo de clase · Trabajadora (si el servicio es de cupo máximo 1) · Fecha y hora elegidas
> · Email · Nombre · Apellido.
>
> **Proceso:**
> 1. Recibir el servicio y, si corresponde, la trabajadora elegida.
> 2. Determinar la oferta según el cupo máximo del servicio. *(Regla 4)*
>    - **Cupo máximo 1** — calcular los horarios libres de la trabajadora: horario de atención, menos pausas no
>      laborables, menos sus turnos ya tomados, sin contar los clientes `Cancelado`, verificando que entre la
>      duración total del servicio, sin dividirlo en tramos. *(Reglas 1 y 5, D-25)*
>    - **Cupo máximo mayor a 1** — recuperar las clases de la semana en estado `Programado` de ese tipo cuya
>      cantidad de anotados, contando los fijos y sin contar los `Cancelado`, sea menor que el cupo máximo.
>      *(Reglas 2 y 5, D-25)*
> 3. Mostrar los horarios disponibles.
> 4. Recibir el horario elegido y los datos de contacto.
> 5. Identificar al cliente por su email; si no existe, registrarlo sin exigir contraseña. *(Regla 7)*
> 6. Si el servicio es de cupo máximo 1, crear el turno en estado `Programado` con origen `Reservado` y enviar
>    inmediatamente el enlace privado "Ver mi turno" (RF-23). Si es de cupo máximo mayor a 1, incorporar al
>    cliente a la clase elegida con modalidad `Suelta`. *(Regla 4, D-18)*
> 7. Dejar al cliente en estado `Pendiente`.
>
> **Outputs:** Anotación registrada en estado `Pendiente` · Confirmación de día, hora y trabajadora al cliente ·
> Email con el enlace privado, si corresponde a un turno individual.
>
> **Error Handling:**
> - Si la trabajadora elegida no tiene disponibilidad en el día pedido, se ofrece su horario libre más cercano,
>   **de esa misma persona**. *(`R2`, D-01)*
> - Si la clase está completa, se ofrece otro día u horario. *(`R13`, Regla 2)*
>
> **Trazabilidad:** `R2`, `R3`, `R12`, `R13`, `R15`, `R23`, `R29`, `R32`, D-01, D-18, D-25, RF-01.

---

> ### RF-08: CONFIRMAR INTENCIÓN DE ASISTENCIA (TURNO INDIVIDUAL) — `E`
>
> **Descripción:** Registrar la confirmación de intención de asistir de un cliente a su turno individual, antes
> del inicio, sin contraseña ni plazo mínimo. Aplica únicamente a turnos individuales; no se aplica a clases
> recurrentes de abono ni a clases sueltas. *(D-19)*
>
> **Inputs:** Cliente · Turno individual · Confirmación del cliente.
>
> **Proceso:**
> 1. Recibir la confirmación del cliente, sea en respuesta al recordatorio (RF-17) o por iniciativa propia desde
>    el enlace privado (RF-24) o su panel, antes del inicio del turno.
> 2. Si el cliente ya había confirmado, una nueva confirmación no genera un cambio adicional.
> 3. Cambiar su estado a `Confirmado`.
>
> **Outputs:** Cliente en estado `Confirmado`.
>
> **Error Handling:** La confirmación no registra asistencia real ni modifica un estado ya marcado `Atendido` o
> `Cobrado`. La falta de respuesta conserva la reserva en `Pendiente`, sin cancelarla automáticamente. Si el
> cliente prefiere no asistir, cancela el turno mediante RF-22 en lugar de confirmar. Si en lugar de cancelarlo
> quiere pasarlo a otro horario, se reprograma (RF-09).
>
> **Trazabilidad:** `R4`, `R15`, `R32`, D-18, D-19.

---

> ### RF-09: REPROGRAMAR TURNO — `E`
>
> **Descripción:** Pasar un turno individual a otro horario libre de la misma trabajadora, antes del inicio del
> turno original, a pedido del cliente autenticado desde su panel o de la dueña. El turno original permanece
> reservado mientras se consultan alternativas y solo se libera al confirmar exitosamente el nuevo horario.
> *(D-19)*
>
> **Inputs:** Turno a reprogramar · Nueva fecha y hora elegida.
>
> **Proceso:**
> 1. Recibir el turno a reprogramar, de cupo máximo 1, siempre que no haya comenzado. *(Regla 20)*
> 2. Calcular los horarios libres de la misma trabajadora con el mismo criterio de la reserva: horario de
>    atención, menos pausas no laborables, menos sus otros turnos, sin contar los clientes `Cancelado`,
>    verificando que entre la duración total del servicio. *(Reglas 1 y 5)*
> 3. Mostrar los horarios disponibles.
> 4. Recibir el horario elegido.
> 5. Cambiar la fecha y la hora del turno, liberando recién en ese momento el horario anterior. *(Regla 5)*
> 6. Dejar al cliente en estado `Confirmado`, porque el nuevo horario lo eligió él.
>
> **Outputs:** Turno con su nueva fecha y hora · Confirmación inmediata por email del nuevo día y hora al cliente.
> El enlace privado existente se conserva y pasa a vencer 24 horas después del nuevo inicio; no se emite un token
> distinto por reprogramar. Si no se confirma un nuevo horario, el turno original permanece reservado sin cambios.
>
> **Error Handling:** Si no hay ningún horario de esa trabajadora que le sirva al cliente, o si abandona el
> cambio, el turno original se conserva; no se cancela automáticamente. *(`R36`)* Desde el inicio del turno
> original no se admite reprogramación: una atención posterior requiere una reserva nueva (RF-07). Las clases
> grupales no se reprograman: el alumno cancela su inscripción (RF-22) y se anota en otra clase con lugar
> mediante una nueva inscripción (RF-05); no hay traslado automático. El enlace privado sin sesión (RF-23,
> RF-24) no habilita esta reprogramación.
>
> **Trazabilidad:** `R2`, `R4`, `R15`, `R36`, D-01, D-17, D-19, RF-07.

---

> ### RF-10: SUSPENDER CLASE — `C`
>
> **Descripción:** Suspender la clase de una semana puntual y enviar inmediatamente el aviso a cada alumno
> anotado. *(D-22)*
>
> **Inputs:** Clase de la semana a suspender · Motivo.
>
> **Proceso:**
> 1. Recibir la clase a suspender.
> 2. Cambiar su estado a `Suspendido`, sin modificar la grilla. *(Regla 9)*
> 3. Recuperar los alumnos anotados en esa clase.
> 4. No generar ninguna recuperación ni devolución de dinero automáticas: los alumnos con modalidad `Fija`
>    pierden la clase y los de modalidad `Suelta` no la pagan, porque abonan al llegar. *(Regla 17, D-15, D-24)*
> 5. Enviar inmediatamente por email, a cada alumno anotado, el aviso de la suspensión. *(D-22)*
>
> **Outputs:** Clase suspendida · Detalle de los alumnos que estaban anotados · Aviso enviado por email a cada
> uno.
>
> **Error Handling:** Si no hay alumnos anotados, se suspende la clase sin enviar avisos. Un reintento técnico no
> debe duplicar el aviso de suspensión para el mismo alumno y clase.
>
> **Trazabilidad:** `R12`, `R13`, `R34`, `R35`, D-05, D-15, D-22.

---

> ### RF-11: REGISTRAR ATENCIÓN Y COBRO — `E`
>
> **Descripción:** Registrar que un cliente fue atendido o asistió a una clase y su cobro, sea por un turno
> individual, una clase suelta o una atención sin turno. El cobro y la asistencia son datos independientes: se
> pueden registrar en cualquier orden y ninguno modifica automáticamente al otro. *(D-20)*
>
> **Inputs:** Cliente · Servicio o clase · Trabajadora o profesora · Fecha y hora · Concepto cobrado y su
> referencia concreta (turno, atención sin turno o clase suelta) · Importe acordado, con motivo si difiere del
> tarifario (opcional) · Observaciones para la próxima atención (opcional).
>
> **Proceso:**
> 1. Recibir los datos de la atención.
> 2. Registrar al cliente como `Atendido`. Si corresponde cobrar, registrar el importe acordado y marcarlo como
>    `Cobrado`; ambas marcas son independientes y pueden registrarse en cualquier orden. *(`R14`, D-20)*
> 3. Registrar el cobro con fecha, hora, monto, servicio y trabajadora, o con fecha, alumno y monto si es una
>    clase suelta, identificando siempre el concepto y su referencia concreta. *(`R5`, `R14`, D-20)*
> 4. Si se informaron observaciones, agregarlas a la ficha del cliente, como el tono de tintura aplicado o las
>    alergias declaradas. *(`R3`)*
>
> **Outputs:** Cliente en estado `Atendido` · Cobro registrado cuando corresponde, con su concepto y referencia
> concreta · Ficha del cliente con sus observaciones actualizadas, cuando se informaron. **Sin comprobante hacia
> el cliente.** *(D-07)*
>
> **Error Handling:**
> - Si el cliente se presentó **sin turno** y el servicio es de **cupo máximo 1**, se crea el turno en el mismo
>   acto en estado `Programado` y con origen `Sin reserva`, y se continúa desde el paso 2. *(`R21`, D-09)*
> - Si el cliente se presentó **sin turno** y el servicio es de **cupo máximo mayor a 1**, no se crea ningún
>   turno: se lo incorpora con modalidad `Suelta` a la clase de ese horario, respetando su cupo máximo.
>   *(Reglas 2 y 4, D-09)*
> - Si la atención se superpone con otro turno de la misma trabajadora u otra atención, el sistema **informa el
>   conflicto pero registra igual**: el registro nunca se bloquea. *(`R22`, D-10, D-25, Regla 11)*
> - Si el negocio tiene desactivada la atención sin turno (RF-01), no se ofrece el alta de la atención sin
>   reserva. *(`R13`, Regla 15)*
> - El cobro ya registrado no se edita ni se borra: se anula con motivo (RF-28) y, si corresponde, se registra
>   uno nuevo. *(D-20)*
>
> **Trazabilidad:** `R3`, `R5`, `R13`, `R14`, `R21`, `R22`, D-06, D-07, D-09, D-10, D-20, D-25.

---

> ### RF-12: REGISTRAR AUSENCIA — `E`
>
> **Descripción:** Registrar que un cliente no se presentó a su turno o clase, sea porque no avisó y la dueña lo
> comprueba, o porque el cliente confirma "No voy a asistir" desde el enlace privado de su turno individual una
> vez iniciado. *(D-18, D-19)*
>
> **Inputs:** Cliente · Turno o clase de la semana sin presentación · Origen del aviso, si corresponde.
>
> **Proceso:**
> 1. Recibir la indicación de ausencia, sea registrada por la dueña o confirmada por el cliente desde el enlace
>    privado (RF-24) una vez iniciado el turno.
> 2. Registrar al cliente como `Ausente`, dejando constancia del origen cuando lo informó el cliente.
>
> **Outputs:** Cliente en estado `Ausente`, disponible para el recuento de ausencias.
>
> **Error Handling:** El aviso confirmado del cliente no sobrescribe un estado ya registrado como `Atendido`, ni
> un turno completado, ni un lugar ya liberado por una cancelación previa (RF-22). *(D-18, D-19)*
>
> **Trazabilidad:** `R6`, `R14`, `R32`, D-18, D-19, Regla 18.

---

> ### RF-13: REGISTRAR GASTO — `E`
>
> **Descripción:** Registrar un gasto del negocio con su fecha, monto, tipo y descripción.
>
> **Inputs:** Fecha · Monto · Tipo de gasto, entre los tipos del negocio · Descripción.
>
> **Proceso:**
> 1. Recibir los datos del gasto.
> 2. Registrar el gasto asociado al negocio.
>
> **Outputs:** Gasto registrado, clasificado por tipo, disponible para el resultado del período.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R7`, `R27`, RF-01.

---

> ### RF-14: REGISTRAR INSUMO Y SU EXISTENCIA — `E`
>
> **Descripción:** Registrar un insumo o elemento con su existencia mínima, y declarar su existencia actual cada
> vez que se repone o se detecta que falta.
>
> **Inputs:** Nombre · Existencia declarada · Existencia mínima.
>
> **Proceso:**
> 1. Recibir los datos del insumo. Si no existe, registrarlo asociado al negocio.
> 2. Actualizar la existencia con el valor declarado. *(Regla 8)*
> 3. Comparar la existencia contra el mínimo definido.
>
> **Outputs:** Insumo con su existencia actualizada · Alerta de reposición si quedó por debajo del mínimo.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R9`, `R17`, `R26`, D-03.
>
> **Nota:** el alta y la actualización son el mismo proceso sobre el mismo objeto. Dar de alta un insumo es
> declarar su existencia por primera vez, agregando el mínimo.

---

> ### RF-15: CONSULTAR LA JORNADA EN CURSO — `E`
>
> **Descripción:** Mostrar el detalle del día: quién viene, quién fue atendido o asistió, quién falta cobrar y
> cuánto se lleva cobrado.
>
> **Inputs:** Fecha de la jornada.
>
> **Proceso:**
> 1. Recibir la fecha.
> 2. Recuperar los turnos y clases del día con sus anotados y estados.
> 3. Sumar los importes ya cobrados.
>
> **Outputs:** Detalle de la jornada con el parcial cobrado.
>
> **Error Handling:** N/A.
>
> **Nota:** desde esta vista debe poder invocarse RF-11 sin abandonar la pantalla. *(`R22`)*
>
> **Trazabilidad:** `R4`, `R14`, `R22`.

---

> ### RF-16: CONSULTAR REPORTES DEL NEGOCIO — `E`
>
> **Descripción:** Obtener el resultado del período y los indicadores del negocio, con comparación entre
> períodos, excluyendo los cobros anulados y descontando las devoluciones registradas. *(D-20)*
>
> **Inputs:** Período a consultar.
>
> **Proceso:**
> 1. Recibir el período.
> 2. Sumar los cobros vigentes, no anulados, de atenciones, clases sueltas y abonos. *(`R8`, `R27`, D-20)*
> 3. Restar las devoluciones manuales registradas en el período y los gastos. *(`R8`, `R27`, D-20)*
> 4. Calcular los indicadores: facturación por servicio, gastos por tipo, clientes recurrentes, clientes que
>    dejaron de venir, recuento de ausencias y producción por trabajadora. *(`R6`, `R10`)*
>
> **Outputs:** Resultado del período · Indicadores del negocio.
>
> **Error Handling:** Los cobros anulados no integran el resultado del período, aunque su historial se conserva
> junto con la anulación (RF-28). *(D-20)*
>
> **Trazabilidad:** `R6`, `R7`, `R8`, `R10`, `R18`, `R27`, D-20.

---

> ### RF-17: ENVIAR RECORDATORIO DE TURNO — `E`
>
> **Descripción:** Avisar por email al cliente de su turno individual o su clase suelta con la anticipación
> configurada (24 horas por defecto), con deduplicación en los casos límite. Las clases con abono no generan
> recordatorio. *(D-22)*
>
> **Inputs:** Turnos individuales y clases sueltas próximos · Anticipación configurada (24 horas por defecto).
>
> **Proceso:**
> 1. Al cumplirse la anticipación configurada, recuperar los turnos individuales y las clases sueltas
>    alcanzados, excluyendo las clases fijas de abono y los turnos cancelados.
> 2. Para un turno individual, emitir por email el recordatorio con el enlace privado vigente (RF-23),
>    solicitando confirmar la intención de asistir (RF-08) solo si el cliente todavía no confirmó.
> 3. Para una clase suelta, emitir por email el recordatorio con fecha y horario, sin solicitar confirmación.
> 4. Si el turno o la inscripción se realizó con menos de 24 horas de anticipación, no emitir este recordatorio:
>    alcanza con el email inicial enviado al reservar (RF-07, RF-23).
> 5. Si el turno se reservó exactamente 24 horas antes de su inicio, emitir un único email en lugar de dos
>    simultáneos, con el contenido aplicable del recordatorio.
>
> **Outputs:** Recordatorio enviado al cliente, sin duplicar envíos.
>
> **Error Handling:** Si el turno individual fue cancelado, no se envía recordatorio. Un reintento técnico no
> debe duplicar el envío para el mismo turno o clase.
>
> **Trazabilidad:** `R4`, `R16`, D-22, RF-01.

---

> ### RF-18: ENVIAR RESUMEN DE AGENDA DIARIA — `E`
>
> **Descripción:** Enviar por email a la dueña el detalle de la agenda del día siguiente.
>
> **Inputs:** Turnos y clases del día siguiente · Hora de emisión configurada (21:00 por defecto) · Email de
> avisos del negocio.
>
> **Proceso:**
> 1. Al llegar la hora configurada, recuperar los turnos y clases del día siguiente.
> 2. Emitir por email el resumen al email de avisos del negocio.
>
> **Outputs:** Resumen de agenda enviado por email a la dueña.
>
> **Error Handling:** Si el negocio no tiene email de avisos configurado, no se genera el resumen para
> la dueña; la emisión comienza cuando se registra un email (RF-01).
>
> **Trazabilidad:** `R4`, `R16`, RF-01.

---

> ### RF-19: ENVIAR AVISO DE REVISIÓN DE EXISTENCIAS — `E`
>
> **Descripción:** Recordar por email, periódicamente, a la dueña que actualice la existencia de sus insumos.
>
> **Inputs:** Insumos registrados del negocio · Periodicidad configurada (semanal por defecto) · Email de avisos
> del negocio.
>
> **Proceso:**
> 1. Al cumplirse la periodicidad, recuperar los insumos del negocio.
> 2. Emitir por email el aviso de revisión al email de avisos del negocio.
>
> **Outputs:** Aviso de revisión enviado por email a la dueña.
>
> **Error Handling:** Si el negocio no tiene email de avisos configurado, no se genera el aviso de revisión
> para la dueña; la emisión comienza cuando se registra un email (RF-01).
>
> **Trazabilidad:** `R17`, D-03, RF-01.
>
> **Nota:** es consecuencia directa de D-03. Sin descuento automático, la existencia registrada se desactualiza,
> la alerta de RF-14 nunca se dispara y el control de insumos pierde su valor.

---

> ### RF-20: ANULAR INSCRIPCIÓN PENDIENTE — `C`
>
> **Descripción:** Permite que la dueña anule una inscripción pendiente de pago cuando el alumno no paga o no se
> presenta, liberando los lugares retenidos. *(D-21)*
>
> **Inputs:** Inscripción pendiente a anular.
>
> **Proceso:**
> 1. Recuperar la inscripción pendiente de pago.
> 2. Registrar la anulación.
> 3. Liberar todos los lugares fijos retenidos por esa inscripción.
>
> **Outputs:** Inscripción anulada, lugares disponibles para otra persona.
>
> **Error Handling:** El comienzo de la primera clase no anula ni libera la inscripción por sí solo; la anulación
> es siempre una acción manual de la dueña. *(D-21)*
>
> **Trazabilidad:** D-21, RF-05.

---

> ### RF-21: CAMBIAR HORARIO FIJO DEL ABONO — `C`
>
> **Descripción:** Permite que la dueña registre, a pedido del alumno, el cambio de los días u horarios fijos de
> su abono, sujeto a cupo disponible y con vigencia desde una fecha elegida por la dueña. *(D-19)*
>
> **Inputs:** Alumno · Horario fijo actual · Nuevo horario fijo elegido de la grilla (RF-04) · Fecha de
> vigencia.
>
> **Proceso:**
> 1. Recibir el pedido del alumno y el nuevo horario fijo elegido entre las clases de la grilla.
> 2. Verificar que el nuevo horario tenga cupo disponible.
> 3. Registrar el cambio con la fecha de vigencia elegida por la dueña.
> 4. Desde la fecha de vigencia, liberar el lugar del horario anterior; las listas semanales se generan con el
>    nuevo horario (RF-06).
>
> **Outputs:** Horario fijo del abono actualizado desde la fecha de vigencia.
>
> **Error Handling:** Si el nuevo horario no tiene cupo, el cambio no se registra y el alumno conserva sus
> horarios actuales. Las fechas anteriores a la vigencia, con sus asistencias, cancelaciones y suspensiones, no
> se modifican. El cambio no altera el vencimiento, el día de referencia ni el precio del abono (RF-29).
>
> **Trazabilidad:** D-19, RF-04, RF-06, RF-29.

---

> ### RF-22: CANCELAR TURNO O CLASE — `E`
>
> **Descripción:** Registrar que el cliente no va a asistir a su turno individual, su clase con abono o su clase
> suelta, antes del inicio, liberando el lugar correspondiente. Sin plazo mínimo ni aprobación de la dueña.
> *(D-19)*
>
> **Inputs:** Cliente · Turno o clase a cancelar.
>
> **Proceso:**
> 1. Recuperar el turno o la clase elegida, siempre que no haya comenzado.
> 2. Registrar la anotación como cancelada.
> 3. Según el tipo:
>    - **Turno individual:** el horario queda disponible para otra persona. El cliente cancela directamente
>      desde el enlace privado (RF-24) sin sesión, o la dueña lo registra.
>    - **Clase con abono:** se libera únicamente el lugar de esa fecha; los días y horarios fijos se conservan
>      (RF-21).
>    - **Clase suelta:** con cuenta, el cliente cancela desde su panel; sin cuenta, avisa a la dueña y ella
>      registra la cancelación.
>
> **Outputs:** Turno o clase cancelada y lugar liberado.
>
> **Error Handling:** Desde el inicio no se admite esta cancelación; corresponde registrar ausencia (RF-12). La
> cancelación no genera devolución, crédito ni penalización automáticos, ni borra los cobros registrados (D-24).
> Para asistir a otra clase se requiere una nueva inscripción sujeta a cupo (RF-05); no hay traslado automático.
>
> **Trazabilidad:** `R4`, `R32`, D-19, D-24, RF-05, RF-12, RF-24.

---

> ### RF-23: ENVIAR ENLACE PRIVADO "VER MI TURNO" — `E`
>
> **Descripción:** Al reservar un turno individual, enviar inmediatamente por email al cliente sus detalles y un
> enlace privado, reutilizable, que le permite consultar y gestionar ese turno exacto mientras esté vigente.
> *(D-18)*
>
> **Inputs:** Turno individual reservado · Email del cliente.
>
> **Proceso:**
> 1. Al confirmarse la reserva de un turno individual (RF-07), generar un enlace privado asociado únicamente a
>    ese turno.
> 2. Enviar inmediatamente por email al cliente los detalles del turno junto con el enlace.
> 3. El enlace vence 24 horas después del inicio del turno, no de su fin ni de su creación; puede abrirse y
>    usarse varias veces durante su vigencia sin consumirse.
> 4. La dueña puede copiar y compartir manualmente el enlace vigente por el canal que elija.
>
> **Outputs:** Email enviado con los detalles del turno y el enlace privado vigente.
>
> **Error Handling:** Si la dueña sospecha que el enlace fue comprometido o enviado al destinatario equivocado,
> puede revocarlo; el enlace revocado deja de autorizar acceso, y el reemplazo conserva la fecha de vencimiento
> ya acordada, sin extenderla. Vencido el enlace, la persona debe contactar al negocio. No se aprueba una
> integración automática con WhatsApp ni su uso obligatorio.
>
> **Trazabilidad:** D-18, D-22, RF-07.

---

> ### RF-24: GESTIONAR TURNO INDIVIDUAL DESDE EL ENLACE PRIVADO — `E`
>
> **Descripción:** Permite que la persona que posee el enlace privado (RF-23) consulte, confirme su intención de
> asistir o cancele su turno individual antes del inicio, y confirme su inasistencia desde el inicio, sin
> contraseña ni cuenta. *(D-18)*
>
> **Inputs:** Enlace privado vigente · Acción elegida (consultar, confirmar intención, cancelar, confirmar "No
> voy a asistir").
>
> **Proceso:**
> 1. Validar que el enlace esté vigente y no haya sido revocado.
> 2. Mostrar únicamente la fecha, el horario, el servicio y la trabajadora del turno exacto al que se limita
>    el enlace, sin historial, clases, abonos, observaciones ni otros datos del cliente; el enlace no habilita
>    ver otras reservas.
> 3. Antes del inicio del turno, permitir confirmar la intención de asistir (RF-08) o cancelar directamente
>    (RF-22), sin plazo mínimo ni aprobación de la dueña.
> 4. Desde el inicio del turno, permitir confirmar "No voy a asistir", lo que registra Ausente con constancia de
>    que lo informó el cliente (RF-12).
>
> **Outputs:** Consulta del turno y, según la acción elegida, intención de asistir confirmada, turno cancelado o
> Ausente registrado.
>
> **Error Handling:** El enlace no habilita reprogramación (RF-09) ni vincula una cuenta con fichas existentes.
> "No voy a asistir" no sobrescribe un turno ya marcado `Atendido` o completado. Un turno posterior se registra
> como una reserva nueva (RF-07). Un enlace vencido o revocado no permite ninguna acción.
>
> **Trazabilidad:** D-18, RF-08, RF-09, RF-12, RF-22, RF-23.

---

> ### RF-25: REGISTRAR CLIENTE EN FORMA AUTÓNOMA — `O`
>
> **Descripción:** Permite que una persona sin ficha previa cree su propia cuenta para gestionar sus reservas
> desde el panel de cliente. *(D-18)*
>
> **Inputs:** Nombre · Apellido · Email · Contraseña.
>
> **Proceso:**
> 1. La persona completa sus datos y solicita el registro.
> 2. Se verifica su email mediante un mensaje de confirmación.
> 3. Establece su contraseña y se crea su cuenta y ficha de cliente.
>
> **Outputs:** Cuenta y ficha de cliente creadas.
>
> **Error Handling:** Si el email ya está registrado, se informa y no se crea una ficha duplicada; se indica
> iniciar sesión o recuperar la contraseña (RF-27). Si no se verifica el email, la cuenta no se activa.
>
> **Trazabilidad:** D-18.

---

> ### RF-26: INVITAR VINCULACIÓN DE FICHA EXISTENTE — `C`
>
> **Descripción:** Permite que la dueña asocie una cuenta a una ficha de cliente ya existente sin cuenta,
> mediante una invitación que el cliente debe confirmar, sin fusionar historiales. *(D-18)*
>
> **Inputs:** Ficha de cliente · Email de destino confirmado con la persona.
>
> **Proceso:**
> 1. La dueña abre la ficha del cliente y confirma con la persona a qué email enviar la invitación, que puede
>    ser distinto del registrado en la ficha.
> 2. Se genera y envía una invitación válida por 48 horas, de un solo uso y revocable por la dueña.
> 3. El destinatario crea su cuenta o inicia sesión y confirma la vinculación.
> 4. Al confirmar, la cuenta queda asociada a la ficha existente sin crear una ficha nueva.
>
> **Outputs:** Ficha vinculada a la cuenta del cliente.
>
> **Error Handling:** Emitir una nueva invitación invalida la anterior. El vencimiento o la revocación de la
> invitación no afecta las reservas existentes. Si la cuenta del destinatario ya está vinculada a otra ficha, la
> invitación no cambia esa vinculación ni revela el historial de la ficha invitada; se indica contactar al
> negocio para revisión de la dueña. El acceso obtenido por esta vía no incluye observaciones ni notas internas.
>
> **Trazabilidad:** D-18.

---

> ### RF-27: RECUPERAR CONTRASEÑA — `O`
>
> **Descripción:** Permite que un cliente con cuenta restablezca su contraseña mediante un enlace enviado a su
> email verificado. *(D-18)*
>
> **Inputs:** Email verificado del cliente.
>
> **Proceso:**
> 1. El cliente solicita la recuperación desde el ingreso de sesión.
> 2. Se envía un enlace de recuperación válido por 30 minutos y de un solo uso.
> 3. El cliente establece una nueva contraseña desde el enlace.
>
> **Outputs:** Contraseña actualizada.
>
> **Error Handling:** Un enlace ya utilizado o con más de 30 minutos no permite cambiar la contraseña; debe
> solicitarse uno nuevo. La dueña no ve ni asigna contraseñas. Si la persona perdió el acceso a su email, se
> deriva a atención asistida por la dueña.
>
> **Trazabilidad:** D-18.

---

> ### RF-28: ANULAR COBRO — `E`
>
> **Descripción:** Permite que la dueña anule un cobro registrado por error o para corregirlo, sin editarlo ni
> borrarlo. *(D-20)*
>
> **Inputs:** Cobro a anular · Motivo.
>
> **Proceso:**
> 1. Recuperar el cobro a anular.
> 2. Registrar la anulación con motivo, fecha y responsable, enlazada al cobro original.
> 3. Conservar el cobro original en el historial junto con su anulación.
>
> **Outputs:** Cobro anulado, con su historial conservado; excluido del resultado del período (RF-16).
>
> **Error Handling:** Un cobro no puede editarse ni borrarse directamente; solo anularse. Si corresponde, la
> dueña registra el cobro correcto por separado (RF-11, RF-05).
>
> **Trazabilidad:** D-20, RF-16.

---

> ### RF-29: RENOVAR O DAR DE BAJA EL ABONO — `C`
>
> **Descripción:** Gestiona el vencimiento y la renovación del abono mensual: calcula su vencimiento, conserva el
> lugar del alumno hasta que la dueña resuelva y registra la renovación o la baja. *(D-23)*
>
> **Inputs:** Abono del alumno · Fecha de pago o de baja.
>
> **Proceso:**
> 1. El abono dura un mes desde su alta, ocurrida cuando se registró su primer cobro (RF-05). Vence el mismo
>    número de día del mes siguiente; si ese día no existe, vence el último día de ese mes.
> 2. La fecha de vencimiento es el primer día sin cobertura: el período cubre desde el alta hasta el día
>    anterior al vencimiento inclusive, sin depender de la hora del pago.
> 3. Al renovar sin interrupción, registrar el cobro (RF-11) y conservar el día de referencia original; la
>    nueva cobertura comienza al vencimiento del período anterior, aunque el pago sea anticipado.
> 4. Al renovar tarde, la nueva cobertura comienza el día del pago, sin cobertura ni cobro retroactivos por el
>    intervalo vencido; el día de referencia pasa a ser el día del pago.
> 5. Al vencer sin renovar, el abono queda `Vencido`; el lugar fijo se conserva pendiente de resolución y no se
>    ofrece a otra persona (RF-06).
> 6. La dueña registra la renovación o da de baja la inscripción; al dar de baja, se liberan los lugares fijos.
>    La baja no elimina la ficha del cliente, su cuenta ni su historial.
>
> **Outputs:** Abono vigente con su nuevo vencimiento, o inscripción dada de baja con los lugares liberados.
>
> **Error Handling:** Conservar el lugar no habilita asistencia sin abono vigente; el alumno debe renovar antes
> de volver a asistir (RF-12). Si el alumno vuelve tras una baja, se registra como una nueva inscripción sujeta
> a cupo disponible (RF-05), sin garantía de recuperar sus horarios anteriores. Si la inscripción todavía
> conserva sus lugares y solo venció el abono, corresponde renovar, no crear una inscripción nueva.
>
> **Trazabilidad:** D-23, RF-05, RF-06, RF-12.

---

> ### RF-30: ENVIAR AVISO DE VENCIMIENTO DE ABONO — `C`
>
> **Descripción:** Avisar por email al alumno sobre el vencimiento próximo o vencido de su abono, sin insistencia
> diaria ni anuncio de liberación automática del lugar. *(D-22)*
>
> **Inputs:** Abonos con vencimiento próximo o vencido · Fecha de vencimiento (RF-29).
>
> **Proceso:**
> 1. Tres días antes del vencimiento, enviar un email recordando la fecha.
> 2. Al vencer, si el alumno todavía no renovó, enviar un email informando el vencimiento.
>
> **Outputs:** Avisos de vencimiento enviados al alumno.
>
> **Error Handling:** Si el alumno renovó antes del vencimiento, no se envía el aviso de abono vencido
> correspondiente a ese período. No se envían mensajes diarios ni se anuncia liberación automática del lugar; la
> conservación y liberación manual se rigen por RF-29.
>
> **Trazabilidad:** D-22, D-23, RF-29.

---

# 6. REQUERIMIENTOS NO FUNCIONALES

| Categoría | Requerimiento | Trazabilidad |
|---|---|---|
| **Desempeño** | La vista de reserva debe mostrar los horarios disponibles en menos de 3 segundos sobre red móvil 4G, sosteniendo ~3.200 atenciones y ~340 clientes por año por negocio. | `R15`, `R19`, supuestos de `relevamiento.md` §5.2 |
| **Disponibilidad** | La vista de reserva debe estar operativa las 24 horas, porque su razón de ser es recibir reservas fuera del horario de atención. El panel de gestión requiere disponibilidad durante el horario de atención. | `R15` |
| **Confiabilidad** | Un turno, una anotación, un cobro o un abono no pueden perderse ni duplicarse. Debe conservarse respaldo diario: la libreta, los cuadernos y las planillas son hoy el único registro, y el sistema los reemplaza. | `R2`, `R5`, `R27`, `R30` |
| **Seguridad** | El panel de gestión se accede con usuario y contraseña por negocio. El panel de cliente, cuando el cliente opta por tener cuenta, se accede con email y contraseña, con un mecanismo de hash de contraseñas revisado y mantenido. La vista de reserva y el enlace privado son de acceso público sin cuenta y **no deben exponer datos de otros clientes**, incluidas las observaciones de salud de los alumnos: el enlace privado limita su información al turno exacto al que corresponde. | D-06, D-18, `R29` |
| **Portabilidad** | Aplicación web responsiva, operable desde el navegador de un celular sin instalar nada. | `R19` |
| **Accesibilidad** | La vista de reserva debe cumplir **WCAG 2.1 nivel AA**: contraste mínimo de 4,5:1 en texto normal y 3:1 en texto grande, texto ampliable hasta el 200% sin pérdida de contenido, navegación completa por teclado y compatibilidad con lectores de pantalla. | `R19` |
| **Mantenibilidad** | El tarifario, los horarios, las pausas, la grilla, los tipos de gasto, los mínimos de stock y los tiempos de aviso deben ser configurables por la propia dueña, sin intervención técnica. | `R1`, `R12`, `R16`, `R17`, `R28` |

---

# 7. RESTRICCIONES

1. La duración de un servicio es un valor único; el turno ocupa esa duración completa sin dividirse en tramos.
   *(D-25)*
2. El cliente se identifica por su email; el teléfono es un dato de contacto adicional. La cuenta con contraseña
   es opcional. *(D-18)*
3. El cupo máximo de un servicio determina cuántos clientes admite un turno; el turno individual es el caso de
   cupo máximo 1. *(Regla 2, `R11`)*
4. La existencia de un insumo solo se modifica por declaración de la dueña. *(D-03)*
5. El negocio tiene una única cuenta de acceso. Trabajadoras y profesoras son datos, no usuarios. *(D-06)*
6. La validación de disponibilidad y de cupo máximo rige únicamente en el flujo de reserva; no bloquea el
   registro de una atención sin turno. *(D-10, D-25)*
7. El sistema no emite comprobantes fiscales. *(D-07)*
8. Retirada. No se modela un límite de capacidad simultánea del negocio ni de puestos físicos: las reservas
   validan solo disponibilidad de la trabajadora (Regla 1) y cupo máximo de la clase (Regla 2); el registro de
   una atención sin turno nunca se bloquea (Regla 11). *(D-25)*
9. El abono cubre un mes desde la fecha de alta o de renovación; sus clases perdidas por falta o suspensión no se
   recuperan ni se devuelve el dinero automáticamente. *(`R30`, `R32`, `R34`, D-13, D-15, D-23, D-24)*
10. Un cobro registrado no se edita ni se borra; solo se anula con motivo, fecha y responsable. *(D-20)*
11. La inscripción pendiente retiene sus lugares hasta que la dueña registre el cobro o la anule; el inicio de la
    primera clase no la vence automáticamente. *(D-21)*
12. El enlace privado de un turno individual vence 24 horas después de su inicio, no habilita reprogramación ni
    acceso a otros datos del cliente, y no reemplaza la atención sin cuenta. *(D-18)*

---

# 8. CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Relevamiento narrativo | `Relevamiento WorkUp.md` | Fuente |
| Análisis del relevamiento | `relevamiento.md` | Emitido |
| Propuestas de resolución de brechas de requerimientos | `docs/propuestas-resolucion-requerimientos.md` | Incorporado en la v3.0 |
| Requerimientos funcionales narrativos | `docs/requerimientos-funcionales.md` | Actualizado junto con esta versión |
| Especificación de Requerimientos (IEEE 830) | `docs/srs.md` | Este documento (v3.0). `srs.md` (raíz, v2.2) queda como antecedente histórico |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
| Evoluciones futuras | `evoluciones/` | Fuera de alcance |
