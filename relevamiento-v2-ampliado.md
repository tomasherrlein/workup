# WorkUp — Informe de Relevamiento

**Versión:** 2.0 — Alcance ampliado (varios trabajadores + atención grupal)
**Fecha:** 08-09-2026
**Equipo:** Bapton Solutions

---

## Contexto

Bapton Solutions fue contratada para desarrollar un sistema web que permita a emprendedores de servicios administrar su actividad diaria.

Para el relevamiento se tomaron dos casos de estudio complementarios, elegidos para cubrir las dos formas de atención que se observan en el rubro:

- **Caso A — Peluquería de Marina Gómez:** atención individual, con más de una persona trabajando.
- **Caso B — Estudio de yoga de Lucía Fernández:** atención grupal, con clases de cupo limitado.

---

# Caso A — Peluquería

Marina Gómez es dueña de una peluquería en la que trabaja junto a Sofía Paz, una empleada que atiende sus propios turnos.

Marina ofrece distintos servicios y a cada uno le asigna un precio y una duración estimada. Actualmente el corte de pelo lleva 40 minutos y cuesta $8.000, la coloración lleva 2 horas y cuesta $25.000, y el brushing lleva 30 minutos y cuesta $6.000. Cuando cambian los precios, los actualiza en una hoja pegada en el espejo. Cada una de las dos tiene su propio horario: Marina atiende de martes a sábado de 9 a 18, y Sofía de martes a sábado de 12 a 20. **`{R1}`**

Cuando una clienta quiere atenderse, escribe por WhatsApp indicando qué servicio necesita, qué día le queda cómodo y, muchas veces, con quién se quiere atender. Marina revisa la libreta, que tiene una columna por cada una de ellas, y busca un espacio libre en la columna de la persona que va a atender, verificando que alcance para la duración de ese servicio. Si no le entra, ofrece el horario libre más cercano de esa misma persona. Anota en la columna correspondiente el nombre de la clienta, el servicio y el horario. Dos clientas pueden estar agendadas en el mismo horario siempre que las atiendan personas distintas. **`{R2}`**

La primera vez que atienden a alguien, le piden el nombre, el apellido y un teléfono de contacto, y lo anotan en una agenda aparte. Con el tiempo le suman observaciones que sirven para las próximas visitas, como el tono de tintura que usó o alguna alergia. **`{R3}`**

Cada mañana Marina repasa la libreta para ver a quiénes atienden ese día. El día anterior a cada turno le manda un mensaje a la clienta para confirmar que va a venir. Si la clienta confirma, lo marca con una tilde. Si avisa que no puede, tacha el turno y ese horario le queda libre a esa trabajadora para ofrecérselo a otra persona. **`{R4}`**

Cuando la clienta llega, la atienden y al terminar le cobran. Anotan el monto cobrado en un cuaderno, junto con la fecha, el servicio realizado y quién la atendió. Si la clienta no se presenta y tampoco avisó, lo anotan como ausente, porque a Marina le interesa saber quiénes le fallan seguido. **`{R5}`**

Marina también anota en ese mismo cuaderno lo que gasta: la compra de productos, el alquiler del local, la luz y el transporte. Cada gasto lo anota con la fecha, el monto y una descripción de qué fue. **`{R6}`**

A fin de mes, Marina suma con la calculadora todo lo que cobró y le resta todo lo que gastó, para saber cuánto le quedó. Es una tarea que le lleva un rato largo y en la que suele equivocarse, sobre todo cuando hay hojas del cuaderno con la letra corrida o anotaciones que quedaron sin fecha. **`{R7}`**

Para trabajar usan productos que se van consumiendo: tinturas, shampoo, oxidantes y guantes. Cuando terminan de atender, anotan mentalmente qué usaron, pero no llevan un registro. Se dan cuenta de que se están quedando sin algo recién cuando abren el cajón y ven poco, y más de una vez tuvieron que salir corriendo a comprar tintura en el medio de la jornada o reprogramar una coloración. **`{R8}`**

Cuando le preguntan cómo le fue en el mes, Marina no sabe responder con precisión. No tiene forma de saber qué servicio le dejó más plata, qué clientas vuelven seguido y cuáles dejaron de venir, cuántos turnos se cayeron por ausencias, ni cuánto trabajó cada una de las dos. **`{R9}`**

---

# Caso B — Estudio de yoga

Lucía Fernández tiene un estudio de yoga donde da clases grupales. También trabaja con una profesora, Julieta Ríos, que dicta sus propias clases.

Lucía ofrece distintos tipos de clase y a cada una le asigna un precio, una duración y una **cantidad máxima de alumnos**, que depende del espacio y del tipo de práctica. La clase de hatha dura 60 minutos, cuesta $6.000 y entran 8 personas. La de vinyasa dura 75 minutos, cuesta $7.000 y entran 6 personas, porque necesita más lugar por alumno. **`{R10}`**

A diferencia de una peluquería, los alumnos no eligen el horario que quieren: Lucía arma la grilla semanal por adelantado. Define qué clase se dicta, qué día, a qué hora y quién la dicta, y esa clase queda publicada con sus lugares disponibles. Lucía y Julieta pueden dar clases distintas en el mismo horario, porque el estudio tiene dos salas. **`{R11}`**

Cuando un alumno quiere asistir, avisa por WhatsApp a qué clase se quiere anotar. Lucía revisa cuántos lugares quedan en esa clase y, si hay lugar, lo anota en la lista de esa clase puntual. Si la clase está completa, le ofrece otro día u horario. Un alumno anotado puede avisar que no va a ir, y en ese caso se libera su lugar para otra persona. **`{R12}`**

Cada alumno paga su propio lugar al llegar, y Lucía lo anota individualmente. Si un alumno se anotó y no fue sin avisar, lo registra como ausente, porque ese lugar quedó desaprovechado. Al finalizar la clase, Lucía necesita saber cuántos de los anotados efectivamente asistieron y cuánto se cobró en total por esa clase. **`{R13}`**

---

## Necesidades planteadas por las usuarias

Ambas manifiestan que contestar mensajes para coordinar turnos les interrumpe el trabajo constantemente, y que muchas consultas les llegan fuera del horario de atención. Quieren que sus clientes puedan ver la disponibilidad real y anotarse por su cuenta, sin que ellas tengan que intervenir. Cada cliente debería identificarse antes de reservar, y la reserva quedaría pendiente hasta que el profesional la confirme. **`{R14}`**

También plantean que las ausencias les generan pérdidas y que hoy los recordatorios los mandan a mano, uno por uno, y a veces se olvidan. Necesitan que el aviso al cliente salga solo antes del turno, y recibir ellas un resumen de su agenda del día siguiente. **`{R15}`**

---

## Análisis de variabilidad entre rubros

Al contrastar ambos casos con otros rubros de servicios, se observa que lo que cambia son los valores, no las reglas:

| Dato | Peluquería | Yoga | Tatuajes | ¿Regla o valor? |
|---|---|---|---|---|
| Duración del servicio | 40 min | 60 min | 4 hs | **Valor** — campo del tarifario |
| Precio del servicio | $8.000 | $6.000 | Variable | **Valor** — campo del tarifario |
| **Cupo del servicio** | 1 | 8 | 1 | **Valor** — campo del tarifario |
| Horario de atención | 9 a 18 | 8 a 21 | 14 a 22 | **Valor** — por trabajador |
| Insumos que se consumen | Tinturas, guantes | Mats, bandas | Agujas, tinta | **Valor** — registro genérico |
| No puede haber dos turnos superpuestos del mismo trabajador | Sí | Sí | Sí | **Regla común** |
| No se puede anotar más gente que el cupo | Sí (cupo 1) | Sí (cupo 8) | Sí (cupo 1) | **Regla común** |

**Conclusión del análisis:** el turno individual es el caso particular de un servicio con cupo 1. Un único modelo cubre ambas formas de atención.

---

## Reglas de negocio derivadas

1. **Disponibilidad por trabajador.** El horario libre se calcula sobre el horario de atención del trabajador asignado, descontando sus turnos ya tomados y la duración del servicio elegido. Dos turnos pueden coincidir en horario si corresponden a trabajadores distintos.

2. **Cupo por servicio.** Cada servicio del tarifario define cuántos clientes admite un turno. El sistema impide anotar más clientes que el cupo definido.

3. **El estado y el importe corresponden al cliente, no al turno.** En un turno de cupo mayor a 1, cada cliente anotado tiene su propia confirmación, su propio cobro y su propia asistencia o ausencia.

4. **Momento de creación del turno.** En servicios de cupo 1, el turno se crea cuando el cliente reserva. En servicios de cupo mayor a 1, el profesional programa el turno por adelantado y los clientes se anotan sobre uno ya existente.

---

## Unidad de negocio de la primera versión

Un emprendimiento con uno o más trabajadores, donde un turno puede recibir uno o varios clientes según el cupo definido para el servicio.

---

## Cuestiones a definir

`[AMBIGÜEDAD]` **Elección de trabajador en la reserva online.** No está definido si el cliente elige con qué trabajador se atiende, o si el sistema le muestra todos los horarios libres del negocio y luego asigna a quien esté disponible. En el Caso A las clientas suelen pedir a una persona en particular, lo que sugiere que la elección debería estar disponible. Pendiente de confirmación con las usuarias, ya que impacta en el proceso de reserva y en su manejo de errores.

---

## Fuera de alcance de la primera versión

- **Trabajadores como usuarios del sistema.** El trabajador se registra como un dato del negocio, no como un usuario con credenciales propias. No se contemplan roles ni permisos diferenciados: el dueño es el único que opera el panel de gestión. Incorporarlo implicaría agregar una regla de autorización a cada uno de los requerimientos del sistema.
- **Trabajos en múltiples sesiones.** Un trabajo con seña y varios turnos encadenados, como un tatuaje extenso, requiere vincular turnos entre sí y administrar pagos parciales.
- **Gestión de espacios o salas.** La disponibilidad se calcula únicamente sobre el trabajador. No se contempla el conflicto por recursos compartidos, como dos trabajadores que necesiten la misma sala o el mismo equipamiento.

Todas quedan identificadas como evoluciones posibles del sistema.
