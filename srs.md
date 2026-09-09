# WORKUP
## Especificaciones de Requerimientos de Software

**Versión:** 1.0 — **Fecha:** 09-09-2026
**Equipo:** Bapton Solutions
**Documento fuente:** `relevamiento.md` v4.0

**Objeto de este documento.** Especifica **qué necesita** el usuario y qué debe hacer el sistema. El
funcionamiento actual se describe en `relevamiento.md`; el modelado (Lista de Eventos, DFD, DD, DER) en
`analisis.md`. Todo requerimiento de este documento es trazable a un identificador `{R#}` del relevamiento.

---

# 1. INTRODUCCIÓN

## 1.1 Propósito

Definir los requerimientos funcionales y no funcionales de WorkUp, un sistema web de gestión para emprendimientos
de servicios prestados por turno.

## 1.2 Unidad de negocio

Un emprendimiento de servicios que se prestan **en un turno con un trabajador asignado**, con uno o más
trabajadores, donde un turno puede recibir uno o varios clientes según el cupo definido para el servicio.

## 1.3 Alcance

**Comprende:** definición del tarifario con precio, duración por tramos y cupo; definición de trabajadores con
horario y bloqueos; definición de la grilla recurrente y generación de turnos; reserva autogestionada por el
cliente con elección de trabajador; confirmación y cancelación; registro de atención, cobro y ausencia por
cliente; registro de atenciones sin turno; vista de la jornada en curso; registro de gastos por tipo; control de
existencias de insumos con alerta de mínimo; avisos automáticos; consulta de reportes del negocio; y alta del
negocio con su cuenta única de acceso y sus parámetros de operación.

**No comprende:**

| Exclusión | Justificación |
|---|---|
| Comercios de venta de productos | Sin turno, trabajador asignado ni duración: no comparten ninguna regla relevada (D-04) |
| Trabajadores como usuarios del sistema | Agregaría una regla de autorización a cada requerimiento sin resolver ningún problema declarado (D-06) |
| Cuenta de cliente e historial autogestionado | Consecuencia de D-02 |
| Cálculo automático de consumo de insumos | No existe base relevada: `R9` establece que el consumo no se registra y no es constante (D-03) |
| Comprobantes y facturación fiscal | `R5`: hoy no se entrega comprobante; ambos casos son monotributistas (D-07) |
| Cobro en línea y seña | No planteado por las usuarias. Se deja constancia de que la seña es el mecanismo más efectivo contra el ausentismo según la referencia del sector |
| Trabajos en múltiples sesiones | Requiere vincular turnos entre sí y administrar pagos parciales |
| Identificación y asignación de puestos o salas | El sistema controla *cuántos* clientes simultáneos admite el negocio, no *cuál* puesto ocupa cada uno (D-11) |
| Optimización automática de la agenda | El sistema ofrece los huecos disponibles pero no sugiere ni reordena turnos |

## 1.4 Glosario

Vocabulario cerrado. Ningún documento del proyecto puede usar otro término para estos conceptos.

| Término | Aplica a | Significado |
|---|---|---|
| `Pendiente` | Cliente en turno | Reservó pero todavía no confirmó asistencia |
| `Confirmado` | Cliente en turno | Respondió el mensaje confirmando que asiste |
| `Atendido` | Cliente en turno | Se presentó y recibió el servicio |
| `Cobrado` | Cliente en turno | Se registró el importe percibido |
| `Cancelado` | Cliente en turno | Avisó que no asiste; su lugar se liberó |
| `Ausente` | Cliente en turno | No se presentó y no avisó |
| `Programado` | Turno | Existe en la agenda. Admite inscripciones mientras no alcance su cupo |
| `Dado de baja` | Turno | Anulado para esa fecha; no admite inscripciones |
| `Reservado` | Origen del turno | Se creó porque un cliente reservó por anticipado |
| `Sin reserva` | Origen del turno | Se creó en el momento de atender a quien se presentó sin turno |
| `Aplicación` / `Espera` / `Terminación` | Tramo del servicio | Los tres tramos posibles. Solo `Aplicación` y `Terminación` ocupan al trabajador |
| Tramo activo | Tramo del servicio | `Aplicación` o `Terminación` |
| Capacidad simultánea | Negocio | Cuántos clientes puede tener el negocio en atención al mismo tiempo |

---

# 2. NECESIDADES MANIFESTADAS

Origen de los requerimientos. Son las necesidades expresadas por las usuarias durante el relevamiento y
conservan la numeración `{R#}` de ese documento.

**`{R15}` Reserva autogestionada.** Contestar mensajes para coordinar turnos les interrumpe el trabajo
constantemente, y muchas consultas llegan fuera del horario de atención. Quieren que sus clientes puedan ver la
disponibilidad real y anotarse por su cuenta, sin que ellas tengan que intervenir. El cliente informa sus datos
de contacto al reservar y la reserva queda pendiente hasta que se confirma. Marina insiste en que la clienta pueda
elegir con quién se atiende, porque "vienen por vos, no por el local".

**`{R16}` Avisos automáticos.** Las ausencias les generan pérdidas, y hoy los recordatorios los mandan a mano,
uno por uno, y a veces se olvidan. Necesitan que el aviso al cliente salga solo **24 horas antes del turno**, y
recibir ellas un resumen de su agenda del día siguiente **a las 21:00**. Ambos tiempos deben poder ajustarse.

**`{R17}` Control de existencias de insumos.** Necesitan saber cuánto queda de cada insumo antes de quedarse sin
stock en medio de la jornada, y que el sistema les avise cuando algo esté por debajo del mínimo que ellas
definan. Marina pide además que el sistema le recuerde revisar el stock, porque sabe que si no se lo piden no lo
va a actualizar. Propone que ese recordatorio le llegue una vez por semana.

**`{R18}` Información del negocio.** Necesitan el resultado del período calculado solo, y poder responder qué
servicio deja más, qué clientes vuelven, cuáles dejaron de venir, cuántos turnos se cayeron por ausencia y cuánto
trabajó cada trabajador.

**`{R19}` Uso desde el celular y clientas mayores.** Ambas trabajan de pie y usan exclusivamente el celular.
Marina señala que alrededor de un tercio de su cartera son clientas mayores de 65 años que "se pelean con el
teléfono", y que si la pantalla de reserva no es simple y con letra grande, van a seguir escribiéndole por
WhatsApp.

**`{R22}` Vista de la jornada.** Marina pide una pantalla donde vea el día completo de un vistazo: quién viene,
quién ya fue atendida, quién falta cobrar y cuánto lleva cobrado. Insiste en dos condiciones: poder anotar una
atención sin turno en el momento y sin salir de esa pantalla, y que el sistema **no le impida** registrar algo
porque "no entra" en el horario. Textualmente: "si yo lo hice, lo hice; el sistema me tiene que dejar anotarlo,
no discutirme".

**`{R23}` Reserva sin bloquear el procesado.** Si el sistema le bloquea las dos horas enteras de una coloración,
no lo va a poder usar, porque perdería los turnos que hoy mete en el medio. Necesita que los horarios ofrecidos a
las clientas tengan en cuenta que durante el procesado ella está libre.

---

# 3. DECISIONES ADOPTADAS

Resolución de las ambigüedades detectadas en `relevamiento.md` Sección 4, y decisiones de diseño que condicionan
los requerimientos.

| ID | Decisión | Fundamento |
|---|---|---|
| **D-01** | El cliente elige con qué trabajador se atiende, antes de ver horarios | `R2` ofrece "el horario más cercano de esa misma persona"; `R15` lo confirma |
| **D-02** | La reserva no requiere cuenta: nombre, apellido y teléfono | Los datos coinciden con `R3`; `R19` advierte que la fricción hace abandonar el flujo |
| **D-03** | El sistema registra existencias declaradas, no calcula consumo | `R9`: el consumo no se registra y no es constante entre atenciones |
| **D-04** | Alcance multi-rubro acotado a servicios prestados por turno con trabajador asignado | El análisis de variabilidad muestra que cambian los valores, no las reglas |
| **D-05** | La grilla del Caso B es recurrente; el sistema genera las instancias semanales | `R12`: la grilla se revisa una o dos veces al año, pero las bajas son por semana |
| **D-06** | Cuenta única por negocio; el trabajador es un dato del turno | `R5` ya registra "quién atendió" como dato, no como operador |
| **D-07** | El sistema no emite comprobantes fiscales | `R5`: hoy no se entrega comprobante. Monotributo |
| **D-08** | El servicio se define en tramos; la disponibilidad bloquea solo los tramos activos | `R1`, `R20`, `R23`: durante el procesado el trabajador está libre |
| **D-09** | La atención sin turno **es** un turno, creado al atender, con origen `Sin reserva` | Todo lo posterior es idéntico: consume tiempo, genera cobro, entra en reportes |
| **D-10** | El sistema valida reservas; no impide registros | `R22`: "si yo lo hice, lo hice" |
| **D-11** | Capacidad simultánea del negocio: un número, sin identificar puestos | `R24`: cuatro puestos son el límite real al solapar |
| **D-12** | La atención se fecha cuando ocurrió, no cuando se cargó | `R24`: muchas veces anota al cierre, con la hora real |

**Nota sobre D-08.** Es la decisión que hace viable el producto. Sin ella el sistema bloquearía 58 horas
mensuales de capacidad que la libreta de papel sí permite usar, y sería un retroceso frente a la herramienta que
viene a reemplazar.

---

# 4. REGLAS DE NEGOCIO

1. **Disponibilidad por trabajador.** El horario libre se calcula sobre el horario de atención del trabajador
   asignado, descontando sus bloqueos no laborables, los **tramos activos** de sus turnos ya tomados y la
   duración del servicio elegido. Dos turnos pueden coincidir si corresponden a trabajadores distintos.
   *(`R1`, `R2`, D-08)*
2. **Cupo por servicio.** Cada servicio define cuántos clientes admite un turno. No se puede anotar más clientes
   que el cupo. *(`R11`, `R13`)*
3. **El estado y el importe corresponden al cliente, no al turno.** En un turno de cupo mayor a 1, cada cliente
   tiene su propia confirmación, cobro y asistencia. *(`R14`)*
4. **Momento de creación del turno.** Cupo 1: se crea al reservar. Cupo mayor a 1: se genera desde la grilla y
   los clientes se anotan sobre uno existente. Sin reserva: se crea al atender. *(`R2`, `R12`, `R13`, `R21`)*
5. **Liberación de lugar.** Al cancelar, el lugar vuelve a estar disponible dentro del mismo turno.
   *(`R4`, `R13`)*
6. **Un servicio pertenece a un negocio.** Precio, duración, tramos y cupo se definen a nivel del negocio.
   *(`R1`, `R11`)*
7. **El cliente se identifica por su teléfono.** Dos reservas con el mismo teléfono son el mismo cliente.
   *(`R3`, `R15`, D-02)*
8. **La existencia de insumos es un valor declarado.** El sistema nunca la modifica por sí mismo.
   *(`R9`, `R17`, D-03)*
9. **La baja de un turno generado no altera la grilla.** Afecta solo a esa semana. *(`R12`, D-05)*
10. **El tramo de espera no ocupa al trabajador.** El cliente, en cambio, sigue ocupado hasta el último tramo.
    *(`R1`, `R20`, `R23`, D-08)*
11. **La validación de superposición y de cupo solo se aplica al reservar.** Al registrar, el sistema informa el
    conflicto pero no lo impide. *(`R22`, D-10)*
12. **Toda atención genera un turno.** No existe ingreso registrado sin un turno que lo explique.
    *(`R21`, D-09)*
13. **Capacidad simultánea.** Al reservar no se ofrece un horario si ya hay tantos clientes en atención como la
    capacidad configurada, incluidos los que están en tramo de espera. *(`R24`, D-11)*
14. **La atención se fecha cuando ocurrió.** *(`R24`, D-12)*
15. **Ante falta de lugar, el sistema no decide.** Se ofrece volver más tarde (sin registro) o reservar un turno
    (reserva común). *(`R21`)*

16. **La atención sin turno se habilita por negocio.** Un negocio cuya oferta se publica por grilla puede no
    admitirla. *(`R13`, `R21`, tabla de variabilidad)*

---
# 5. REQUERIMIENTOS FUNCIONALES

**Prioridad:** E = Esencial · C = Condicional · O = Opcional · D = Deseable

Los requerimientos se agrupan por su momento en el ciclo de negocio: **configuración** (RF-01 a RF-04),
**operación** (RF-05 a RF-11), **consulta** (RF-12 y RF-13) y **avisos programados** (RF-14 a RF-16). La
numeración no indica orden de ejecución.

---

> ### RF-01: DAR DE ALTA Y CONFIGURAR EL NEGOCIO — `E`
>
> **Descripción:** Registrar o modificar el negocio, su única cuenta de acceso al panel de gestión, el canal por
> el que recibe sus avisos y los parámetros de operación que el resto de los requerimientos consume.
>
> **Inputs:** Nombre del negocio · Usuario · Contraseña · Teléfono de contacto del negocio · Capacidad
> simultánea (sin valor por defecto: depende del local) · Admite atención sin turno, sí o no (sí por defecto) ·
> Anticipación del recordatorio de turno (24 horas por defecto) · Hora de emisión del resumen de agenda (21:00
> por defecto) · Periodicidad del aviso de revisión de existencias (semanal por defecto).
>
> **Proceso:**
> 1. Recibir los datos del negocio.
> 2. Registrar la cuenta única de acceso al panel de gestión. *(Restricción 6)*
> 3. Registrar el teléfono al que se dirigen el resumen de agenda y los avisos de existencias.
> 4. Validar la capacidad simultánea contra el producto del cupo máximo del tarifario por la cantidad de
>    trabajadores. *(Restricción 10)*
> 5. Registrar los parámetros asociados al negocio.
>
> **Outputs:** Negocio registrado con su cuenta de acceso, su canal de contacto y sus parámetros, disponibles
> para RF-06, RF-08, RF-14, RF-15 y RF-16.
>
> **Error Handling:** Si la capacidad simultánea informada es menor que el producto del cupo máximo del
> tarifario por la cantidad de trabajadores, no se registra ese valor. *(Restricción 10)*
>
> **Trazabilidad:** `R12`, `R16`, `R17`, `R21`, `R24`, D-06, D-11, Restricción 6, RNF Seguridad, RNF
> Mantenibilidad.
>
> **Nota:** el alta de la cuenta no proviene de un hecho relevado —los casos estudiados operan en papel y no
> tienen ninguna cuenta— sino de la decisión D-06 y del RNF de Seguridad. Se especifica porque sin él la
> Restricción 6 queda declarada sin ningún requerimiento que la produzca, y RF-15 y RF-16 no tendrían a dónde
> enviar sus avisos.

---

> ### RF-02: DEFINIR SERVICIO DEL TARIFARIO — `E`
>
> **Descripción:** Registrar o modificar un servicio ofrecido por el negocio, con su precio, sus tramos de
> duración y su cupo.
>
> **Inputs:** Nombre del servicio · Precio · Duración del tramo de aplicación · Duración del tramo de espera
> (cero si no tiene) · Duración del tramo de terminación (cero si no tiene) · Cupo de clientes.
>
> **Proceso:**
> 1. Recibir los datos del servicio.
> 2. Calcular la duración total como la suma de los tres tramos.
> 3. Registrar el servicio asociado al negocio.
>
> **Outputs:** Servicio registrado en el tarifario.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R1`, `R11`, D-08.

---

> ### RF-03: DEFINIR TRABAJADOR Y SU HORARIO DE ATENCIÓN — `E`
>
> **Descripción:** Registrar o modificar un trabajador del negocio con los días y horas en que atiende, y los
> bloqueos no laborables dentro de su jornada.
>
> **Inputs:** Nombre del trabajador · Días de atención · Hora de inicio y de fin por día · Bloqueos no
> laborables (hora de inicio y de fin).
>
> **Proceso:**
> 1. Recibir los datos del trabajador.
> 2. Registrar su horario de atención por día.
> 3. Registrar sus bloqueos no laborables.
>
> **Outputs:** Trabajador registrado, con su horario disponible para el cálculo de disponibilidad.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R1`, `R25`.

---

> ### RF-04: DEFINIR CLASE EN LA GRILLA SEMANAL — `C`
>
> **Descripción:** Registrar o modificar en la grilla recurrente qué servicio se dicta, qué día de la semana, a
> qué hora y quién lo dicta. Aplica a servicios de cupo mayor a 1.
>
> **Inputs:** Servicio · Día de la semana · Hora de inicio · Trabajador que lo dicta.
>
> **Proceso:**
> 1. Recibir los datos de la clase.
> 2. Registrar la entrada en la grilla del negocio.
>
> **Outputs:** Clase incorporada a la grilla semanal.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R12`, D-05.

---

> ### RF-05: GENERAR LOS TURNOS DE LA SEMANA — `C`
>
> **Descripción:** Crear automáticamente los turnos de la semana entrante a partir de la grilla recurrente.
>
> **Inputs:** Grilla semanal del negocio · Fecha de la semana a generar.
>
> **Proceso:**
> 1. Recorrer las clases de la grilla.
> 2. Crear un turno en estado `Programado` por cada una, con su fecha concreta, su trabajador y su cupo.
>
> **Outputs:** Turnos de la semana disponibles para inscripción.
>
> **Error Handling:** Si una clase generada debe caerse una semana puntual (feriado o ausencia del trabajador),
> se da de baja esa instancia y se notifica a los clientes anotados, sin alterar la grilla. *(`R12`, Regla 9)*
>
> **Trazabilidad:** `R12`, D-05.
>
> **Nota:** este requerimiento no proviene de un hecho relevado. En la situación actual la grilla *es* la
> agenda, y los alumnos se anotan directamente sobre ella. La generación de instancias semanales surge de D-05 y
> existe para que las inscripciones, las bajas y las asistencias tengan una fecha concreta a la que asociarse.

---

> ### RF-06: RESERVAR TURNO — `E`
>
> **Descripción:** Permitir que el cliente elija servicio y trabajador, consulte la disponibilidad real y reserve
> su lugar, informando sus datos de contacto.
>
> **Inputs:** Servicio elegido · Trabajador elegido · Fecha y hora elegidas · Nombre · Apellido · Teléfono.
>
> **Proceso:**
> 1. Recibir el servicio y el trabajador elegidos.
> 2. Determinar la oferta según el cupo del servicio. *(Regla 4)*
>    - **Cupo 1** — calcular los horarios libres: horario de atención del trabajador, menos sus bloqueos, menos
>      los tramos activos de sus turnos ya tomados, **sin contar los clientes en estado `Cancelado`**,
>      verificando que entre la duración total del servicio. *(Reglas 1 y 5)*
>    - **Cupo mayor a 1** — recuperar los turnos en estado `Programado` de ese servicio y de ese trabajador cuya
>      cantidad de clientes anotados, **excluidos los `Cancelado`**, sea menor que el cupo. *(Reglas 2 y 5)*
> 3. Descartar los horarios en los que la cantidad de clientes en atención alcanza la capacidad simultánea del
>    negocio. *(Regla 13)*
> 4. Mostrar los horarios disponibles.
> 5. Recibir el horario elegido y los datos de contacto.
> 6. Identificar al cliente por su teléfono; si no existe, registrarlo. *(Regla 7)*
> 7. Si el servicio tiene cupo 1, crear el turno en estado `Programado` con origen `Reservado`. Si tiene cupo
>    mayor a 1, incorporar al cliente al turno `Programado` elegido, sin crear ninguno. *(Regla 4)*
> 8. Dejar al cliente en estado `Pendiente`.
>
> **Outputs:** Reserva registrada en estado `Pendiente` · Confirmación de día, hora y trabajador al cliente.
>
> **Error Handling:**
> - Si el trabajador elegido no tiene disponibilidad en el día pedido, se ofrece su horario libre más cercano,
>   **de esa misma persona**. *(`R2`, D-01)*
> - Si el turno de cupo mayor a 1 ya está completo, se ofrecen otros días u horarios del mismo servicio.
>   *(`R13`, Regla 2)*
>
> **Trazabilidad:** `R2`, `R3`, `R13`, `R15`, `R23`, `R24`, D-01, D-02, D-08, D-11, RF-01.

---

> ### RF-07: CONFIRMAR RESERVA — `E`
>
> **Descripción:** Registrar el aviso del cliente sobre su asistencia al turno, dejando su lugar confirmado o
> liberándolo.
>
> **Inputs:** Aviso del cliente sobre su asistencia, recibido en respuesta al recordatorio o por iniciativa
> propia en cualquier momento previo al turno.
>
> **Proceso:**
> 1. Recibir el aviso del cliente, sea en respuesta al recordatorio o por iniciativa propia.
> 2. Cambiar su estado a `Confirmado`.
>
> **Outputs:** Cliente en estado `Confirmado` dentro de su turno.
>
> **Error Handling:** Si el cliente avisa que no puede asistir, su estado pasa a `Cancelado` y su lugar vuelve a
> estar disponible dentro del mismo turno. *(`R4`, `R13`, Regla 5)*
>
> **Trazabilidad:** `R4`, `R13`, `R15`.

---

> ### RF-08: REGISTRAR ATENCIÓN Y COBRO — `E`
>
> **Descripción:** Registrar que un cliente fue atendido y el importe percibido, tanto para turnos reservados
> como para clientes que se presentaron sin turno.
>
> **Inputs:** Cliente · Servicio realizado · Trabajador que atendió · Fecha y hora de la atención · Importe
> cobrado.
>
> **Proceso:**
> 1. Recibir los datos de la atención, con la fecha y hora en que efectivamente ocurrió. *(Regla 14)*
> 2. Registrar al cliente como `Atendido` y como `Cobrado` mediante dos marcas independientes, que pueden
>    registrarse en cualquier orden: el cliente puede pagar al llegar y la asistencia confirmarse recién al
>    finalizar el servicio. *(`R14`)*
> 3. Registrar el ingreso asociado a ese turno.
>
> **Outputs:** Cliente en estado `Atendido` y `Cobrado` · Ingreso registrado. **Sin comprobante hacia el
> cliente.** *(D-07)*
>
> **Error Handling:**
> - Si el cliente se presentó **sin turno** y el servicio tiene **cupo 1**, se crea el turno en el mismo acto en
>   estado `Programado` y con origen `Sin reserva`, con la fecha y hora de la atención, y se continúa desde el
>   paso 2. *(`R21`, D-09, Regla 12)*
> - Si el cliente se presentó **sin turno** y el servicio tiene **cupo mayor a 1**, no se crea ningún turno: se
>   lo incorpora al turno `Programado` que ya existe para ese horario, respetando su cupo, y se continúa desde el
>   paso 2. Crear un turno aparte duplicaría el horario y eludiría el control de cupo. *(Reglas 2 y 4, D-09)*
> - Si la atención se superpone con otro turno del mismo trabajador, el sistema **informa el conflicto pero
>   registra igual**. *(`R22`, D-10, Regla 11)*
> - Si no hay lugar para atender a quien se presentó sin turno, se le ofrece volver más tarde —lo que no genera
>   ningún registro— o reservar un turno mediante RF-06. *(`R21`, Regla 15)*
> - Si el negocio tiene desactivada la atención sin turno (RF-01), no se ofrece el alta de la atención sin
>   reserva. Es el caso del estudio de yoga, donde los alumnos siempre se anotan sobre una clase de la grilla.
>   *(`R13`, Regla 16)*
>
> **Trazabilidad:** `R5`, `R14`, `R21`, `R22`, `R24`, D-07, D-09, D-10, D-12.

---

> ### RF-09: REGISTRAR AUSENCIA — `E`
>
> **Descripción:** Registrar que un cliente no se presentó a su turno y tampoco avisó.
>
> **Inputs:** Cliente · Turno vencido sin presentación ni aviso.
>
> **Proceso:**
> 1. Recibir la indicación de ausencia.
> 2. Registrar al cliente como `Ausente` en ese turno.
>
> **Outputs:** Cliente en estado `Ausente`, disponible para el reporte de ausencias.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R6`, `R14`.

---

> ### RF-10: REGISTRAR GASTO — `E`
>
> **Descripción:** Registrar un egreso del negocio con su fecha, importe, tipo y descripción.
>
> **Inputs:** Fecha · Importe · Tipo de gasto (insumos, alquiler, servicios, transporte, impuestos y tasas,
> varios) · Descripción.
>
> **Proceso:**
> 1. Recibir los datos del gasto.
> 2. Registrar el egreso asociado al negocio.
>
> **Outputs:** Egreso registrado, clasificado por tipo, disponible para el cálculo del resultado del período y
> para el desglose de egresos de RF-13.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R7`, `R27`.

---

> ### RF-11: REGISTRAR INSUMO Y SU EXISTENCIA — `E`
>
> **Descripción:** Registrar un insumo con su existencia mínima, y declarar su existencia actual cada vez que el
> profesional repone o detecta consumo.
>
> **Inputs:** Nombre del insumo · Existencia declarada · Existencia mínima.
>
> **Proceso:**
> 1. Recibir los datos del insumo. Si no existe, registrarlo asociado al negocio.
> 2. Actualizar la existencia con el valor declarado por el profesional. *(Regla 8)*
> 3. Comparar la existencia contra el mínimo definido.
>
> **Outputs:** Insumo registrado con su existencia actualizada · Alerta de reposición si el valor quedó por
> debajo del mínimo.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R9`, `R17`, `R26`, D-03.
>
> **Nota:** el alta y la actualización son el mismo proceso sobre el mismo objeto, con el mismo actor y el mismo
> origen relevado. Dar de alta un insumo es declarar su existencia por primera vez, agregando el mínimo.

---

> ### RF-12: CONSULTAR LA JORNADA EN CURSO — `E`
>
> **Descripción:** Mostrar el detalle del día: quién viene, quién fue atendido, quién falta cobrar y cuánto se
> lleva cobrado hasta ese momento.
>
> **Inputs:** Fecha de la jornada.
>
> **Proceso:**
> 1. Recibir la fecha.
> 2. Recuperar los turnos del día con sus clientes y estados.
> 3. Sumar los importes ya cobrados.
>
> **Outputs:** Detalle de la jornada con el parcial cobrado.
>
> **Error Handling:** N/A.
>
> **Nota:** desde esta vista debe poder invocarse RF-08 sin abandonar la pantalla. *(`R22`)*
>
> **Trazabilidad:** `R4`, `R22`.

---

> ### RF-13: CONSULTAR REPORTES DEL NEGOCIO — `E`
>
> **Descripción:** Obtener el resultado del período y los indicadores del negocio, con comparación entre
> períodos.
>
> **Inputs:** Período a consultar.
>
> **Proceso:**
> 1. Recibir el período.
> 2. Sumar los ingresos registrados y restar los egresos del período.
> 3. Calcular los indicadores: facturación por servicio, egresos por tipo de gasto, clientes recurrentes,
>    clientes que dejaron de venir, cantidad de ausencias y producción por trabajador. *(`R7`, `R10`)*
>
> **Outputs:** Resultado del período · Indicadores del negocio.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R7`, `R8`, `R10`, `R18`, `R27`.

---

> ### RF-14: ENVIAR RECORDATORIO DE TURNO — `E`
>
> **Descripción:** Avisar al cliente de su turno con la anticipación configurada.
>
> **Inputs:** Turnos próximos · Anticipación configurada (24 horas por defecto).
>
> **Proceso:**
> 1. Al cumplirse la anticipación configurada, recuperar los turnos alcanzados.
> 2. Emitir el aviso al teléfono de cada cliente `Pendiente`.
>
> **Outputs:** Recordatorio enviado al cliente.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R4`, `R16`, RF-01.

---

> ### RF-15: ENVIAR RESUMEN DE AGENDA DIARIA — `E`
>
> **Descripción:** Enviar al profesional el detalle de su agenda del día siguiente.
>
> **Inputs:** Turnos del día siguiente · Hora de emisión configurada (21:00 por defecto) · Teléfono de contacto
> del negocio.
>
> **Proceso:**
> 1. Al llegar la hora configurada, recuperar los turnos del día siguiente.
> 2. Emitir el resumen al teléfono de contacto del negocio.
>
> **Outputs:** Resumen de agenda enviado al profesional.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R4`, `R16`, RF-01.

---

> ### RF-16: ENVIAR AVISO DE REVISIÓN DE EXISTENCIAS — `E`
>
> **Descripción:** Recordar periódicamente al profesional que actualice la existencia de sus insumos.
>
> **Inputs:** Insumos registrados del negocio · Periodicidad configurada (semanal por defecto) · Teléfono de
> contacto del negocio.
>
> **Proceso:**
> 1. Al cumplirse la periodicidad, recuperar los insumos del negocio.
> 2. Emitir el aviso de revisión al teléfono de contacto del negocio.
>
> **Outputs:** Aviso de revisión enviado al profesional.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R17`, D-03, RF-01.
>
> **Nota:** este requerimiento es consecuencia directa de D-03. Sin descuento automático, la existencia
> registrada se desactualiza sola, la alerta de RF-11 nunca se dispara y el control de insumos pierde todo valor.

---

# 6. REQUERIMIENTOS NO FUNCIONALES

| Categoría | Requerimiento | Trazabilidad |
|---|---|---|
| **Desempeño** | La vista de reserva debe mostrar los horarios disponibles de un trabajador en menos de 3 segundos sobre red móvil 4G, sosteniendo el volumen relevado (~3.200 turnos y ~340 clientes por año, tomando el Caso A como caso de referencia superior). | `R15`, `R19`, volumetría |
| **Disponibilidad** | La vista de reserva debe estar operativa las 24 horas: su razón de ser es recibir reservas fuera del horario de atención. El panel de gestión requiere disponibilidad durante el horario de atención. | `R15` |
| **Confiabilidad** | Un turno confirmado no puede perderse ni duplicarse. Debe conservarse respaldo diario: la libreta y el cuaderno son hoy el único registro de turnos y de dinero, y el sistema los reemplaza. | `R2`, `R5`, `R7`, `R27` |
| **Seguridad** | El panel de gestión se accede con usuario y contraseña por negocio. La vista de reserva es pública y **no debe exponer datos de otros clientes**: solo muestra horarios libres u ocupados, nunca el nombre de quien ocupa un turno. | D-02, D-06 |
| **Portabilidad** | Aplicación web responsiva, operable desde el navegador de un celular sin instalar nada. Es el único dispositivo que usan ambas usuarias. | `R19` |
| **Accesibilidad** | La vista de reserva debe ser operable por personas mayores con baja familiaridad tecnológica: debe cumplir **WCAG 2.1 nivel AA**, con ratio de contraste mínimo de 4,5:1 en texto normal y 3:1 en texto grande, tamaño de texto ampliable hasta el 200% sin pérdida de contenido, navegación completa por teclado y compatibilidad con lectores de pantalla. Un tercio de la cartera del Caso A entra en ese perfil. | `R19` |
| **Mantenibilidad** | El tarifario, los horarios, los bloqueos, la capacidad simultánea, los mínimos de stock y los tiempos de aviso deben ser configurables por la propia usuaria, sin intervención técnica. | `R1`, `R16`, `R17`, `R24` |

---

# 7. RESTRICCIONES

1. La duración total de un servicio es la suma de sus tramos de aplicación, espera y terminación. *(D-08)*
2. Solo los tramos de aplicación y terminación ocupan al trabajador. *(D-08)*
3. El cupo de un servicio determina cuántos clientes admite un turno; el turno individual es el caso de cupo 1.
   *(Regla 2, R11)*
4. El cliente se identifica por su teléfono. No existen credenciales de cliente. *(D-02)*
5. La existencia de un insumo solo se modifica por declaración del profesional. *(D-03)*
6. El negocio tiene una única cuenta de acceso. El trabajador es un dato, no un usuario. *(D-06)*
7. La validación de disponibilidad, cupo y capacidad simultánea rige únicamente en el flujo de reserva.
   *(D-10)*
8. La fecha y hora de una atención son las de su ocurrencia, no las de su carga. *(D-12)*
9. El sistema no emite comprobantes fiscales ni se integra con ARCA. *(D-07)*
10. La capacidad simultánea del negocio no puede ser menor que el **producto del cupo máximo de su tarifario por
    la cantidad de trabajadores**. Un tope menor impediría que dos trabajadores atiendan en paralelo a cupo
    completo, situación que `R12` describe como normal. *(Reglas 2 y 13, `R12`, `R24`, D-11)*

---

# 8. CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Informe de Relevamiento | `relevamiento.md` | Emitido |
| Especificación de Requerimientos (IEEE 830) | `srs.md` | Este documento |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
