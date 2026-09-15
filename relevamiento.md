# WorkUp — Análisis del Relevamiento

**Versión:** 5.2
**Fecha:** 14-09-2026
**Equipo:** Bapton Solutions
**Documento fuente:** `Relevamiento WorkUp.md`

**Objeto de este documento.** Analiza el relevamiento narrativo del proyecto. No repite la narrativa: la
descompone en hechos identificados, aplica QQCCD, señala ambigüedades y extrae lo que varía entre rubros. Los
requerimientos que surgen de este análisis se especifican en `srs.md`.

**Convención de trazabilidad.** Cada hecho relevado lleva un identificador `{R#}`. Los identificadores no se
reutilizan ni se renumeran entre versiones, por eso aparecen salteados: `R15` a `R19`, `R22`, `R23` y `R35`
corresponden a necesidades manifestadas por las usuarias y se especifican en `srs.md`, Sección 2. `R33` está
retirado.

> **Versión 5.2.** Se incorpora `R36`: la reprogramación de turnos en la peluquería.
>
> **Cambios de la versión 5.1.** El estudio vuelve al abono mensual con clases fijas y sin recuperación:
> las clases perdidas por falta o por suspensión no se recuperan ni se devuelve el dinero. Se retira `R33`, que
> el relevamiento actual ya no describe.

---

# SECCIÓN 1 — RECONOCIMIENTO

## 1.1 Información de la organización

| Campo | Caso A | Caso B |
|---|---|---|
| Nombre | Peluquería de Marina Gómez | Estudio de yoga de Lucía Fernández |
| Actividad principal | Servicios de corte, coloración y peinado | Clases grupales de yoga |
| Estructura | Marina Gómez (dueña, atiende) · Sofía Paz (empleada, atiende) | Lucía Fernández (dueña, dicta) · Julieta Ríos (profesora, dicta) |
| Recursos físicos | Local con cuatro puestos de trabajo | Estudio con dos salas |
| Contacto clave | Marina Gómez, dueña | Lucía Fernández, dueña |

## 1.2 Requerimiento del cliente

Bapton Solutions fue contratada para desarrollar un sistema web que permita a emprendedores de servicios
administrar su actividad diaria. Se tomaron dos casos porque cubren las dos formas de atención del rubro:
**atención individual** (Caso A) y **clases grupales con cupo limitado** (Caso B).

## 1.3 Visión del consultor

1. **La información está dispersa en soportes que no se vinculan entre sí.** En la peluquería conviven la
   libreta de turnos, el cuaderno de clientes y el cuaderno de movimientos (`R2`, `R3`, `R5`); en el estudio, la
   planilla de profesoras, el registro de alumnos, la planilla de abonos, las listas de cada clase y el registro
   de movimientos (`R25`, `R29`, `R30`, `R14`, `R27`).
2. **Los registros no cierran entre sí.** Hay cobros en el cuaderno que ningún turno explica (`R21`), y los
   turnos intercalados en un tiempo de espera se anotan encimados (`R20`).
3. **No hay información consolidada para decidir.** El resultado del período se calcula a mano en ambos casos
   (`R8`, `R27`) y no se puede saber qué servicio deja más margen ni qué clientes dejaron de venir (`R10`).

## 1.4 Observaciones

- **La operación de ambos casos es inversa.** En la peluquería el cliente pide y recién ahí se crea el turno. En
  el estudio la oferta se publica primero en la grilla y el alumno se anota sobre algo que ya existe.
- **En el estudio, faltar y que se suspenda la clase tienen el mismo efecto para el alumno.** En ninguno de los
  dos casos la clase se recupera ni se devuelve el dinero (`R32`, `R34`). Avisar solo sirve para liberar el lugar.
- **Solo las dueñas registran.** La empleada y la profesora atienden o dictan, pero los registros los lleva la
  dueña: Julieta le entrega a Lucía la lista y lo cobrado (`R14`).

---

# SECCIÓN 2 — HECHOS RELEVADOS

Cada fila resume un bloque del relevamiento narrativo. El texto completo está en `Relevamiento WorkUp.md`.

## 2.1 Caso A — Peluquería

| ID | Hecho | Qué se relevó |
|---|---|---|
| `{R1}` | Servicios y precios | La lista de precios está pegada en el espejo y se actualiza a mano con cada aumento. El corte y el brushing no tienen tiempo de espera; la coloración sí. |
| `{R28}` | Datos de las trabajadoras | En una carpeta se guardan nombre, apellido, teléfono y CUIL de cada trabajadora, con su horario de atención: días de trabajo, hora de inicio y fin, y pausas no laborables. Se actualiza cuando cambia un horario. |
| `{R2}` | Toma de turno | El cliente escribe por WhatsApp con el servicio, el día y, en la mayoría de los casos, la trabajadora. Se busca en la columna de esa trabajadora un espacio que alcance para la duración del servicio; si no hay, se ofrece el horario libre más cercano de esa misma persona. Cada turno incluye fecha, hora de inicio, servicio, trabajadora, cliente y estado. Dos turnos coinciden en horario si corresponden a trabajadoras distintas. |
| `{R20}` | Turnos intercalados en el tiempo de espera | Durante el tiempo de espera de una coloración, Marina atiende a otra clienta y anota ese segundo turno al costado o encimado sobre el primero. |
| `{R3}` | Cuaderno de clientes | Si el cliente es nuevo, se anotan nombre, apellido y teléfono mientras se toma el turno; si ya vino, se busca su ficha y se le agrega el turno. Los clientes se identifican por teléfono, apellido, nombre y observaciones para la próxima atención. |
| `{R4}` | Confirmación previa | Cada mañana se repasa la libreta. El día anterior a cada turno se envía un mensaje para confirmar; si confirma se marca con una tilde, si avisa que no puede se tacha y el horario queda libre. |
| `{R36}` | Reprogramación de turnos | Si el cliente no puede ir pero no quiere perder el turno, se busca otro espacio libre de la misma trabajadora que alcance para el servicio, se le ofrecen las opciones y, cuando elige, se tacha el turno anterior y se anota el nuevo. Lo mismo se hace cuando la trabajadora no puede atender. Si no aparece un horario que le sirva al cliente, el turno se cancela. |
| `{R5}` | Cobro | Al terminar el turno se cobra y el monto se anota en el cuaderno de movimientos, con fecha, hora, monto, servicio y trabajador que atendió. No se entrega comprobante salvo que el cliente pida factura. |
| `{R6}` | Ausencias | Si el cliente no se presenta ni avisa, el turno se anota como ausente. Al final del mes se hace un recuento de turnos perdidos por ausencias. |
| `{R21}` | Atención sin turno | Llegan clientes sin turno. Si hay un puesto libre se los atiende y se anota el cobro; si no, se les ofrece volver más tarde o sacar turno. Estas atenciones no van a la libreta, por lo que quedan cobros sin turno. |
| `{R24}` | Capacidad del local | El local tiene cuatro puestos de trabajo. Ante un cliente sin turno, Marina decide mirando cuántos están ocupados. |
| `{R7}` | Gastos | En el cuaderno de movimientos se registran los gastos con fecha, monto, tipo y descripción. Los tipos son seis: insumos, alquiler, servicios, transporte, impuestos y tasas, y varios. |
| `{R8}` | Cierre mensual | A fin de mes se suman con calculadora los cobros, se restan los gastos y se obtiene el reporte del período. |
| `{R9}` | Insumos | Se usan tinturas, shampoo, oxidantes y guantes. No se registra cuánto hay ni cuánto se usa, y el consumo varía entre atenciones. La falta se detecta al abrir el cajón. |
| `{R10}` | Falta de información del negocio | No se puede establecer qué servicio deja más margen, qué clientas vuelven con frecuencia y cuáles dejaron de venir, ni cuánto trabajó cada una. |

## 2.2 Caso B — Estudio de yoga

| ID | Hecho | Qué se relevó |
|---|---|---|
| `{R11}` | Tipos de clase | Hay distintos tipos de clase. Para cada uno se fijan precio, duración y cupo máximo de alumnos, que depende del espacio y de la práctica. |
| `{R25}` | Planilla de profesoras | Se registran nombre, apellido, teléfono, CUIL y disponibilidad horaria de cada profesora dentro del horario del estudio. Se actualiza cuando algo cambia y se usa para armar la grilla. |
| `{R12}` | Grilla semanal | Se arma de antemano una grilla que se repite todas las semanas y queda publicada. Cada clase tiene tipo, día de la semana, horario y profesora. Se revisa cuando cambia la temporada o la disponibilidad. Con dos salas, dos profesoras pueden dictar en el mismo horario. |
| `{R29}` | Registro de alumnos | Al inscribirse por primera vez, con abono o para una clase suelta, se registran nombre, apellido, teléfono y observaciones de salud relevantes. |
| `{R30}` | Abono mensual | El alumno elige cuántas veces por semana asiste y cuáles van a ser sus clases fijas, entre las que tienen lugar. El abono tiene precio fijo, se paga al inscribirse y cubre un mes desde esa fecha. La planilla de abonos registra alumno, frecuencia, clases fijas, fecha de pago, monto y fecha de vencimiento. |
| `{R31}` | Listas fijas | El alumno queda incorporado a la lista fija de sus clases y tiene el lugar reservado todas las semanas. Si no renueva al vencimiento, sale de las listas fijas y su lugar queda disponible. |
| `{R32}` | Faltas | Las clases del abono no se recuperan: si el alumno falta, haya avisado o no, pierde esa clase. Avisar con anticipación libera el lugar esa semana para otra persona. Si falta sin avisar, se lo registra como ausente. |
| `{R34}` | Suspensión de una clase | Si una clase no se puede dictar una semana puntual, se suspende solo esa clase sin tocar la grilla y se avisa a cada anotado por WhatsApp. No se recupera ni se devuelve el dinero: el alumno con abono la pierde como si hubiera faltado, y quien iba a una clase suelta no la paga porque abona al llegar. |
| `{R13}` | Clase suelta | Quien no tiene abono escribe por WhatsApp. Se revisa si hay lugar esa semana contando los lugares fijos y los avisos de falta; si hay, se lo anota para esa fecha; si está completa, se le ofrece otro día u horario. Paga al llegar. |
| `{R14}` | Asistencia y cobro de clases | Cada profesora toma asistencia sobre la lista de la semana: fecha, alumnos anotados (fijos y sueltos) y si asistió o faltó. Cobra a quienes asisten sin abono. Julieta entrega a Lucía la lista y lo cobrado, y Lucía anota cada cobro con fecha, alumno y monto. |
| `{R26}` | Elementos del estudio | Mats, bandas elásticas, cintas y toallas se reutilizan. No se registra su estado; se reponen cuando se los ve gastados. |
| `{R27}` | Gastos y cierre del estudio | Cada gasto se anota con fecha, monto y tipo: alquiler, servicios, reposición de elementos e impuestos. A fin de mes se suma lo cobrado por abonos y clases sueltas, se restan los gastos y se obtiene el resultado del período. |

---

# SECCIÓN 3 — CUADRO QQCCD

| Proceso | Qué recibe / de quién | Qué elabora / a quién | Quién | Cuándo | Dónde |
|---|---|---|---|---|---|
| `R1` Actualizar precios | Aumento de precio | Lista de precios en el espejo | Marina | Con cada aumento | Local |
| `R28` Registrar trabajadora | Datos y horario de la trabajadora | Ficha en la carpeta de trabajadoras | Marina | Al incorporarla o cambiar un horario | Local |
| `R2` Tomar turno | Servicio, día y trabajadora, del cliente por WhatsApp | Turno en la columna de la libreta; confirmación de día y hora al cliente | Marina | Al recibir el mensaje | Local |
| `R20` Intercalar turno | Turno de coloración en espera | Segundo turno anotado al costado o encimado | Marina | Durante el tiempo de espera | Local |
| `R3` Registrar cliente | Nombre, apellido y teléfono del cliente | Ficha en el cuaderno de clientes | Marina | Mientras toma el turno | Local |
| `R4` Confirmar turnos | Libreta de turnos | Mensaje de confirmación; tilde o tachado | Marina | Cada mañana y el día anterior a cada turno | Local |
| `R36` Reprogramar turno | Aviso del cliente, o imposibilidad de la trabajadora | Turno anterior tachado y turno nuevo anotado; nuevo horario al cliente | Marina | Al recibir el aviso, o cuando la trabajadora no puede atender | Local |
| `R5` Cobrar | Cliente atendido | Cobro en el cuaderno de movimientos | Marina | Al terminar el turno | Local |
| `R6` Registrar ausencia y recuento | Turno sin presentación ni aviso | Turno marcado ausente; recuento mensual | Marina | Pasado el turno; a fin de mes | Local |
| `R21` Atender sin turno | Cliente que se presenta sin turno | Cobro en el cuaderno, sin turno en la libreta | Marina | Al presentarse el cliente | Local |
| `R24` Decidir si hay lugar | Puestos ocupados | Decisión de atender u ofrecer otro horario | Marina | Al llegar alguien sin turno | Local |
| `R7` Registrar gasto | Gasto realizado | Gasto en el cuaderno de movimientos | Marina | Al realizar el gasto | Local |
| `R8` Cerrar el mes | Cuaderno de movimientos | Reporte del período | Marina | A fin de mes | Local |
| `R9` Reponer insumos | Observación del cajón | Compra de insumos | Marina | Al ver que queda poco | Local / comercio |
| `R11` Definir tipos de clase | — | Precio, duración y cupo máximo por tipo | Lucía | Al definir la oferta | Estudio |
| `R25` Registrar profesora | Datos y disponibilidad de la profesora | Fila en la planilla de profesoras | Lucía | Al incorporarla o cambiar la disponibilidad | Estudio |
| `R12` Armar la grilla | Planilla de profesoras | Grilla semanal publicada | Lucía | De antemano; al cambiar temporada o disponibilidad | Estudio |
| `R29` Registrar alumno | Datos y observaciones de salud del alumno | Registro de alumnos | Lucía | En la primera inscripción | Estudio |
| `R30` Registrar abono | Frecuencia, clases fijas elegidas y pago del alumno | Fila en la planilla de abonos | Lucía | Al inscribirse y cada mes al renovar | Estudio |
| `R31` Mantener listas fijas | Planilla de abonos | Alumno incorporado o quitado de las listas fijas | Lucía | Al inscribirse; al vencer sin renovar | Estudio |
| `R32` Registrar aviso de falta | Aviso del alumno | Lugar liberado esa semana; ausencia si no avisó | Lucía | Al recibir el aviso; pasada la clase | Estudio |
| `R34` Suspender clase | Feriado o ausencia de la profesora | Clase suspendida; avisos por WhatsApp | Lucía | Antes de la clase de esa semana | Estudio |
| `R13` Anotar clase suelta | Clase elegida, del alumno por WhatsApp | Alumno en la lista de esa fecha | Lucía | Al recibir el mensaje | Estudio |
| `R14` Tomar asistencia y cobrar | Lista de la semana; alumnos presentes | Lista con asistencia; cobros de clases sueltas | Profesora; Lucía registra | Durante la clase; al terminar el turno | Estudio |
| `R26` Reponer elementos | Observación del estado de los elementos | Compra de reposición | Lucía | Al verlos gastados | Estudio / comercio |
| `R27` Registrar gastos y cerrar el mes | Gastos; cobros de abonos y clases sueltas | Registro de movimientos; resultado del período | Lucía | Al gastar; a fin de mes | Estudio |

---

# SECCIÓN 4 — AMBIGÜEDADES DETECTADAS

Las ambigüedades se detectan acá con su interpretación propuesta. La decisión adoptada sobre cada una se documenta
en `srs.md`, Sección 3. Las filas 4, 5, 7 y 10 surgieron de necesidades manifestadas, cuyo texto está en `srs.md`,
Sección 2.

| # | Tipo | Ambigüedad | Interpretación propuesta |
|---|---|---|---|
| 1 | Vaguedad | `R5`: no se entrega comprobante "salvo que solicite factura" | La factura se emite por fuera del sistema cuando el cliente la pide |
| 2 | Semántica | `R7` y `R27`: los tipos de gasto no son los mismos en los dos casos | El tipo de gasto es un valor que define cada negocio |
| 3 | Semántica | `R12`: la grilla "se repite todas las semanas", pero se suspenden clases puntuales (`R34`) | Grilla recurrente de la que surgen clases concretas por semana |
| 4 | Vaguedad | `R16`: "el aviso salga antes del turno" no tiene criterio verificable | Requiere un valor concreto de anticipación |
| 5 | Vaguedad | `R16`: "resumen de la agenda del día siguiente" no tiene momento de emisión | Requiere una hora concreta |
| 6 | Pragmática | `R14`: la profesora cobra, pero quien registra es Lucía | Registra la dueña; la profesora entrega la lista y lo cobrado |
| 7 | Semántica | `R15`: "identificarse antes de reservar" admite cuenta con credenciales o datos básicos | Los datos de `R3` y `R29` son el mínimo observado |
| 8 | Vaguedad | `R9`: el consumo de insumos no se registra y varía entre atenciones | No hay base para calcular consumo por servicio |
| 9 | Semántica | `R2`: el cliente "en la mayoría de los casos" indica trabajadora | El cliente elige; se le ofrece el horario de esa misma persona |
| 10 | Léxica | `R15`: "pendiente" convive con otro vocabulario de estados | Requiere un glosario cerrado |
| 11 | Léxica | "Clase" designa tres cosas: el **tipo de clase** (`R11`), la **clase de la grilla** (`R12`) y la **clase de una semana puntual** que se suspende o donde se toma asistencia (`R34`, `R14`) | Distinguir en el glosario tipo de clase, clase de la grilla y clase de la semana |
| 12 | Semántica | `R30`: el abono tiene "un precio fijo", pero el alumno elige cuántas veces por semana asiste | El precio lo fija la dueña para cada abono; se registra el monto pagado |
| 13 | Vaguedad | `R32`: "avisar con anticipación" no fija cuánta | Cualquier aviso previo a la clase libera el lugar; como la clase se pierde igual, no hace falta un plazo |

---

# SECCIÓN 5 — ANÁLISIS DE LA INFORMACIÓN RELEVADA

## 5.1 Variabilidad entre rubros

Al contrastar los casos con otros rubros de servicios, **lo que cambia son los valores, no las reglas**:

| Dato | Peluquería | Yoga | Tatuajes *(comparación)* | ¿Regla o valor? |
|---|---|---|---|---|
| Precio y duración del servicio | Por servicio | Por tipo de clase | Por trabajo | **Valor** |
| Cupo máximo | 1 | Por tipo de clase | 1 | **Valor** |
| Tiempo de espera dentro del servicio | En la coloración | No tiene | No tiene | **Valor** |
| Horario de atención | Por trabajadora, con pausas | Por profesora | Por tatuador | **Valor** |
| Momento de creación del turno | Al pedir el turno | Grilla previa | Al pedir el turno | **Valor**, derivado del cupo máximo |
| Recibe clientes sin turno | Sí | No | No | **Valor** |
| Forma de pago | Por atención | Abono mensual con clases fijas o clase suelta | Por sesión | **Valor** |
| Tipos de gasto | Seis tipos | Cuatro tipos | — | **Valor** |
| No se superponen dos turnos de la misma persona, salvo decisión de quien atiende | Sí | Sí | Sí | **Regla común** |
| No se anota más gente que el cupo máximo | Sí | Sí | Sí | **Regla común** |
| La persona que atiende queda determinada antes de atender | Elegida por el cliente | Por la clase elegida | Elegida por el cliente | **Regla común** |

**Conclusión.** El turno individual es el caso particular de un servicio con cupo máximo 1. Un único modelo cubre
ambas formas de atención.

## 5.2 Supuestos de dimensionamiento

> Estos valores **no surgen del relevamiento**: son supuestos del equipo para poder formular requerimientos de
> desempeño verificables. Se revisan si el relevamiento incorpora volúmenes reales.

| Supuesto | Valor |
|---|---|
| Atenciones por año en un negocio de dos trabajadoras | ~3.200 |
| Clientes activos por negocio | ~340 |

---

# SECCIÓN 6 — CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Relevamiento narrativo | `Relevamiento WorkUp.md` | Fuente |
| Análisis del relevamiento | `relevamiento.md` | Este documento |
| Especificación de Requerimientos (IEEE 830) | `srs.md` | Emitido |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
| Evoluciones futuras | `evoluciones/` | Fuera de alcance |
| Versiones anteriores | `historial/` | Archivo |
