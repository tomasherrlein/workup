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
> Lo mismo vale para `R25`, `R26` y `R27`, que cierran el Caso B. Los identificadores `R29`, `R30` y `R31` se
> incorporaron al redefinir la operatoria del Caso B y también aparecen fuera de orden numérico, ubicados donde
> corresponde temáticamente dentro de la narrativa del estudio de yoga.
> Los identificadores `R15` a `R19`, `R22` y `R23` corresponden a necesidades manifestadas por las usuarias.
> Ninguno describe el funcionamiento actual, por lo que se especifican en `srs.md`.
> Los identificadores `R32` y `R33` se incorporaron para documentar el registro de los datos de las
> trabajadoras, y aparecen fuera de orden numérico ubicados junto a `R1` y `R25` respectivamente, donde
> corresponde temáticamente.

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
2. **La información está dispersa en tres soportes no vinculados:** agenda de turnos, cuaderno de clientes y
   cuaderno de movimientos (`R2`, `R3`, `R5`, `R7`). Ningún soporte referencia a los otros, y `R21` muestra que
   hay ingresos registrados que ningún turno explica.
3. **No hay información consolidada para decidir.** El cierre mensual es manual y con errores (`R8`), y no existe
   forma de responder preguntas básicas sobre el negocio (`R10`).

## 1.4 Observaciones

- **La operación de ambos casos es inversa.** En el Caso A la demanda precede a la oferta: el cliente pide y
  recién ahí se crea el turno. En el Caso B la oferta se publica primero: la grilla existe antes de que el alumno
  decida sumarse, ya sea reservando un lugar fijo semanal mediante un abono o anotándose a una clase suelta.
- **Ninguna de las dos usuarias tiene formación administrativa.** Ambas describieron el cierre mensual como la
  tarea que más les cuesta y la que más postergan.
- **Marina manifestó resistencia inicial a "otro sistema".** Ya probó dos aplicaciones de turnos y las abandonó
  porque, según relató, resolvían la agenda pero la obligaban a seguir llevando el cuaderno de plata aparte.
  Este dato condiciona la aceptación de cualquier solución.
- **La agenda de turnos admite anotaciones que un formulario rígido no admitiría.** `R20` describe turnos escritos al
  costado o encimados. Es a la vez la mayor fuente de desorden y la razón por la que la agenda de turnos le sigue
  sirviendo.

---

# SECCIÓN 2 — FUNCIONAMIENTO ACTUAL

## 2.1 Caso A — Peluquería

Marina Gómez es dueña de una peluquería en la que trabaja junto a Sofía Paz, una empleada que atiende sus propios
turnos.

**`{R1}` Servicios y horarios.** Marina define los servicios que ofrece y a cada uno le asigna un precio y una
duración total estimada. Actualmente el corte de pelo lleva 40 minutos y cuesta $10.000, la coloración lleva 2 horas y
cuesta $32.000, y el brushing lleva 30 minutos y cuesta $8.000. Aclara que la coloración no la tiene ocupada las
dos horas: son 30 minutos de aplicación, 50 minutos en los que la tintura procesa y ella no hace nada con esa
clienta, y 40 minutos de lavado y terminación. El corte y el brushing, en cambio, la ocupan de punta a punta. En
total el tarifario tiene 14 servicios, de los cuales cuatro tienen ese tiempo de procesado en el medio:
coloración, mechas, balayage y alisado. Marina calcula que de cada diez turnos que toma, unos tres son de
coloración. Cuando cambian los precios, actualiza el servicio y el precio en la lista de precios pegada en el
espejo.
Cada una tiene su propio horario de trabajo: Marina atiende de martes a sábado de 9 a 18 y **se toma una hora de
almuerzo a las 13**, durante la cual no agenda turnos; Sofía atiende de martes a sábado de 12 a 20.

**`{R32}` Datos de la trabajadora.** Cuando Sofía empezó a trabajar, Marina anotó en una carpeta de trabajadoras
que se guarda en el local su nombre, su apellido, un teléfono de contacto y su CUIL —necesario para darla de alta
como empleada—, junto con su horario de atención: días, hora de inicio y fin, y pausa. Ahí figuran también los
datos y el horario de la propia Marina. Las columnas de la agenda de turnos se organizan según esos horarios, y cuando un
horario cambia, Marina lo actualiza en la carpeta.

**`{R2}` Toma de turno.** Cuando una clienta quiere atenderse, escribe por WhatsApp indicando qué servicio
necesita, qué día le queda cómodo y, muchas veces, con quién se quiere atender. Si la clienta es nueva, Marina la
da de alta como se describe en `R3` antes de anotar el turno. Hecho esto, Marina revisa la agenda de turnos, que
tiene una columna por cada una de ellas, y busca en la columna de la persona que va a atender un espacio libre
que alcance para la duración total estimada de ese servicio. Si no le entra, ofrece el horario libre más cercano de esa misma
persona. Anota en la columna correspondiente el día, el horario, el nombre de la clienta y el servicio. Dos
clientas pueden estar agendadas en el mismo horario siempre que las atiendan personas distintas.

**`{R20}` Aprovechamiento del tiempo de procesado.** Mientras una tintura procesa, Marina atiende a otra clienta:
le hace un corte o un brushing, que entran cómodos en esos 50 minutos. La clienta de la coloración se queda
sentada esperando en otro puesto. En la agenda de turnos esto lo resuelve escribiendo el segundo turno chiquito al costado
o encimado sobre el de la coloración, y reconoce que es la principal fuente de confusión de la agenda de turnos: hay días
en que ella misma no entiende lo que anotó. Estima que sin este solapamiento perdería alrededor de la mitad de la
tarde cada vez que hace un color.

**`{R21}` Atención sin turno.** Hay clientas que caen al local sin haber sacado turno y preguntan si las pueden
atender. Si en ese momento hay lugar, las atienden; si no, les ofrecen volver más tarde o sacar turno. Estas
atenciones **no se anotan en la agenda de turnos**, pero sí se anota el cobro en el
cuaderno de movimientos. Marina calcula unas 8 por semana. Es una de las razones por las que el cuaderno de movimientos y la agenda de turnos nunca le
cierran entre sí: hay plata cobrada que no tiene ningún turno que la explique.

**`{R24}` Capacidad del local y momento de la anotación.** El local tiene cuatro puestos de atención. Marina
explica que ese número es el que la limita cuando cae alguien sin turno o cuando quiere meter a otra clienta
mientras procesa un color: mira cuántos puestos están ocupados y decide en el momento. Aclara además que muchas
veces **no anota en el momento sino al final de la jornada**, cuando cierra y se sienta con el cuaderno de movimientos, pero que
anota la hora en la que efectivamente atendió, no la hora en la que se sentó a escribir.

**`{R3}` Alta de clienta.** Cuando una clienta que todavía no está registrada toma un turno por primera vez
(`R2`), Marina le pide el nombre, el apellido y un teléfono de contacto, y la anota con esos datos en el cuaderno
de clientes, que se lleva aparte de la agenda de turnos. Con el tiempo agrega ahí observaciones que sirven para
las próximas visitas, como el tono de tintura que usó o alguna alergia.

**`{R4}` Confirmación previa.** Cada mañana Marina repasa la agenda de turnos para ver a quiénes atienden ese día. El día
anterior a cada turno le manda un mensaje a la clienta para confirmar que va a venir. Si la clienta confirma, lo
marca con una tilde. Si avisa que no puede, tacha el turno y ese horario le queda libre a esa trabajadora para
ofrecérselo a otra persona.

**`{R5}` Atención y cobro.** Cuando la clienta llega, la atienden y al terminar le cobran. Anotan en el cuaderno
de movimientos la fecha, la hora de la atención, el monto, el servicio y quién atendió. **No entregan ningún
comprobante a la clienta.** Marina aclara que factura solamente cuando alguien se lo pide expresamente, cosa que
según ella no pasa nunca.

**`{R6}` Registro de ausencia.** Si la clienta no se presenta y tampoco avisó, lo anotan como ausente, porque a
Marina le interesa saber quiénes le fallan seguido. Estima que se le caen unos 28 turnos por mes de esta forma.

**`{R7}` Registro de gastos.** Marina también anota en el cuaderno de movimientos, por cada gasto, la fecha, el
monto, el tipo y una descripción. Los agrupa mentalmente en seis tipos: compra de insumos, alquiler del local, servicios (luz, gas e
internet), transporte, impuestos y tasas, y una categoría de varios donde entra todo lo demás.

**`{R8}` Cierre mensual.** A fin de mes, Marina suma con la calculadora todo lo que cobró y le resta todo lo que
gastó, para saber cuánto le quedó. Le lleva alrededor de tres horas y suele equivocarse, sobre todo cuando hay
hojas del cuaderno de movimientos con la letra corrida o anotaciones que quedaron sin fecha.

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

**`{R11}` Tipos de clase.** Lucía ofrece seis tipos de clase, y a cada uno le asigna un precio, una duración fija
y una **cantidad máxima de alumnos**, que depende del espacio y del tipo de práctica. La clase de hatha dura 60 minutos y entran 8 personas; la de vinyasa dura 75 minutos y
entran 6 personas, porque necesita más lugar por alumno.

**`{R12}` Armado de la grilla.** A diferencia de una peluquería, los alumnos no eligen el horario que quieren:
Lucía arma una **grilla semanal fija** que se repite todas las semanas. Para cada clase de la grilla define qué
tipo de clase se dicta, qué día de la semana, a qué hora y quién la dicta, y esa grilla queda publicada. Son unas 20 clases por semana. La grilla la revisa una o dos veces al año, cuando
cambia la temporada o la disponibilidad de Julieta. Si una semana puntual se cae una clase, por un feriado o
porque la profesora no puede, Lucía la da de baja solo esa semana sin tocar la grilla, y avisa a los alumnos
afectados. Lucía y Julieta pueden dar clases distintas en el mismo horario, porque el estudio tiene dos salas.

**`{R29}` Alta del alumno.** Cuando una persona pide sumarse por primera vez, ya sea con un abono (`R13`) o para
una clase suelta (`R31`), Lucía le pide el nombre, el apellido y un teléfono de contacto, y lo anota con esos
datos en un registro de alumnos, junto con las observaciones de salud que resulten relevantes, como lesiones o
un embarazo en curso.

**`{R13}` Abono mensual.** El alumno elige una frecuencia semanal —una, dos o tres veces por semana— y las clases
fijas de la grilla en las que hay lugar para esa frecuencia. Lucía lo incorpora a la lista fija de esas clases, de
modo que el lugar queda reservado todas las semanas sin necesidad de volver a anotarse. El abono se paga el
día en que el alumno se inscribe, en efectivo o por transferencia, y cubre un mes desde esa fecha: quien pagó un
día 18 vuelve a pagar el 18 del mes siguiente. Lucía deja asentado en una planilla de abonos el alumno, la
frecuencia, las clases fijas, la fecha de pago, el monto y la fecha de vencimiento de cada abono.
Cuando un alumno no renueva al vencimiento, Lucía lo quita de las listas fijas y esos lugares quedan libres para
otros alumnos.

**`{R30}` Aviso de falta y recuperación.** Un alumno con abono que avisa con anticipación que va a faltar a una
clase libera ese lugar para esa semana y puede recuperar la clase en otra que tenga lugar antes del vencimiento
de su abono; Lucía verifica la disponibilidad y deja la recuperación anotada en la lista de esa semana. También son
recuperables dentro de ese plazo las clases que se suspenden por un feriado o porque la profesora no puede dictarlas;
en esos casos Lucía avisa a los alumnos afectados. Si un alumno falta sin avisar, Lucía lo registra como ausente y
esa clase no se recupera.

**`{R31}` Clase suelta.** Una persona sin abono que quiere asistir a una clase puntual escribe por WhatsApp
indicando la clase que le interesa. Lucía revisa la lista de esa semana —los alumnos fijos, descontando los avisos
de falta y sumando las recuperaciones— y, si hay lugar, la anota para esa fecha. Si la clase está completa, le
ofrece otro día u horario. Esta persona paga al llegar al estudio.

**`{R14}` Asistencia y cobro en clase.** Cada profesora toma asistencia en su propia clase sobre la lista de la
clase de esa semana, donde constan la fecha, los alumnos anotados —fijos, recuperaciones y clases sueltas— y la
asistencia o ausencia de cada uno, y cobra a quienes asisten por clase suelta. Julieta le entrega a Lucía la
lista y lo cobrado —efectivo y transferencias— de sus clases al finalizar su turno; Lucía asienta en su registro
de movimientos, por cada cobro de clase suelta, la fecha, el alumno y el monto.

**`{R25}` Horarios del estudio y de las profesoras.** El estudio abre de lunes a viernes de 8 a 21. Dentro de esa
franja, cada una tiene su propia disponibilidad: Lucía dicta de 8 a 13 y de 17 a 21, porque al mediodía se va a
buscar a su hija y no toma clases en esa franja; Julieta dicta solamente de 17 a 21. Lucía arma la grilla
respetando esas disponibilidades.

**`{R33}` Datos de la profesora.** Lucía guarda en una planilla de profesoras el nombre, el apellido, un
teléfono de contacto, el CUIL y la disponibilidad horaria de Julieta, y anota ahí también sus propios datos. Actualiza la planilla cuando cambia la
disponibilidad de alguna, y la usa como base para armar la grilla.

**`{R26}` Elementos reutilizables.** Para dictar las clases usan mats, bandas elásticas, cintas y toallas. Lucía no
lleva un registro de su estado y advierte que necesita reponerlos cuando los ve gastados.

**`{R27}` Gastos y cierre mensual.** Lucía anota en su registro de movimientos, por cada gasto, la fecha, el
monto y el tipo —alquiler del estudio, servicios, reposición de elementos e impuestos— y a fin de mes suma lo
cobrado por abonos y por clases sueltas, le resta los gastos y hace
su propio cierre a mano para saber cuánto le quedó, tarea que le lleva alrededor de 2 horas. Estima además que se
le caen unos 54 lugares por mes entre alumnas con abono que faltan sin avisar y personas de clase suelta que
reservan un lugar y no se presentan.

---

# SECCIÓN 3 — CUADRO QQCCD

| Proceso | Qué recibe / de quién | Qué elabora / a quién | Quién | Cuándo | Dónde |
|---|---|---|---|---|---|
| `R1` Definir servicios y horarios | — | Lista de precios pegada en el espejo (servicio, precio) | Marina | Al cambiar los precios | Local |
| `R32` Registrar datos de la trabajadora | Nombre, apellido, teléfono, CUIL y horario de atención de la trabajadora | Anotación en la carpeta de trabajadoras | Marina | Al incorporarse una trabajadora o cambiar su horario | Local |
| `R2` Tomar turno | Servicio, día preferido y trabajadora preferida, de la clienta por WhatsApp | Anotación en la columna de la agenda de turnos; confirmación de día y hora a la clienta | Marina | Al recibir el mensaje | Local |
| `R20` Aprovechar el tiempo de procesado | Turno en procesado, en la agenda de turnos | Segundo turno anotado al costado o encimado | Marina | Durante el procesado de un color | Local |
| `R21` Atender sin turno | Clienta que se presenta sin reserva | Anotación del cobro en el cuaderno de movimientos, sin registro en la agenda de turnos | Quien esté disponible | Al presentarse la clienta | Local |
| `R3` Dar de alta a la clienta | Nombre, apellido y teléfono, de la clienta | Anotación en el cuaderno de clientes (nombre, apellido, teléfono, observaciones) | Marina | Al tomar el turno de una clienta nueva | Local |
| `R4` Confirmar turnos del día siguiente | Agenda de turnos | Mensaje de confirmación a la clienta; tilde o tachado en la agenda de turnos | Marina | Cada mañana | Local |
| `R5` Atender y cobrar | Clienta presente | Anotación en el cuaderno de movimientos (fecha, monto, servicio, quién atendió). Sin comprobante a la clienta | Quien esté en el mostrador | Al terminar la atención, o al cierre de la jornada (`R24`) | Local |
| `R6` Registrar ausencia | Turno agendado sin presentación ni aviso | Anotación de ausencia | Quien esté en el mostrador | Pasado el horario del turno | Local |
| `R7` Registrar gasto | Ticket o factura del proveedor | Anotación en el cuaderno de movimientos (fecha, monto, tipo, descripción) | Marina | Al incurrir en el gasto | Local |
| `R8` Cerrar el mes | Cuaderno de movimientos (cobros y gastos) | Resultado del período (cálculo manual) | Marina | A fin de mes | Local |
| `R9` Reponer insumos | Observación visual del cajón | Compra de insumo | Marina | Al detectar faltante | Local / comercio |
| `R24` Decidir si hay lugar | Observación de los puestos ocupados | Decisión de atender o no en el momento | Marina | Al caer alguien sin turno o al solapar un procesado | Local |
| `R11` Definir tipos de clase | — | Precio, duración fija y cupo máximo por tipo de clase | Lucía | Al definir o cambiar la oferta | Estudio |
| `R25` Definir horarios de las profesoras | Disponibilidad de cada profesora | Franjas horarias sobre las que se arma la grilla | Lucía | Al cambiar la disponibilidad | Estudio |
| `R33` Registrar datos de la profesora | Nombre, apellido, teléfono, CUIL y disponibilidad horaria de la profesora | Anotación en la planilla de profesoras | Lucía | Al incorporarse una profesora o cambiar su disponibilidad | Estudio |
| `R12` Armar la grilla semanal | Horarios de las profesoras (`R25`) | Grilla semanal publicada, con clase, día, horario y profesora | Lucía | Una o dos veces al año; bajas puntuales por semana | Estudio |
| `R29` Dar de alta al alumno | Nombre, apellido, teléfono y observaciones de salud, del alumno | Anotación en el registro de alumnos (nombre, apellido, teléfono, observaciones de salud) | Lucía | Al inscribirse por primera vez, con abono o para una clase suelta | Estudio |
| `R13` Dar de alta el abono mensual | Frecuencia semanal y clases elegidas, del alumno | Anotación del alumno en las listas fijas de esas clases; anotación del pago en la planilla de abonos (alumno, frecuencia, clases fijas, fecha de pago, monto, fecha de vencimiento) | Lucía | Al inscribirse el alumno y en cada vencimiento mensual del abono | Estudio |
| `R30` Registrar aviso de falta y recuperación | Aviso de falta, del alumno; disponibilidad de otras clases antes del vencimiento del abono | Liberación del lugar en la clase avisada; anotación de la recuperación en la lista de la clase elegida | Lucía | Al recibir el aviso o al reprogramar una clase suspendida | Estudio |
| `R31` Anotar clase suelta | Clase elegida, de la persona por WhatsApp | Anotación en la lista de esa clase para esa fecha | Lucía | Al recibir el mensaje | Estudio |
| `R14` Tomar asistencia y cobrar en clase | Lista de la clase de la semana; alumnos y personas de clase suelta presentes | Asistencia marcada en la lista de la clase (fecha, alumnos anotados, asistencia o ausencia); cobro de clase suelta anotado en el registro de movimientos (fecha, alumno, monto); lista y cobros entregados por Julieta a Lucía | Cada profesora, en su clase; Lucía consolida | Al finalizar cada clase | Estudio |
| `R26` Observar elementos reutilizables | Observación del estado del elemento | Sin registro de control de elementos reutilizables | Lucía | Al detectar que está gastado | Estudio |
| `R27` Registrar gasto | Ticket o factura del proveedor | Anotación en el registro de movimientos de gastos (fecha, monto, tipo) | Lucía | Al incurrir en el gasto | Estudio |
| `R27` Cerrar el mes | Cobros de abonos y de clases sueltas; registro de gastos | Resultado del período (cálculo manual) | Lucía | A fin de mes | Estudio |

---

# SECCIÓN 4 — AMBIGÜEDADES DETECTADAS

Se relevan aquí las ambigüedades encontradas al analizar la información, con la interpretación propuesta. La
**decisión adoptada** sobre cada una se documenta en `srs.md`, Sección 3.


| # | Tipo | Ambigüedad | Interpretación propuesta |
|---|---|---|---|
| 1 | Vaguedad | `R5` dice "le cobran" sin indicar si se entrega comprobante | Confirmado: no se entrega comprobante (R5) |
| 2 | Semántica | `R7` no aclaraba si el gasto se clasifica o es texto libre | Confirmado: se agrupa en seis tipos |
| 3 | Semántica | `R12` no aclaraba si la grilla es recurrente o se carga semana a semana | Confirmado: recurrente, con bajas por semana puntual |
| 4 | Pragmática | `R5` está en plural, pero el registro lo concentra Marina | Confirmar quién ejecuta el registro cuando atiende Sofía |
| 5 | Vaguedad | El consumo de insumos no se registra y no es constante (`R9`) | No hay base para calcular consumo por servicio |
| 6 | Semántica | No estaba definido si el cliente elige trabajador o se le asigna | `R2` sugiere que elige: se le ofrece el horario "de esa misma persona" |

---

# SECCIÓN 5 — ANÁLISIS DE LA INFORMACIÓN RELEVADA

## 5.1 Comparación de los casos relevados

Los dos casos muestran modalidades de atención distintas. La peluquería agenda atenciones individuales a partir del
pedido de la clienta; el estudio de yoga publica primero una grilla semanal y los alumnos se anotan en cada clase.

| Aspecto observado | Peluquería | Estudio de yoga |
|---|---|---|
| Modalidad de atención | Individual | Grupal, con cupo limitado |
| Organización de la agenda | La clienta pide turno y Marina lo anota en la agenda de turnos | Lucía arma una grilla semanal que se repite; los alumnos con abono ocupan lugares fijos en ella |
| Duración de la atención | Cada servicio tiene una duración total estimada | Cada clase tiene una duración fija según su tipo |
| Tiempo intermedio | En la coloración hay procesado; Marina puede atender a otra clienta | No se relevó un tiempo intermedio aprovechable |
| Atención sin turno | Se atiende si hay lugar y el cobro queda en el cuaderno de movimientos | Clase suelta: quien no tiene abono puede asistir a una clase puntual con lugar disponible, coordinada por WhatsApp |
| Materiales | Usa insumos consumibles sin registro de consumo ni existencias | Usa elementos reutilizables sin registro de su estado |

**Conclusión:** ambos casos utilizan registros manuales para organizar la atención, los cobros y los gastos. Sus
prácticas difieren según la modalidad de servicio, el cupo y la forma en que se organiza la agenda.

## 5.2 Volumetría

| Indicador | Caso A — Peluquería | Caso B — Yoga |
|---|---|---|
| Días de atención por semana | 5 (martes a sábado) | 5 (lunes a viernes) |
| Trabajadores | 2 | 2 |
| Capacidad simultánea (puestos) | 4 | 2 salas, cupo por clase |
| Servicios en el tarifario | 14 | 6 |
| Servicios con tiempo de espera | 4 (coloración, mechas, balayage, alisado) | Ninguno |
| Turnos reservados por semana | ~55 | ~20 clases |
| Atenciones sin turno por semana | ~8 | — |
| Atenciones por mes | ~265 (230 reservadas + 35 sin turno) | No relevado |
| Coloraciones por mes | ~70 (30% de los turnos reservados) | — |
| Clientes activos (últimos 12 meses) | ~340 | ~85 |
| Productos consumibles relevados | 22 | No relevado |
| Ausencias por mes | ~28 (12% de los turnos) | ~54 (base no relevada) |
| Tiempo del cierre mensual | ~3 horas | ~2 horas |

**Impacto del tramo de espera.** Las ~70 coloraciones mensuales del Caso A contienen 50 minutos de espera cada
una: unas **58 horas de trabajador por mes** durante las cuales Marina está libre. Hoy ya las aprovecha solapando
turnos en la agenda de turnos (`R20`). Es capacidad existente, no capacidad nueva.

**Base del dato de ausentismo.** A partir de las cantidades estimadas por las usuarias, el Caso A presenta alrededor
de 12% de ausencias (`R6`) sobre los turnos reservados. El Caso B no tiene una base equivalente relevada: el dato
de 54 lugares perdidos por mes (`R27`) combina abonos con aviso tardío y clases sueltas que no se presentan, sin
que se haya relevado el total de lugares ocupados por mes contra el cual calcular un porcentaje.

---

# SECCIÓN 6 — CONTINUIDAD

| Producto | Documento | Estado |
|---|---|---|
| Informe de Relevamiento | `relevamiento.md` | Este documento |
| Especificación de Requerimientos (IEEE 830) | `srs.md` | Emitido |
| Lista de Eventos, DFD, DD, DER | `analisis.md` | Pendiente |
