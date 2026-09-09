# WorkUp — Informe de Relevamiento

**Versión:** 4.0
**Fecha:** 09-09-2026
**Equipo:** Bapton Solutions
**Reemplaza a:** v1.0, v2.0 y v3.x

**Objeto de este documento.** Describe **cómo trabajan hoy** los casos relevados: qué hacen, con qué soportes,
quién lo hace, cuándo y dónde. No especifica el sistema a construir. Los requerimientos derivados de este
relevamiento se especifican en `srs.md`, y el modelado en `analisis.md`.

**Convención de trazabilidad.** Cada afirmación relevada lleva un identificador `{R#}` de numeración continua.
Los identificadores no se reutilizan ni se renumeran. Toda especificación posterior debe poder rastrearse a uno
de ellos.

> Los identificadores `R20`, `R21` y `R24` se incorporaron en revisiones posteriores y aparecen fuera de orden
> numérico dentro de la narrativa. Se ubican donde corresponde temáticamente, conservando su número original.
> Lo mismo vale para `R25`, `R26` y `R27`, que cierran el Caso B.
> Los identificadores `R15` a `R19`, `R22` y `R23` corresponden a necesidades manifestadas por las usuarias: no
> describen el funcionamiento actual, por lo que se especifican en `srs.md`.

---

# SECCIÓN 1 — RECONOCIMIENTO

## 1.1 Información de la organización

| Campo | Caso A | Caso B |
|---|---|---|
| Nombre | Peluquería Marina Gómez | Estudio de yoga Prana |
| Actividad principal | Servicios de peluquería y coloración | Clases grupales de yoga |
| Tipo de organización | Emprendimiento unipersonal con una empleada | Emprendimiento unipersonal con una profesora |
| Ubicación | Villa Crespo, CABA. Local a la calle, un ambiente | Caballito, CABA. Local con dos salas |
| Antigüedad | 6 años | 3 años |
| Estructura organizacional | Marina Gómez (dueña, atiende) + Sofía Paz (trabajadora, atiende) | Lucía Fernández (dueña, dicta) + Julieta Ríos (profesora, dicta) |
| Contacto clave | Marina Gómez, dueña | Lucía Fernández, dueña |
| Condición fiscal | Monotributo | Monotributo |

> **Nota metodológica:** si la cátedra exige el Informe de Reconocimiento como documento independiente, esta
> sección se extrae a un archivo propio sin otras modificaciones.

## 1.2 Requerimiento del cliente

Bapton Solutions fue contratada para desarrollar un sistema web que permita a emprendedores de servicios
administrar su actividad diaria. Se seleccionaron dos casos de estudio complementarios, elegidos para cubrir las
dos formas de atención observadas en el rubro: **atención individual** (Caso A) y **atención grupal con cupo
limitado** (Caso B).

## 1.3 Visión del consultor

Los tres problemas centrales observados, en orden de impacto declarado por las usuarias:

1. **La coordinación de turnos consume tiempo productivo.** La atención de mensajes interrumpe el trabajo y llega
   fuera del horario de atención.
2. **La información está dispersa en tres soportes no vinculados:** libreta de turnos, agenda de clientes y
   cuaderno de movimientos (`R2`, `R3`, `R5`, `R7`). Ningún soporte referencia a los otros, y `R21` muestra que
   hay ingresos registrados que ningún turno explica.
3. **No hay información consolidada para decidir.** El cierre mensual es manual y con errores (`R8`), y no existe
   forma de responder preguntas básicas sobre el negocio (`R10`).

## 1.4 Observaciones

- **La operación de ambos casos es inversa.** En el Caso A la demanda precede a la oferta: el cliente pide y
  recién ahí se crea el turno. En el Caso B la oferta se publica primero y el cliente se anota sobre algo que ya
  existe.
- **Ninguna de las dos usuarias tiene formación administrativa.** Ambas describieron el cierre mensual como la
  tarea que más les cuesta y la que más postergan.
- **Marina manifestó resistencia inicial a "otro sistema".** Ya probó dos aplicaciones de turnos y las abandonó
  porque, según relató, resolvían la agenda pero la obligaban a seguir llevando el cuaderno de plata aparte.
  Este dato condiciona la aceptación de cualquier solución.
- **La libreta admite anotaciones que un formulario rígido no admitiría.** `R20` describe turnos escritos al
  costado o encimados. Es a la vez la mayor fuente de desorden y la razón por la que la libreta le sigue
  sirviendo.

---

# SECCIÓN 2 — FUNCIONAMIENTO ACTUAL

## 2.1 Caso A — Peluquería

Marina Gómez es dueña de una peluquería en la que trabaja junto a Sofía Paz, una empleada que atiende sus propios
turnos.

**`{R1}` Servicios y horarios.** Marina define los servicios que ofrece y a cada uno le asigna un precio y una
duración estimada. Actualmente el corte de pelo lleva 40 minutos y cuesta $10.000, la coloración lleva 2 horas y
cuesta $32.000, y el brushing lleva 30 minutos y cuesta $8.000. Aclara que la coloración no la tiene ocupada las
dos horas: son 30 minutos de aplicación, 50 minutos en los que la tintura procesa y ella no hace nada con esa
clienta, y 40 minutos de lavado y terminación. El corte y el brushing, en cambio, la ocupan de punta a punta. En
total el tarifario tiene 14 servicios, de los cuales cuatro tienen ese tiempo de procesado en el medio:
coloración, mechas, balayage y alisado. Marina calcula que de cada diez turnos que toma, unos tres son de
coloración. Cuando cambian los precios, los actualiza en una hoja pegada en el espejo.
Cada una tiene su propio horario de trabajo: Marina atiende de martes a sábado de 9 a 18 y **se toma una hora de
almuerzo a las 13**, durante la cual no agenda turnos; Sofía atiende de martes a sábado de 12 a 20.

**`{R2}` Toma de turno.** Cuando una clienta quiere atenderse, escribe por WhatsApp indicando qué servicio
necesita, qué día le queda cómodo y, muchas veces, con quién se quiere atender. Marina revisa la libreta, que
tiene una columna por cada una de ellas, y busca en la columna de la persona que va a atender un espacio libre
que alcance para la duración de ese servicio. Si no le entra, ofrece el horario libre más cercano de esa misma
persona. Anota en la columna correspondiente el nombre de la clienta, el servicio y el horario. Dos clientas
pueden estar agendadas en el mismo horario siempre que las atiendan personas distintas.

**`{R20}` Aprovechamiento del tiempo de procesado.** Mientras una tintura procesa, Marina atiende a otra clienta:
le hace un corte o un brushing, que entran cómodos en esos 50 minutos. La clienta de la coloración se queda
sentada esperando en otro puesto. En la libreta esto lo resuelve escribiendo el segundo turno chiquito al costado
o encimado sobre el de la coloración, y reconoce que es la principal fuente de confusión de la libreta: hay días
en que ella misma no entiende lo que anotó. Estima que sin este solapamiento perdería alrededor de la mitad de la
tarde cada vez que hace un color.

**`{R21}` Atención sin turno.** Hay clientas que caen al local sin haber sacado turno y preguntan si las pueden
atender. Si en ese momento hay lugar, las atienden; si no, les ofrecen volver más tarde o sacar turno. Estas
atenciones **no se anotan en la libreta**, porque la libreta es de turnos, pero sí se anota el cobro en el
cuaderno. Marina calcula unas 8 por semana. Es una de las razones por las que el cuaderno y la libreta nunca le
cierran entre sí: hay plata cobrada que no tiene ningún turno que la explique.

**`{R24}` Capacidad del local y momento de la anotación.** El local tiene cuatro puestos de atención. Marina
explica que ese número es el que la limita cuando cae alguien sin turno o cuando quiere meter a otra clienta
mientras procesa un color: mira cuántos puestos están ocupados y decide en el momento. Aclara además que muchas
veces **no anota en el momento sino al final de la jornada**, cuando cierra y se sienta con el cuaderno, pero que
anota la hora en la que efectivamente atendió, no la hora en la que se sentó a escribir.

**`{R3}` Alta de clienta.** La primera vez que atienden a alguien, le piden el nombre, el apellido y un teléfono
de contacto, y lo anotan en una agenda aparte de la libreta de turnos. Con el tiempo le suman observaciones que
sirven para las próximas visitas, como el tono de tintura que usó o alguna alergia.

**`{R4}` Confirmación previa.** Cada mañana Marina repasa la libreta para ver a quiénes atienden ese día. El día
anterior a cada turno le manda un mensaje a la clienta para confirmar que va a venir. Si la clienta confirma, lo
marca con una tilde. Si avisa que no puede, tacha el turno y ese horario le queda libre a esa trabajadora para
ofrecérselo a otra persona.

**`{R5}` Atención y cobro.** Cuando la clienta llega, la atienden y al terminar le cobran. Anotan el monto
cobrado en un cuaderno, junto con la fecha, el servicio realizado y quién la atendió. **No entregan ningún
comprobante a la clienta.** Marina aclara que factura solamente cuando alguien se lo pide expresamente, cosa que
según ella no pasa nunca.

**`{R6}` Registro de ausencia.** Si la clienta no se presenta y tampoco avisó, lo anotan como ausente, porque a
Marina le interesa saber quiénes le fallan seguido. Estima que se le caen unos 28 turnos por mes de esta forma.

**`{R7}` Registro de gastos.** Marina también anota en ese mismo cuaderno lo que gasta. Los agrupa mentalmente en
seis tipos: compra de insumos, alquiler del local, servicios (luz, gas e internet), transporte, impuestos y
tasas, y una categoría de varios donde entra todo lo demás. Cada gasto lo anota con la fecha, el monto, el tipo y
una descripción de qué fue.

**`{R8}` Cierre mensual.** A fin de mes, Marina suma con la calculadora todo lo que cobró y le resta todo lo que
gastó, para saber cuánto le quedó. Le lleva alrededor de tres horas y suele equivocarse, sobre todo cuando hay
hojas del cuaderno con la letra corrida o anotaciones que quedaron sin fecha.

**`{R9}` Insumos.** Para trabajar usan productos que se van consumiendo: tinturas, shampoo, oxidantes y guantes,
unos 22 productos distintos en total. Cuando terminan de atender, anotan mentalmente qué usaron, pero no llevan
un registro, y aclaran que el consumo no es igual en cada atención: una coloración puede llevar una o dos
tinturas según el largo del pelo. Se dan cuenta de que se están quedando sin algo recién cuando abren el cajón y
ven poco, y más de una vez tuvieron que salir corriendo a comprar tintura en el medio de la jornada o
reprogramar una coloración.

**`{R10}` Falta de información del negocio.** Cuando le preguntan cómo le fue en el mes, Marina no sabe responder
con precisión. No tiene forma de saber qué servicio le dejó más plata, qué clientas vuelven seguido y cuáles
dejaron de venir, cuántos turnos se cayeron por ausencias, ni cuánto trabajó cada una de las dos.

## 2.2 Caso B — Estudio de yoga

Lucía Fernández tiene un estudio de yoga donde da clases grupales. También trabaja con una profesora, Julieta
Ríos, que dicta sus propias clases.

**`{R11}` Tipos de clase.** Lucía ofrece seis tipos de clase y a cada uno le asigna un precio, una duración y una
**cantidad máxima de alumnos**, que depende del espacio y del tipo de práctica. La clase de hatha dura 60
minutos, cuesta $7.000 y entran 8 personas. La de vinyasa dura 75 minutos, cuesta $8.500 y entran 6 personas,
porque necesita más lugar por alumno.

**`{R12}` Armado de la grilla.** A diferencia de una peluquería, los alumnos no eligen el horario que quieren:
Lucía arma una **grilla semanal fija** que se repite todas las semanas. Define qué clase se dicta, qué día de la
semana, a qué hora y quién la dicta, y esa grilla queda publicada. Son unas 20 clases por semana. La grilla la
revisa una o dos veces al año, cuando cambia la temporada o la disponibilidad de Julieta. Si una semana puntual
se cae una clase, por un feriado o porque la profesora no puede, Lucía la da de baja solo esa semana sin tocar la
grilla, y avisa a los que estaban anotados. Lucía y Julieta pueden dar clases distintas en el mismo horario,
porque el estudio tiene dos salas.

**`{R13}` Inscripción del alumno.** Cuando un alumno quiere asistir, avisa por WhatsApp a qué clase se quiere
anotar. Lucía revisa cuántos lugares quedan en esa clase de esa semana y, si hay lugar, lo anota en la lista de
esa clase puntual. Si la clase está completa, le ofrece otro día u horario. Un alumno anotado puede avisar que no
va a ir, y en ese caso se libera su lugar para otra persona.

**`{R14}` Cobro y asistencia.** Cada alumno paga su propio lugar al llegar, y Lucía lo anota individualmente. Si
un alumno se anotó y no fue sin avisar, lo registra como ausente, porque ese lugar quedó desaprovechado. Al
finalizar la clase, Lucía necesita saber cuántos de los anotados efectivamente asistieron y cuánto se cobró en
total por esa clase.

**`{R25}` Horarios del estudio y de las profesoras.** El estudio abre de lunes a viernes de 8 a 21. Dentro de esa
franja, cada una tiene su propia disponibilidad: Lucía dicta de 8 a 13 y de 17 a 21, porque al mediodía se va a
buscar a su hija y no toma clases en esa franja; Julieta dicta solamente de 17 a 21. Lucía arma la grilla
respetando esas disponibilidades.

**`{R26}` Insumos.** Para dictar las clases usan elementos que se van consumiendo o gastando: mats, bandas
elásticas, cintas y toallas, unos 9 productos distintos en total. Igual que le pasa a Marina (`R9`), Lucía no
lleva un registro de lo que se usa en cada clase, y se entera de que algo está para reponer cuando lo ve gastado.

**`{R27}` Gastos y cierre mensual.** Lucía anota lo que gasta —alquiler del estudio, servicios, insumos e
impuestos— y a fin de mes hace su propio cierre a mano para saber cuánto le quedó, tarea que le lleva alrededor
de 2 horas. Estima además que se le caen unos 54 lugares por mes de alumnos que se anotaron y no fueron.

---

# SECCIÓN 3 — CUADRO QQCCD

| Proceso | Qué recibe / de quién | Qué elabora / a quién | Quién | Cuándo | Dónde |
|---|---|---|---|---|---|
| `R1` Definir servicios y horarios | — | Hoja de precios pegada en el espejo | Marina | Al cambiar los precios | Local |
| `R2` Tomar turno | Servicio, día preferido y trabajadora preferida, de la clienta por WhatsApp | Anotación en la columna de la libreta; confirmación de día y hora a la clienta | Marina | Al recibir el mensaje | Local |
| `R20` Aprovechar el tiempo de procesado | Turno en procesado, en la libreta | Segundo turno anotado al costado o encimado | Marina | Durante el procesado de un color | Local |
| `R21` Atender sin turno | Clienta que se presenta sin reserva | Anotación del cobro en el cuaderno, sin registro en la libreta | Quien esté disponible | Al presentarse la clienta | Local |
| `R3` Dar de alta a la clienta | Nombre, apellido y teléfono, de la clienta | Anotación en la agenda de clientas | Marina o Sofía | En la primera atención | Local |
| `R4` Confirmar turnos del día siguiente | Libreta de turnos | Mensaje de confirmación a la clienta; tilde o tachado en la libreta | Marina | Cada mañana | Local |
| `R5` Atender y cobrar | Clienta presente | Anotación en el cuaderno (fecha, monto, servicio, quién atendió). Sin comprobante a la clienta | Quien esté en el mostrador | Al terminar la atención, o al cierre de la jornada (`R24`) | Local |
| `R6` Registrar ausencia | Turno agendado sin presentación ni aviso | Anotación de ausencia | Quien esté en el mostrador | Pasado el horario del turno | Local |
| `R7` Registrar gasto | Ticket o factura del proveedor | Anotación en el cuaderno (fecha, monto, tipo, descripción) | Marina | Al incurrir en el gasto | Local |
| `R8` Cerrar el mes | Cuaderno de cobros y gastos | Resultado del período (cálculo manual) | Marina | A fin de mes | Local |
| `R9` Reponer insumos | Observación visual del cajón | Compra de insumo | Marina | Al detectar faltante | Local / comercio |
| `R24` Decidir si hay lugar | Observación de los puestos ocupados | Decisión de atender o no en el momento | Marina | Al caer alguien sin turno o al solapar un procesado | Local |
| `R11` Definir tipos de clase | — | Precio, duración y cupo máximo por tipo de clase | Lucía | Al definir o cambiar la oferta | Estudio |
| `R25` Definir horarios de las profesoras | Disponibilidad de cada profesora | Franjas horarias sobre las que se arma la grilla | Lucía | Al cambiar la disponibilidad | Estudio |
| `R12` Armar la grilla semanal | Horarios de las profesoras (`R25`) | Grilla semanal publicada, con sus lugares disponibles | Lucía | Una o dos veces al año; bajas puntuales por semana | Estudio |
| `R13` Anotar alumno en clase | Clase elegida, del alumno por WhatsApp | Anotación en la lista de esa clase | Lucía | Al recibir el mensaje | Estudio |
| `R14` Cobrar y tomar asistencia | Alumno presente | Anotación individual de pago y asistencia; total cobrado por clase | Lucía | Al llegar el alumno / al finalizar la clase | Estudio |
| `R26` Reponer insumos | Observación del estado del elemento | Compra de insumo | Lucía | Al detectar que está gastado | Estudio / comercio |
| `R27` Registrar gasto | Ticket o factura del proveedor | Anotación de gastos (alquiler, servicios, insumos, impuestos) | Lucía | Al incurrir en el gasto | Estudio |
| `R27` Cerrar el mes | Registro de cobros y gastos | Resultado del período (cálculo manual) | Lucía | A fin de mes | Estudio |

---

# SECCIÓN 4 — AMBIGÜEDADES DETECTADAS

Se relevan aquí las ambigüedades encontradas al analizar la información, con la interpretación propuesta. La
**decisión adoptada** sobre cada una se documenta en `srs.md`, Sección 3.

> Las ambigüedades 4, 5, 7 y 10 no surgieron de la narrativa del funcionamiento actual sino de las **necesidades
> manifestadas** por las usuarias (`R15` a `R19`, `R22` y `R23`), que por su naturaleza se especifican en
> `srs.md`, Sección 2. Se detectan y documentan acá porque la detección de ambigüedades es parte del
> relevamiento, aunque su texto de origen viva en el otro documento.

| # | Tipo | Ambigüedad | Interpretación propuesta |
|---|---|---|---|
| 1 | Vaguedad | `R5` dice "le cobran" sin indicar si se entrega comprobante | Confirmado: no se entrega comprobante (R5) |
| 2 | Semántica | `R7` no aclaraba si el gasto se clasifica o es texto libre | Confirmado: se agrupa en seis tipos |
| 3 | Semántica | `R12` no aclaraba si la grilla es recurrente o se carga semana a semana | Confirmado: recurrente, con bajas por semana puntual |
| 4 | Vaguedad | `R16`: "el aviso salga antes del turno" no tiene criterio verificable | Requiere un valor concreto de anticipación |
| 5 | Vaguedad | `R16`: "resumen de la agenda del día siguiente" no tiene momento de emisión | Requiere una hora concreta |
| 6 | Pragmática | `R5` está en plural, pero el registro lo concentra Marina | Confirmar quién ejecuta el registro cuando atiende Sofía |
| 7 | Semántica | `R15`: "identificarse antes de reservar" admite cuenta con credenciales o datos básicos | Los datos de `R3` son el mínimo observado |
| 8 | Vaguedad | El consumo de insumos no se registra y no es constante (`R9`) | No hay base para calcular consumo por servicio |
| 9 | Semántica | No estaba definido si el cliente elige trabajador o se le asigna | `R2` sugiere que elige: se le ofrece el horario "de esa misma persona" |
| 10 | Léxica | `R15`: "pendiente" convive con otro vocabulario de estados | Requiere un glosario cerrado antes de especificar |

---

# SECCIÓN 5 — ANÁLISIS DE LA INFORMACIÓN RELEVADA

## 5.1 Variabilidad entre rubros

El relevamiento se realizó sobre casos concretos para obtener reglas verificables. Al contrastarlos con otros
rubros de servicios se observa que **lo que cambia son los valores, no las reglas**:

| Dato | Peluquería | Yoga | Tatuajes | ¿Regla o valor? |
|---|---|---|---|---|
| Duración del servicio | 40 min | 60 min | 4 hs | **Valor** |
| Precio del servicio | $10.000 | $7.000 | Variable | **Valor** |
| Cupo del servicio | 1 | 8 | 1 | **Valor** |
| Horario de atención | 9 a 18 | 8 a 13 y 17 a 21 | 14 a 22 | **Valor** — por trabajador |
| Bloqueos dentro de la jornada | Almuerzo 13 a 14 | Mediodía de Lucía | — | **Valor** — por trabajador |
| Tiempo de espera dentro del servicio | 50 min (coloración) | No tiene | No tiene | **Valor** |
| Insumos que se consumen | Tinturas, guantes | Mats, bandas | Agujas, tinta | **Valor** |
| Momento de creación del turno | Al reservar | Grilla previa | Al reservar | **Valor** — derivado del cupo |
| Recibe clientes sin turno | Sí | No | No | **Valor** |
| No puede haber dos turnos superpuestos del mismo trabajador | Sí | Sí | Sí | **Regla común** |
| No se puede anotar más gente que el cupo | Sí (cupo 1) | Sí (cupo 8) | Sí (cupo 1) | **Regla común** |
| El cliente elige con quién se atiende | Sí, directamente | Indirectamente: elige la clase, que ya tiene profesora asignada | Sí, directamente | **Regla común** — el trabajador siempre queda determinado antes de reservar |

**Conclusión:** el turno individual es el caso particular de un servicio con cupo 1. Un único modelo cubre ambas
formas de atención, y las reglas comunes se sostienen en todos los rubros de servicios prestados por turno.

## 5.2 Volumetría

| Indicador | Caso A — Peluquería | Caso B — Yoga |
|---|---|---|
| Días de atención por semana | 5 (martes a sábado) | 5 (lunes a viernes) |
| Trabajadores | 2 | 2 |
| Capacidad simultánea (puestos) | 4 | 2 salas, cupo por clase |
| Servicios en el tarifario | 14 | 6 |
| Servicios con tiempo de espera | 4 (coloración, mechas, balayage, alisado) | Ninguno |
| Turnos reservados por semana | ~55 | ~20 clases, ~85 inscripciones |
| Atenciones sin turno por semana | ~8 | — |
| Atenciones por mes | ~265 (230 reservadas + 35 sin turno) | ~360 inscripciones |
| Coloraciones por mes | ~70 (30% de los turnos reservados) | — |
| Clientes activos (últimos 12 meses) | ~340 | ~85 |
| Insumos controlados | 22 | 9 |
| Ausencias por mes | ~28 (12% de los turnos) | ~54 (15% de las inscripciones) |
| Tiempo del cierre mensual | ~3 horas | ~2 horas |

**Impacto del tramo de espera.** Las ~70 coloraciones mensuales del Caso A contienen 50 minutos de espera cada
una: unas **58 horas de trabajador por mes** durante las cuales Marina está libre. Hoy ya las aprovecha solapando
turnos en la libreta (`R20`). Es capacidad existente, no capacidad nueva.

**Base del dato de ausentismo.** Se adoptó 12% para el Caso A (`R6`) y 15% para el Caso B (`R25`). La referencia del sector
ubica una tasa aceptable por debajo del 5%, pero **solo en negocios con seña o pago anticipado**, mecanismo que
ninguno de los dos casos utiliza. Los recordatorios automáticos reducen los no-shows entre un 30% y un 50%, lo
que ubica a un negocio sin recordatorios sistemáticos por encima del 10%.

---

# SECCIÓN 6 — CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Informe de Relevamiento | `relevamiento.md` | Este documento |
| Especificación de Requerimientos (IEEE 830) | `srs.md` | Emitido |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
