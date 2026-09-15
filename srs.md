# WORKUP
## Especificaciones de Requerimientos de Software

**Versión:** 2.2 — **Fecha:** 14-09-2026
**Equipo:** Bapton Solutions
**Documentos fuente:** `Relevamiento WorkUp.md` · `relevamiento.md` v5.2

**Objeto de este documento.** Especifica **qué necesita** el usuario y qué debe hacer el sistema. El
funcionamiento actual se describe en `Relevamiento WorkUp.md` y se analiza en `relevamiento.md`; el modelado
(Lista de Eventos, DFD, DD, DER) irá en `analisis.md`. Todo requerimiento es trazable a un identificador `{R#}`.

> **Versión 2.2.** Se incorpora la reprogramación de turnos individuales (RF-09) y se renumeran los
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
cliente paga por atención, por clase suelta o mediante un abono mensual con clases fijas.

## 1.3 Alcance

**Comprende:** alta del negocio con su cuenta única y sus parámetros; tarifario con precio, duración por tramos y
cupo máximo; trabajadores con horario y pausas; grilla semanal recurrente y generación de las clases de cada
semana; abonos mensuales con clases fijas; reserva autogestionada con elección de trabajador; reprogramación
de turnos individuales; avisos de asistencia; suspensión de clases con aviso redactado; registro de atención,
cobro y ausencia; atenciones sin turno; vista de la jornada; gastos por tipo; existencias de insumos con alerta
de mínimo; avisos automáticos; y reportes del negocio.

**No comprende:**

| Exclusión | Justificación |
|---|---|
| Comercios de venta de productos | Sin turno, trabajador asignado ni duración: no comparten ninguna regla relevada (D-04) |
| Trabajadores como usuarios del sistema | Solo las dueñas registran (`R14`); agregaría una regla de autorización a cada requerimiento (D-06) |
| Cuenta de cliente e historial autogestionado | Consecuencia de D-02 |
| Cálculo automático de consumo de insumos | `R9`: el consumo no se registra y varía entre atenciones (D-03) |
| Comprobantes y facturación fiscal | `R5`: la factura se hace solo si el cliente la pide, por fuera del sistema (D-07) |
| Cobro en línea y seña | No planteado por las usuarias |
| Recuperación o reprogramación de clases del abono | `R32`: si el alumno falta, avise o no, pierde la clase (D-13, Regla 20) |
| Devolución de dinero por faltas o clases suspendidas | `R34`: la clase suspendida no se recupera ni se devuelve el dinero (D-15) |
| Packs de clases y pase libre | Evolución futura, documentada aparte en `evoluciones/pack-de-clases.md` |
| Envío automático del aviso de suspensión | `R35`: el mensaje queda redactado y la dueña lo envía desde su WhatsApp |
| Identificación y asignación de puestos o salas | El sistema controla *cuántos* clientes simultáneos admite el negocio, no *cuál* puesto ocupa cada uno (D-11) |
| Trabajos en múltiples sesiones | Requiere vincular turnos entre sí y administrar pagos parciales |

## 1.4 Glosario

Vocabulario cerrado. Ningún documento del proyecto puede usar otro término para estos conceptos.

| Término | Aplica a | Significado |
|---|---|---|
| `Pendiente` | Cliente en turno | Se anotó pero todavía no confirmó asistencia |
| `Confirmado` | Cliente en turno | Avisó que asiste |
| `Atendido` | Cliente en turno | Se presentó y recibió el servicio o asistió a la clase |
| `Cobrado` | Cliente en turno | Se registró el importe percibido por esa atención o clase suelta |
| `Cancelado` | Cliente en turno | Avisó que no asiste; su lugar se liberó |
| `Ausente` | Cliente en turno | No se presentó y no avisó |
| `Programado` | Turno | Existe en la agenda. Admite anotaciones mientras no alcance su cupo máximo |
| `Suspendido` | Turno | Clase de una semana puntual que no se dicta; no admite anotaciones |
| `Reservado` | Origen del turno | Se creó porque un cliente lo pidió por anticipado |
| `Sin reserva` | Origen del turno | Se creó al atender a quien se presentó sin turno |
| `Fija` / `Suelta` | Modalidad de la anotación | Anotación automática de un alumno con abono en su clase fija · clase paga al llegar |
| `Aplicación` / `Espera` / `Terminación` | Tramo del servicio | Los tres tramos posibles. Solo `Aplicación` y `Terminación` ocupan al trabajador |
| Cupo máximo | Servicio | Cantidad máxima de clientes que admite un turno de ese servicio |
| Tipo de clase | Servicio | Servicio del estudio con precio, duración y cupo máximo, como hatha o vinyasa |
| Clase de la grilla | Grilla | Tipo de clase fijado en un día de la semana, un horario y una profesora, que se repite cada semana |
| Clase de la semana | Turno | Clase de la grilla concretada en una fecha, donde se anotan alumnos, se toma asistencia o se suspende |
| Abono | Alumno | Pago mensual que reserva al alumno un lugar en sus clases fijas todas las semanas |
| Clase fija | Abono | Clase de la grilla elegida por el alumno con abono, en la que queda anotado cada semana |
| `Vigente` / `Vencido` | Abono | Dentro o fuera del mes que cubre desde la fecha de pago |
| Capacidad simultánea | Negocio | Cantidad de clientes que el negocio puede tener en atención al mismo tiempo |

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
trabajó cada trabajador.

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

---

# 3. DECISIONES ADOPTADAS

Resolución de las ambigüedades de `relevamiento.md`, Sección 4, y decisiones que condicionan los requerimientos.

| ID | Decisión | Fundamento |
|---|---|---|
| **D-01** | El cliente elige con qué trabajador se atiende antes de ver horarios; en una clase, la profesora queda determinada por la clase elegida | `R2` ofrece "el horario libre más cercano que tenga esa misma persona"; `R15` lo confirma; `R12` asigna profesora a cada clase |
| **D-02** | La reserva no requiere cuenta: el cliente se identifica por su teléfono | Los datos coinciden con `R3` y `R29`; `R19` advierte que la fricción hace abandonar el flujo |
| **D-03** | El sistema registra existencias declaradas, no calcula consumo | `R9`: el consumo no se registra y varía; `R26`: los elementos se reponen al verlos gastados |
| **D-04** | Alcance multi-rubro acotado a servicios prestados por turno con trabajador asignado | La variabilidad (`relevamiento.md` §5.1) muestra que cambian los valores, no las reglas |
| **D-05** | La grilla es recurrente; el sistema genera las clases de cada semana | `R12`: la grilla se repite todas las semanas; `R34`: las suspensiones son por semana puntual |
| **D-06** | Cuenta única por negocio; trabajadoras y profesoras son datos, no usuarios | `R5` registra "trabajador que atendió" como dato; `R14`: la profesora entrega lista y cobro y registra la dueña |
| **D-07** | El sistema no emite comprobantes fiscales | `R5`: no se entrega comprobante salvo que el cliente pida factura |
| **D-08** | El servicio se define en tramos; la disponibilidad bloquea solo los tramos activos | `R1` y `R20`: durante la espera de la coloración se atiende a otra clienta; `R23` |
| **D-09** | La atención sin turno **es** un turno, creado al atender, con origen `Sin reserva` | `R21`: todo lo posterior es idéntico y hoy quedan cobros sin turno |
| **D-10** | El sistema valida reservas; no impide registros | `R22`: "si yo lo hice, lo hice" |
| **D-11** | Capacidad simultánea del negocio: un número, sin identificar puestos | `R24`: los cuatro puestos son el límite real |
| **D-13** | El abono es mensual y reserva un lugar en las clases fijas; sus clases no se recuperan | `R30`, `R31`, `R32` |
| **D-15** | Suspender una clase no la recupera ni devuelve dinero: el alumno con abono la pierde como una falta y la clase suelta no se cobra | `R34`, `R13` |
| **D-16** | Una clase fija es una anotación automática que se hace al generar la semana; sin abono vigente, no se anota | `R31`, `R30` |
| **D-17** | Reprogramar es mover un turno individual a otro horario libre de la misma trabajadora, a pedido del cliente o de la dueña; las clases grupales no se reprograman | `R36`, `R15`, `R32` |

**Decisiones retiradas.** **D-12**: fechar la atención independientemente del momento de carga no tiene respaldo
en el relevamiento unificado. **D-14**: la anticipación mínima y el ajuste manual de cupos pertenecían a los packs
de clases, que pasaron a evolución futura.

**Nota sobre D-08.** Sin tramos, la reserva online bloquearía el tiempo de espera completo y el sistema quedaría
por detrás de la libreta, donde hoy ese hueco se usa (`R20`).

---

# 4. REGLAS DE NEGOCIO

1. **Disponibilidad por trabajador.** El horario libre se calcula sobre el horario de atención del trabajador,
   descontando sus pausas no laborables, los **tramos activos** de sus turnos ya tomados y la duración del
   servicio. Dos turnos pueden coincidir si corresponden a trabajadores distintos. *(`R2`, `R28`, D-08)*
2. **Cupo máximo por servicio.** No se puede anotar en un turno más clientes que su cupo máximo.
   *(`R11`, `R13`)*
3. **El estado corresponde al cliente, no al turno.** En un turno de cupo máximo mayor a 1, cada cliente tiene su
   propia confirmación, asistencia o ausencia. El importe se registra por atención, por clase suelta o por abono.
   *(`R5`, `R14`, `R30`)*
4. **Momento de creación del turno.** Cupo máximo 1: se crea al pedir el turno. Cupo máximo mayor a 1: se genera
   desde la grilla y los clientes se anotan sobre uno existente. Sin reserva: se crea al atender.
   *(`R2`, `R12`, `R21`)*
5. **Liberación de lugar.** Al cancelar, el lugar vuelve a estar disponible dentro del mismo turno.
   *(`R4`, `R32`)*
6. **Un servicio pertenece a un negocio.** Precio, duración, tramos y cupo máximo se definen a nivel del negocio.
   *(`R1`, `R11`)*
7. **El cliente se identifica por su teléfono.** Dos anotaciones con el mismo teléfono son el mismo cliente.
   *(`R3`, `R29`, D-02)*
8. **La existencia de insumos es un valor declarado.** El sistema nunca la modifica por sí mismo.
   *(`R9`, `R26`, D-03)*
9. **Suspender una clase no altera la grilla.** Afecta solo a esa semana. *(`R12`, `R34`, D-05)*
10. **El tramo de espera no ocupa al trabajador.** El cliente sigue ocupado hasta el último tramo.
    *(`R1`, `R20`, `R23`, D-08)*
11. **La validación de superposición y de cupo máximo solo se aplica al reservar.** Al registrar, el sistema
    informa el conflicto pero no lo impide. *(`R22`, D-10)*
12. **Toda atención genera un turno.** No existe cobro de atención sin un turno que lo explique.
    *(`R21`, D-09)*
13. **Capacidad simultánea.** Al reservar no se ofrece un horario si ya hay tantos clientes en atención como la
    capacidad configurada, incluidos los que están en tramo de espera. *(`R24`, D-11)*
14. **Ante falta de lugar, el sistema no decide.** Se ofrece volver más tarde (sin registro) o reservar un turno.
    *(`R21`)*
15. **La atención sin turno se habilita por negocio.** Un negocio que publica su oferta en una grilla puede no
    admitirla. *(`R13`, `R21`)*
16. **Abono mensual.** El abono cubre un mes desde la fecha de pago y reserva el lugar del alumno en sus clases
    fijas todas las semanas. *(`R30`, `R31`, D-13)*
17. **Las clases del abono no se recuperan.** Si el alumno falta, haya avisado o no, o si la clase se suspende,
    pierde esa clase y no se le devuelve dinero. *(`R32`, `R34`, D-13, D-15)*
18. **Avisar libera el lugar.** Un aviso de falta libera el lugar esa semana para otra persona; faltar sin avisar se
    registra como ausencia. *(`R32`)*
19. **Vencimiento del abono.** Si el alumno no renueva al vencimiento, sale de las listas fijas y su lugar queda
    disponible. *(`R31`)*
20. **Solo se reprograman turnos individuales.** Un turno de cupo máximo 1 puede pasarse a otro horario libre de la
    misma trabajadora; las clases grupales no se reprograman. *(`R36`, `R32`, D-17)*

---

# 5. REQUERIMIENTOS FUNCIONALES

**Prioridad:** E = Esencial · C = Condicional · O = Opcional · D = Deseable

Los requerimientos se agrupan por su momento en el ciclo de negocio: **configuración** (RF-01 a RF-04),
**operación** (RF-05 a RF-14), **consulta** (RF-15 y RF-16) y **avisos programados** (RF-17 a RF-19). La
numeración no indica orden de ejecución.

---

> ### RF-01: DAR DE ALTA Y CONFIGURAR EL NEGOCIO — `E`
>
> **Descripción:** Registrar o modificar el negocio, su única cuenta de acceso, el canal por el que recibe sus
> avisos y los parámetros de operación que consume el resto de los requerimientos.
>
> **Inputs:** Nombre del negocio · Usuario · Contraseña · Teléfono de contacto del negocio · Tipos de gasto que
> usa el negocio · Capacidad simultánea (sin valor por defecto: depende del local) · Admite atención sin turno,
> sí o no (sí por defecto) · Anticipación del recordatorio de turno (24 horas por defecto) · Hora de emisión del
> resumen de agenda (21:00 por defecto) · Periodicidad del aviso de revisión de existencias (semanal por
> defecto).
>
> **Proceso:**
> 1. Recibir los datos del negocio.
> 2. Registrar la cuenta única de acceso al panel de gestión. *(Restricción 6)*
> 3. Registrar el teléfono al que se dirigen el resumen de agenda y los avisos de existencias.
> 4. Validar la capacidad simultánea contra el producto del cupo máximo del tarifario por la cantidad de
>    trabajadores. *(Restricción 9)*
> 5. Registrar los parámetros asociados al negocio.
>
> **Outputs:** Negocio registrado con su cuenta de acceso, su canal de contacto y sus parámetros.
>
> **Error Handling:** Si la capacidad simultánea informada es menor que el producto del cupo máximo del tarifario
> por la cantidad de trabajadores, no se registra ese valor. *(Restricción 9)*
>
> **Trazabilidad:** `R7`, `R12`, `R16`, `R17`, `R21`, `R24`, `R27`, D-06, D-11, RNF Seguridad, RNF
> Mantenibilidad.
>
> **Nota:** la cuenta de acceso no proviene de un hecho relevado —los casos operan en papel— sino de D-06 y del
> RNF de Seguridad. Sin este requerimiento la Restricción 6 no tendría quién la produzca y RF-18 y RF-19 no
> tendrían a dónde enviar sus avisos.

---

> ### RF-02: DEFINIR SERVICIO DEL TARIFARIO — `E`
>
> **Descripción:** Registrar o modificar un servicio o tipo de clase, con su precio, sus tramos de duración y su
> cupo máximo.
>
> **Inputs:** Nombre · Precio · Duración del tramo de aplicación · Duración del tramo de espera (cero si no tiene)
> · Duración del tramo de terminación (cero si no tiene) · Cupo máximo.
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
> **Trazabilidad:** `R12`, D-05.

---

> ### RF-05: REGISTRAR ABONO — `C`
>
> **Descripción:** Registrar o renovar el abono mensual de un alumno, con sus clases fijas y su vencimiento.
>
> **Inputs:** Teléfono del alumno · Nombre, apellido y observaciones de salud (si es su primera inscripción) ·
> Frecuencia semanal · Clases fijas elegidas · Fecha de pago · Monto.
>
> **Proceso:**
> 1. Identificar al alumno por su teléfono; si no existe, registrarlo con sus datos y observaciones de salud.
>    *(Regla 7)*
> 2. Registrar las clases fijas elegidas entre las clases de la grilla que tienen lugar. *(D-16)*
> 3. Calcular la fecha de vencimiento a un mes de la fecha de pago. *(Regla 16)*
> 4. Registrar el cobro del abono.
> 5. Registrar el abono en estado `Vigente`, con alumno, frecuencia, clases fijas, fecha de pago, monto y fecha de
>    vencimiento.
>
> **Outputs:** Abono registrado en la planilla de abonos · Alumno incorporado a las listas fijas · Cobro del
> abono registrado.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R29`, `R30`, `R31`, D-02, D-13, D-16.

---

> ### RF-06: GENERAR LAS CLASES DE LA SEMANA — `C`
>
> **Descripción:** Crear las clases de la semana a partir de la grilla y anotar en ellas a los alumnos con clases
> fijas.
>
> **Inputs:** Grilla semanal del negocio · Fecha de la semana a generar · Clases fijas de los alumnos · Abonos.
>
> **Proceso:**
> 1. Recorrer las clases de la grilla.
> 2. Crear un turno en estado `Programado` por cada una, con su fecha, su profesora y su cupo máximo.
> 3. Anotar en cada turno, con modalidad `Fija`, a los alumnos que tienen esa clase fija con un abono `Vigente`.
>    *(Regla 16)*
>
> **Outputs:** Lista de cada clase de la semana, con fecha y alumnos fijos anotados.
>
> **Error Handling:** Si el abono del alumno está `Vencido` porque no lo renovó, no se lo anota y su lugar queda
> disponible para otra persona. *(`R31`, Regla 19)*
>
> **Trazabilidad:** `R12`, `R30`, `R31`, D-05, D-16.
>
> **Nota:** la generación de clases concretas no es un proceso relevado —hoy la grilla y las listas se llevan en
> papel— sino una consecuencia de D-05: las anotaciones, avisos, suspensiones y asistencias necesitan una fecha
> concreta a la que asociarse.

---

> ### RF-07: RESERVAR TURNO — `E`
>
> **Descripción:** Permitir que el cliente elija un servicio o una clase suelta, consulte la disponibilidad real y
> se anote, informando sus datos de contacto.
>
> **Inputs:** Servicio o tipo de clase · Trabajadora (si el servicio es de cupo máximo 1) · Fecha y hora elegidas
> · Teléfono · Nombre · Apellido.
>
> **Proceso:**
> 1. Recibir el servicio y, si corresponde, la trabajadora elegida.
> 2. Determinar la oferta según el cupo máximo del servicio. *(Regla 4)*
>    - **Cupo máximo 1** — calcular los horarios libres de la trabajadora: horario de atención, menos pausas no
>      laborables, menos los tramos activos de sus turnos, sin contar los clientes `Cancelado`, verificando que
>      entre la duración total del servicio. *(Reglas 1, 5 y 10)*
>    - **Cupo máximo mayor a 1** — recuperar las clases de la semana en estado `Programado` de ese tipo cuya
>      cantidad de anotados, contando los fijos y sin contar los `Cancelado`, sea menor que el cupo máximo.
>      *(Reglas 2, 5 y 18)*
> 3. Descartar los horarios en los que la cantidad de clientes en atención alcanza la capacidad simultánea.
>    *(Regla 13)*
> 4. Mostrar los horarios disponibles.
> 5. Recibir el horario elegido y los datos de contacto.
> 6. Identificar al cliente por su teléfono; si no existe, registrarlo. *(Regla 7)*
> 7. Si el servicio es de cupo máximo 1, crear el turno en estado `Programado` con origen `Reservado`. Si es de
>    cupo máximo mayor a 1, incorporar al cliente a la clase elegida con modalidad `Suelta`. *(Regla 4)*
> 8. Dejar al cliente en estado `Pendiente`.
>
> **Outputs:** Anotación registrada en estado `Pendiente` · Confirmación de día, hora y trabajadora al cliente.
>
> **Error Handling:**
> - Si la trabajadora elegida no tiene disponibilidad en el día pedido, se ofrece su horario libre más cercano,
>   **de esa misma persona**. *(`R2`, D-01)*
> - Si la clase está completa, se ofrece otro día u horario. *(`R13`, Regla 2)*
>
> **Trazabilidad:** `R2`, `R3`, `R12`, `R13`, `R15`, `R23`, `R24`, `R29`, `R32`, D-01, D-02, D-08, D-11, RF-01.

---

> ### RF-08: REGISTRAR AVISO DE ASISTENCIA — `E`
>
> **Descripción:** Registrar el aviso del cliente sobre si asiste o no a su turno o clase, liberando el lugar
> cuando no asiste.
>
> **Inputs:** Cliente · Turno o clase de la semana · Aviso de asistencia o de falta.
>
> **Proceso:**
> 1. Recibir el aviso del cliente, sea en respuesta al recordatorio o por iniciativa propia.
> 2. Si confirma que asiste, cambiar su estado a `Confirmado`.
>
> **Outputs:** Cliente en estado `Confirmado`.
>
> **Error Handling:** Si avisa que no puede asistir, su estado pasa a `Cancelado` y el lugar vuelve a estar
> disponible esa semana para otra persona. Si es un alumno con abono, pierde esa clase: no se recupera ni se
> devuelve dinero. *(`R4`, `R32`, Reglas 5, 17 y 18)* Si en lugar de cancelar un turno individual el cliente quiere
> pasarlo a otro horario, se reprograma mediante RF-09. *(`R36`)*
>
> **Trazabilidad:** `R4`, `R15`, `R32`, D-13.

---

> ### RF-09: REPROGRAMAR TURNO — `E`
>
> **Descripción:** Pasar un turno individual a otro horario libre de la misma trabajadora, a pedido del cliente o
> de la dueña.
>
> **Inputs:** Turno a reprogramar · Nueva fecha y hora elegida.
>
> **Proceso:**
> 1. Recibir el turno a reprogramar, de cupo máximo 1. *(Regla 20)*
> 2. Calcular los horarios libres de la misma trabajadora con el mismo criterio de la reserva: horario de
>    atención, menos pausas no laborables, menos los tramos activos de sus otros turnos, sin contar los clientes
>    `Cancelado`, verificando que entre la duración total del servicio y que no se alcance la capacidad
>    simultánea. *(Reglas 1, 5, 10 y 13)*
> 3. Mostrar los horarios disponibles.
> 4. Recibir el horario elegido.
> 5. Cambiar la fecha y la hora del turno, liberando el horario anterior. *(Regla 5)*
> 6. Dejar al cliente en estado `Confirmado`, porque el nuevo horario lo eligió él.
>
> **Outputs:** Turno con su nueva fecha y hora · Confirmación del nuevo día y hora al cliente.
>
> **Error Handling:** Si no hay un horario de la misma trabajadora que le sirva al cliente, el turno se cancela y
> el cliente queda en estado `Cancelado`. *(`R36`)*
>
> **Trazabilidad:** `R2`, `R4`, `R15`, `R36`, D-01, D-17, RF-07.

---

> ### RF-10: SUSPENDER CLASE — `C`
>
> **Descripción:** Suspender la clase de una semana puntual y dejar preparado el aviso para cada alumno anotado.
>
> **Inputs:** Clase de la semana a suspender · Motivo.
>
> **Proceso:**
> 1. Recibir la clase a suspender.
> 2. Cambiar su estado a `Suspendido`, sin modificar la grilla. *(Regla 9)*
> 3. Recuperar los alumnos anotados en esa clase.
> 4. No generar ninguna recuperación ni devolución de dinero: los alumnos con modalidad `Fija` pierden la clase y
>    los de modalidad `Suelta` no la pagan, porque abonan al llegar. *(Regla 17, D-15)*
> 5. Redactar, para cada alumno anotado, el mensaje de aviso con su teléfono, listo para que la dueña lo envíe
>    desde su WhatsApp.
>
> **Outputs:** Clase suspendida · Detalle de los alumnos que estaban anotados · Mensajes de aviso redactados.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R12`, `R13`, `R34`, `R35`, D-05, D-15.

---

> ### RF-11: REGISTRAR ATENCIÓN Y COBRO — `E`
>
> **Descripción:** Registrar que un cliente fue atendido o asistió a una clase y, si no tiene abono, el importe
> percibido.
>
> **Inputs:** Cliente · Servicio o clase · Trabajadora o profesora · Fecha y hora · Importe cobrado (si no tiene
> abono) · Observaciones para la próxima atención (opcional).
>
> **Proceso:**
> 1. Recibir los datos de la atención.
> 2. Registrar al cliente como `Atendido`. Si no tiene abono, registrar el importe y marcarlo como `Cobrado`. Las
>    dos marcas son independientes y pueden registrarse en cualquier orden: quien toma una clase suelta paga al
>    llegar y la asistencia se toma durante la clase. *(`R14`)*
> 3. Registrar el cobro con fecha, hora, monto, servicio y trabajadora, o con fecha, alumno y monto si es una
>    clase. *(`R5`, `R14`)*
> 4. Si se informaron observaciones, agregarlas a la ficha del cliente, como el tono de tintura aplicado o las
>    alergias declaradas. *(`R3`)*
>
> **Outputs:** Cliente en estado `Atendido` · Cobro registrado cuando corresponde · Ficha del cliente con sus
> observaciones actualizadas, cuando se informaron. **Sin comprobante hacia el cliente.** *(D-07)*
>
> **Error Handling:**
> - Si el cliente se presentó **sin turno** y el servicio es de **cupo máximo 1**, se crea el turno en el mismo
>   acto en estado `Programado` y con origen `Sin reserva`, y se continúa desde el paso 2. *(`R21`, D-09, Regla
>   12)*
> - Si el cliente se presentó **sin turno** y el servicio es de **cupo máximo mayor a 1**, no se crea ningún
>   turno: se lo incorpora con modalidad `Suelta` a la clase de ese horario, respetando su cupo máximo.
>   *(Reglas 2 y 4, D-09)*
> - Si la atención se superpone con otro turno de la misma trabajadora, el sistema **informa el conflicto pero
>   registra igual**. *(`R22`, D-10, Regla 11)*
> - Si no hay lugar para atender a quien se presentó sin turno, se le ofrece volver más tarde —sin registro— o
>   reservar un turno mediante RF-07. *(`R21`, Regla 14)*
> - Si el negocio tiene desactivada la atención sin turno (RF-01), no se ofrece el alta de la atención sin
>   reserva. *(`R13`, Regla 15)*
>
> **Trazabilidad:** `R3`, `R5`, `R13`, `R14`, `R21`, `R22`, `R24`, D-06, D-07, D-09, D-10.

---

> ### RF-12: REGISTRAR AUSENCIA — `E`
>
> **Descripción:** Registrar que un cliente no se presentó a su turno o clase y tampoco avisó.
>
> **Inputs:** Cliente · Turno o clase de la semana sin presentación ni aviso.
>
> **Proceso:**
> 1. Recibir la indicación de ausencia.
> 2. Registrar al cliente como `Ausente`.
>
> **Outputs:** Cliente en estado `Ausente`, disponible para el recuento de ausencias.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R6`, `R14`, `R32`, Regla 18.

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
> **Descripción:** Obtener el resultado del período y los indicadores del negocio, con comparación entre períodos.
>
> **Inputs:** Período a consultar.
>
> **Proceso:**
> 1. Recibir el período.
> 2. Sumar los cobros de atenciones, clases sueltas y abonos, y restar los gastos. *(`R8`, `R27`)*
> 3. Calcular los indicadores: facturación por servicio, gastos por tipo, clientes recurrentes, clientes que
>    dejaron de venir, recuento de ausencias y producción por trabajadora. *(`R6`, `R10`)*
>
> **Outputs:** Resultado del período · Indicadores del negocio.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R6`, `R7`, `R8`, `R10`, `R18`, `R27`.

---

> ### RF-17: ENVIAR RECORDATORIO DE TURNO — `E`
>
> **Descripción:** Avisar al cliente de su turno o clase con la anticipación configurada.
>
> **Inputs:** Turnos y clases próximos · Anticipación configurada (24 horas por defecto).
>
> **Proceso:**
> 1. Al cumplirse la anticipación configurada, recuperar los turnos y clases alcanzados.
> 2. Emitir el aviso al teléfono de cada cliente `Pendiente`.
>
> **Outputs:** Recordatorio enviado al cliente.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R4`, `R16`, RF-01.

---

> ### RF-18: ENVIAR RESUMEN DE AGENDA DIARIA — `E`
>
> **Descripción:** Enviar a la dueña el detalle de la agenda del día siguiente.
>
> **Inputs:** Turnos y clases del día siguiente · Hora de emisión configurada (21:00 por defecto) · Teléfono de
> contacto del negocio.
>
> **Proceso:**
> 1. Al llegar la hora configurada, recuperar los turnos y clases del día siguiente.
> 2. Emitir el resumen al teléfono de contacto del negocio.
>
> **Outputs:** Resumen de agenda enviado a la dueña.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R4`, `R16`, RF-01.

---

> ### RF-19: ENVIAR AVISO DE REVISIÓN DE EXISTENCIAS — `E`
>
> **Descripción:** Recordar periódicamente a la dueña que actualice la existencia de sus insumos.
>
> **Inputs:** Insumos registrados del negocio · Periodicidad configurada (semanal por defecto) · Teléfono de
> contacto del negocio.
>
> **Proceso:**
> 1. Al cumplirse la periodicidad, recuperar los insumos del negocio.
> 2. Emitir el aviso de revisión al teléfono de contacto del negocio.
>
> **Outputs:** Aviso de revisión enviado a la dueña.
>
> **Error Handling:** N/A.
>
> **Trazabilidad:** `R17`, D-03, RF-01.
>
> **Nota:** es consecuencia directa de D-03. Sin descuento automático, la existencia registrada se desactualiza,
> la alerta de RF-14 nunca se dispara y el control de insumos pierde su valor.

---

# 6. REQUERIMIENTOS NO FUNCIONALES

| Categoría | Requerimiento | Trazabilidad |
|---|---|---|
| **Desempeño** | La vista de reserva debe mostrar los horarios disponibles en menos de 3 segundos sobre red móvil 4G, sosteniendo ~3.200 atenciones y ~340 clientes por año por negocio. | `R15`, `R19`, supuestos de `relevamiento.md` §5.2 |
| **Disponibilidad** | La vista de reserva debe estar operativa las 24 horas, porque su razón de ser es recibir reservas fuera del horario de atención. El panel de gestión requiere disponibilidad durante el horario de atención. | `R15` |
| **Confiabilidad** | Un turno, una anotación o un abono no pueden perderse ni duplicarse. Debe conservarse respaldo diario: la libreta, los cuadernos y las planillas son hoy el único registro, y el sistema los reemplaza. | `R2`, `R5`, `R27`, `R30` |
| **Seguridad** | El panel de gestión se accede con usuario y contraseña por negocio. La vista de reserva es pública y **no debe exponer datos de otros clientes**, incluidas las observaciones de salud de los alumnos: solo muestra horarios o lugares libres u ocupados. | D-02, D-06, `R29` |
| **Portabilidad** | Aplicación web responsiva, operable desde el navegador de un celular sin instalar nada. | `R19` |
| **Accesibilidad** | La vista de reserva debe cumplir **WCAG 2.1 nivel AA**: contraste mínimo de 4,5:1 en texto normal y 3:1 en texto grande, texto ampliable hasta el 200% sin pérdida de contenido, navegación completa por teclado y compatibilidad con lectores de pantalla. | `R19` |
| **Mantenibilidad** | El tarifario, los horarios, las pausas, la grilla, la capacidad simultánea, los tipos de gasto, los mínimos de stock y los tiempos de aviso deben ser configurables por la propia dueña, sin intervención técnica. | `R1`, `R12`, `R16`, `R17`, `R28` |

---

# 7. RESTRICCIONES

1. La duración total de un servicio es la suma de sus tramos de aplicación, espera y terminación. *(D-08)*
2. Solo los tramos de aplicación y terminación ocupan al trabajador. *(D-08)*
3. El cupo máximo de un servicio determina cuántos clientes admite un turno; el turno individual es el caso de
   cupo máximo 1. *(Regla 2, `R11`)*
4. El cliente se identifica por su teléfono. No existen credenciales de cliente. *(D-02)*
5. La existencia de un insumo solo se modifica por declaración de la dueña. *(D-03)*
6. El negocio tiene una única cuenta de acceso. Trabajadoras y profesoras son datos, no usuarios. *(D-06)*
7. La validación de disponibilidad, cupo máximo y capacidad simultánea rige únicamente en el flujo de reserva.
   *(D-10)*
8. El sistema no emite comprobantes fiscales. *(D-07)*
9. La capacidad simultánea no puede ser menor que el producto del cupo máximo del tarifario por la cantidad de
   trabajadores. *(Reglas 2 y 13, `R12`, `R24`, D-11)*
10. El abono cubre un mes desde la fecha de pago, y sus clases perdidas por falta o suspensión no se recuperan ni
    se devuelve el dinero. *(`R30`, `R32`, `R34`, D-13, D-15)*

---

# 8. CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Relevamiento narrativo | `Relevamiento WorkUp.md` | Fuente |
| Análisis del relevamiento | `relevamiento.md` | Emitido |
| Especificación de Requerimientos (IEEE 830) | `srs.md` | Este documento |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
| Evoluciones futuras | `evoluciones/` | Fuera de alcance |
