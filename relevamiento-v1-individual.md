# WorkUp — Informe de Relevamiento

**Versión:** 1.0 — Alcance individual
**Fecha:** 08-09-2026
**Equipo:** Bapton Solutions

---

## Contexto

Bapton Solutions fue contratada para desarrollar un sistema web que permita a emprendedores de servicios administrar su actividad diaria. Para el relevamiento se tomó como caso de estudio a Marina Gómez, dueña de una peluquería en la que trabaja sola.

---

## Situación actual

Marina ofrece distintos servicios y a cada uno le asigna un precio y una duración estimada. Actualmente el corte de pelo le lleva 40 minutos y cuesta $8.000, la coloración le lleva 2 horas y cuesta $25.000, y el brushing le lleva 30 minutos y cuesta $6.000. Cuando cambian los precios, los actualiza en una hoja pegada en el espejo. Atiende de martes a sábado de 9 a 18, y se toma una hora de almuerzo a las 13. **`{R1}`**

Cuando una clienta quiere atenderse, le escribe por WhatsApp indicando qué servicio necesita y qué día le queda cómodo. Marina revisa su libreta, busca un espacio libre que alcance para la duración de ese servicio y le confirma día y hora. Si no le entra, le ofrece el horario libre más cercano. Anota en la libreta el nombre de la clienta, el servicio y el horario. **`{R2}`**

La primera vez que atiende a alguien, Marina le pide el nombre, el apellido y un teléfono de contacto, y lo anota en una agenda aparte. Con el tiempo le suma observaciones que le sirven para las próximas visitas, como el tono de tintura que usó o alguna alergia. **`{R3}`**

Cada mañana Marina repasa la libreta para ver a quiénes atiende ese día. El día anterior a cada turno le manda un mensaje a la clienta para confirmar que va a venir. Si la clienta confirma, lo marca con una tilde. Si avisa que no puede, tacha el turno y ese horario le queda libre para ofrecérselo a otra persona. **`{R4}`**

Cuando la clienta llega, Marina la atiende y al terminar le cobra. Anota el monto cobrado en un cuaderno, junto con la fecha y el servicio realizado. Si la clienta no se presenta y tampoco avisó, Marina lo anota como ausente, porque le interesa saber quiénes le fallan seguido. **`{R5}`**

Marina también anota en ese mismo cuaderno lo que gasta: la compra de productos, el alquiler del local, la luz y el transporte. Cada gasto lo anota con la fecha, el monto y una descripción de qué fue. **`{R6}`**

A fin de mes, Marina suma con la calculadora todo lo que cobró y le resta todo lo que gastó, para saber cuánto le quedó. Es una tarea que le lleva un rato largo y en la que suele equivocarse, sobre todo cuando hay hojas del cuaderno con la letra corrida o anotaciones que quedaron sin fecha. **`{R7}`**

Para trabajar, Marina usa productos que se le van consumiendo: tinturas, shampoo, oxidantes y guantes. Cuando termina de atender, anota mentalmente qué usó, pero no lleva un registro. Se da cuenta de que se está quedando sin algo recién cuando abre el cajón y ve poco, y más de una vez tuvo que salir corriendo a comprar tintura en el medio de la jornada o reprogramar una coloración. **`{R8}`**

Cuando le preguntan cómo le fue en el mes, Marina no sabe responder con precisión. No tiene forma de saber qué servicio le dejó más plata, qué clientas vuelven seguido y cuáles dejaron de venir, ni cuántos turnos se le cayeron por ausencias. **`{R9}`**

---

## Necesidades planteadas por la usuaria

Marina manifiesta que contestar mensajes para coordinar turnos le interrumpe el trabajo constantemente, y que muchas consultas le llegan fuera del horario de atención. Quiere que sus clientas puedan ver los horarios disponibles y sacar el turno por su cuenta, sin que ella tenga que intervenir. Cada clienta debería identificarse antes de reservar, y el turno quedaría pendiente hasta que Marina lo confirme. **`{R10}`**

También plantea que las ausencias le generan pérdidas y que hoy los recordatorios los manda a mano, uno por uno, y a veces se olvida. Necesita que el aviso a la clienta salga solo antes del turno, y recibir ella un resumen de su agenda del día siguiente. **`{R11}`**

---

## Análisis de variabilidad entre rubros

El relevamiento se realizó sobre un caso concreto para obtener reglas de negocio verificables. Al contrastarlo con otros rubros de servicios se observa que lo que cambia son los valores, no las reglas:

| Dato | Peluquería | Tatuajes | Masajes | ¿Regla o valor? |
|---|---|---|---|---|
| Duración del servicio | 40 min | 4 hs | 60 min | **Valor** — campo del tarifario |
| Horario de atención | 9 a 18 | 14 a 22 | 10 a 19 | **Valor** — configuración |
| Insumos que se consumen | Tinturas, guantes | Agujas, tinta | Aceites, toallas | **Valor** — registro genérico |
| Un turno corresponde a un cliente | Sí | Sí | Sí | **Regla común** |

Por lo tanto, las reglas relevadas son aplicables a cualquier emprendimiento de servicios de atención individual.

---

## Unidad de negocio de la primera versión

Un profesional que trabaja solo, atendiendo un cliente por turno.

---

## Fuera de alcance de la primera versión

Se identificaron situaciones propias de otros rubros que no se contemplan en esta versión:

- **Atención grupal:** un mismo turno con varios clientes simultáneos, como una clase de entrenamiento o de yoga. Requiere administrar cupo por turno e inscripciones individuales, con cobro, cancelación y ausencia por participante.
- **Trabajos en múltiples sesiones:** un trabajo con seña y varios turnos encadenados, como un tatuaje extenso. Requiere vincular turnos entre sí y administrar pagos parciales.
- **Varios trabajadores:** un negocio con más de un profesional atendiendo. Requiere calcular la disponibilidad por persona y no por negocio.

Ambas quedan identificadas como evoluciones posibles del sistema.
