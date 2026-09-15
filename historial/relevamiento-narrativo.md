Universidad Tecnológica Nacional – Facultad Regional General Pacheco
Técnico Universitario en Programación
Metodología de Sistemas I

# Relevamiento WorkUp

WorkUp releva el funcionamiento de emprendimientos que prestan servicios por turno. Se tomaron dos casos: una
peluquería, que atiende de a un cliente por turno, y un estudio de yoga, que dicta clases grupales de cupo
limitado.

## Peluquería

La peluquería de Marina Gómez ofrece servicios de corte, coloración y peinado. Trabajan dos personas: Marina, la
dueña, y Sofía Paz, una empleada que atiende sus propios turnos.

Los servicios se encuentran identificados por: código de servicio, descripción, precio, duración de aplicación,
duración de espera, duración de terminación y cupo de clientes. El corte lleva 40 minutos de aplicación y cuesta
$10.000; el brushing lleva 30 minutos y cuesta $8.000; la coloración cuesta $32.000 y lleva 30 minutos de
aplicación, 50 minutos de espera mientras la tintura procesa y 40 minutos de terminación, durante los cuales la
peluquera atiende a otra persona. El tarifario tiene 14 servicios, de los cuales cuatro tienen tiempo de espera.
Los precios se anotan en una hoja de precios pegada en el espejo, que se actualiza cuando cambian.

Cada trabajadora se encuentra identificada por: código de trabajador, nombre, apellido y su horario de atención,
compuesto por los días que trabaja, la hora de inicio, la hora de fin y los bloqueos no laborables dentro de la
jornada. Marina atiende de martes a sábado de 9 a 18, con un bloqueo de almuerzo de 13 a 14. Sofía atiende de
martes a sábado de 12 a 20. El local cuenta con cuatro puestos de atención.

Cuando una clienta quiere atenderse escribe por WhatsApp indicando el servicio que necesita, el día que le queda
cómodo y, en la mayoría de los casos, con cuál de las dos se quiere atender. Marina revisa la libreta de turnos,
que tiene una columna por trabajadora, y busca en la columna de la persona indicada un espacio libre que alcance
para la duración del servicio. Si no hay espacio, ofrece el horario libre más cercano de esa misma trabajadora.
Los turnos se encuentran identificados por: número de turno, fecha, hora de inicio, código de servicio, código de
trabajador y código de cliente. Dos turnos pueden coincidir en horario siempre que correspondan a trabajadoras
distintas, y también cuando el segundo cae dentro del tiempo de espera de una coloración, caso en el que se anota
al costado o encimado sobre el primero.

Para tomar el turno la clienta debe estar registrada en la agenda de clientas; si no lo está, se la da de alta en
ese momento. Las clientas se encuentran identificadas por: código de cliente, que corresponde a su teléfono,
apellido y nombre, y observaciones, donde se anotan el tono de tintura que usó o las alergias que declaró.

Cada mañana se repasa la libreta para ver a quiénes se atiende ese día. El día anterior a cada turno se le envía
un mensaje a la clienta para confirmar que va a venir. Si la clienta confirma, el turno se marca con una tilde. Si
avisa que no puede, se tacha el turno y ese horario queda libre para ofrecerlo a otra persona.

Cuando la clienta llega se la atiende y al terminar se le cobra. El cobro se anota en el cuaderno de movimientos,
donde los cobros se encuentran identificados por: número de movimiento, fecha, hora, monto, código de servicio y
código de trabajadora que atendió. No se entrega ningún comprobante a la clienta; solo se factura cuando alguien
lo pide expresamente. Si la clienta no se presenta y tampoco avisó, el turno se anota como ausente, porque
interesa saber quiénes fallan seguido; se estiman unas 28 ausencias por mes.

Hay clientas que se presentan sin turno y preguntan si las pueden atender. El vendedor observa cuántos de los
cuatro puestos están ocupados; si hay lugar se las atiende y se anota el cobro en el cuaderno, y si no lo hay se
les ofrece volver más tarde o sacar turno. Estas atenciones no se anotan en la libreta de turnos, porque la
libreta es únicamente de turnos agendados, de modo que quedan cobros registrados en el cuaderno que ningún turno
explica. Se estiman unas ocho por semana. Muchas veces el cobro no se anota en el momento sino al cierre de la
jornada, consignando la hora en que efectivamente se atendió.

En el mismo cuaderno se anotan los gastos, identificados por: número de movimiento, fecha, monto, tipo de gasto y
descripción. Los tipos de gasto son seis: insumos, alquiler, servicios, transporte, impuestos y tasas, y varios.
A fin de mes se suman con calculadora todos los cobros y se les restan todos los gastos, para obtener el
resultado del período. La tarea lleva alrededor de tres horas y se cometen errores, sobre todo cuando hay hojas
con la letra corrida o anotaciones que quedaron sin fecha.

Para trabajar se usan insumos que se van consumiendo: tinturas, shampoo, oxidantes y guantes, unos 22 productos
distintos. Los insumos se encuentran identificados por: código de insumo, descripción y existencia. Cuando se
termina de atender se registra mentalmente qué se usó, pero no se lleva ningún registro escrito, y el consumo no
es igual en cada atención, porque una coloración puede llevar una o dos tinturas según el largo del pelo. La
falta se detecta al abrir el cajón y ver poco, lo que en más de una ocasión obligó a comprar tintura en el medio
de la jornada o a reprogramar una coloración.

No se dispone de información consolidada del negocio. No se puede establecer qué servicio deja más margen, qué
clientas vuelven seguido y cuáles dejaron de venir, cuántos turnos se cayeron por ausencias, ni cuánto trabajó
cada trabajadora.

## Estudio de yoga

El estudio de yoga de Lucía Fernández dicta clases grupales. Trabajan dos personas: Lucía, la dueña, y Julieta
Ríos, una profesora que dicta sus propias clases. El estudio abre de lunes a viernes de 8 a 21 y cuenta con dos
salas, por lo que ambas pueden dictar clases distintas en el mismo horario.

Los tipos de clase se encuentran identificados por: código de clase, descripción, precio, duración y cantidad
máxima de alumnos, que depende del espacio y del tipo de práctica. La clase de hatha dura 60 minutos, cuesta
$7.000 y admite 8 alumnos. La de vinyasa dura 75 minutos, cuesta $8.500 y admite 6 alumnos, porque necesita más
lugar por persona. El estudio ofrece seis tipos de clase.

Cada profesora tiene su propia disponibilidad dentro del horario del estudio: Lucía dicta de 8 a 13 y de 17 a 21,
y Julieta dicta solamente de 17 a 21.

A diferencia de la peluquería, los alumnos no eligen el horario. La dueña arma por adelantado una grilla semanal
fija que se repite todas las semanas, en la que define qué clase se dicta, qué día de la semana, a qué hora y
quién la dicta. La grilla tiene unas 20 clases por semana y se revisa una o dos veces al año, cuando cambia la
temporada o la disponibilidad de la profesora. Si una semana puntual se cae una clase, por un feriado o porque la
profesora no puede, se la da de baja solo esa semana sin modificar la grilla y se avisa a los alumnos anotados.

Cuando un alumno quiere asistir avisa por WhatsApp a qué clase se quiere anotar. Se revisa cuántos lugares quedan
en esa clase de esa semana y, si hay lugar, se lo anota en la lista de la clase. Si la clase está completa, se le
ofrece otro día u otro horario. Las inscripciones se encuentran identificadas por: número de inscripción, fecha
de la clase, código de clase, código de profesora y código de alumno. Un alumno anotado puede avisar que no va a
ir, y en ese caso su lugar se libera para otra persona.

Cada alumno paga su propio lugar al llegar y el cobro se anota individualmente. La asistencia, en cambio, recién
se conoce al finalizar la clase, momento en el que se necesita saber cuántos de los anotados efectivamente
asistieron y cuánto se cobró en total por esa clase. Si un alumno se anotó y no fue sin avisar, se lo registra
como ausente, porque ese lugar quedó desaprovechado. Se estiman unos 54 lugares perdidos por mes.

Para dictar las clases se usan elementos que se van gastando: mats, bandas elásticas, cintas y toallas, unos 9
productos distintos, de los que tampoco se lleva registro. Los gastos del estudio —alquiler, servicios, insumos e
impuestos— se anotan del mismo modo que en la peluquería, y a fin de mes se hace el cierre a mano para obtener el
resultado del período, tarea que lleva alrededor de dos horas.
