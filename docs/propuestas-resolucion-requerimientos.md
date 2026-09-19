# Propuestas de resolución para brechas de requerimientos

## Estado, alcance y línea de base

**Estado:** documento de propuesta; las decisiones D01–D08 permanecen **Pendientes**.

**Alcance:** resolver coherentemente ocho ambigüedades observadas en los RF vigentes. No modifica los RF, no define una implementación técnica, no incorpora pasarela de pagos ni obliga a integrar SMS o WhatsApp.

**Línea de base:** la fuente normativa para estas propuestas es `Requerimientos Funcionales WorkUp.md` (RF 1–20). `srs.md` se usa solo como antecedente cuando ayuda a explicar una diferencia. En particular, su decisión de no tener cuenta de cliente está superada por los RF 1, 15–18 vigentes y por la intención confirmada de autogestión del cliente.

> Los plazos y horarios que aparecen como **valor propuesto** no son reglas aprobadas. Solo entrarían a los RF luego de una aceptación explícita.

## Resumen de decisiones a conversar

| ID | Tema | Recomendación resumida | Estado |
|---|---|---|---|
| D01 | Cliente, cuenta y vinculación | Separar ficha y cuenta; reclamar historial preexistente solo mediante vínculo confiable previo o verificación de identidad histórica por la dueña. | Pendiente |
| D02 | Reprogramación y cancelación | Reprogramar solo turnos individuales; cancelar una ocurrencia de clase sin afectar el cupo fijo. | Pendiente |
| D03 | Cobros y asistencia | Mantener eventos independientes, trazables, vinculados a la reserva o abono cuando exista, e importe acordado históricamente. | Pendiente |
| D04 | Inscripción pendiente y cupos | Mantener una retención temporal y atómica hasta la primera clase; liberar al vencer o no pagar. | Pendiente |
| D05 | Notificaciones | Aprobar una matriz explícita; correo como canal inicial propuesto y sin recordatorios recurrentes de abono. | Pendiente |
| D06 | Vencimiento y renovación | Liberar el cupo fijo al vencer, conservar el historial y regenerar sin recrear cancelaciones ni suspensiones; la renovación tardía vuelve a validar vacante. | Pendiente |
| D07 | Consecuencias financieras | Distinguir el derecho a reintegro o traslado de su ejecución manual y registrada. | Pendiente |
| D08 | Capacidades y solapamientos | Validar una combinación de capacidad de servicio, trabajadora, puesto y negocio antes de reservar. | Pendiente |

---

## D01 — Ficha de cliente, cuenta y vinculación segura

**Evidencia actual.** RF 1 crea una ficha con datos y credenciales; RF 3, RF 4 y RF 5 buscan por email y pueden dar de alta; RF 15 inicia sesión; RF 16 permite consultar reservas; RF 17 y RF 18 habilitan autogestión. Ningún RF determina si la ficha creada por la dueña y la cuenta creada por el cliente son el mismo objeto, ni cómo se vincula el historial previo. RF 1 exige unicidad de email, pero probar que una cuenta controla hoy un email o teléfono no prueba que controle la identidad histórica de una ficha: esos contactos pueden ser compartidos, reciclados o haber sido cargados erróneamente.

**Recomendación propuesta.** Modelar dos conceptos relacionados, pero distintos:

- **Cliente:** ficha operativa, creada por la dueña o durante una reserva, que concentra contactos, observaciones, turnos, anotaciones y movimientos.
- **Cuenta de cliente:** credencial de acceso creada y controlada por el propio cliente. Una cuenta solo puede ver y operar la ficha a la que quedó vinculada.

Una cuenta que reclama registros anteriores a su creación solo puede vincularse por una de estas vías: un vínculo de cuenta confiable establecido previamente (por ejemplo, una invitación ya aceptada para esa ficha) o una verificación de la dueña de la identidad histórica de quien reclama. La prueba de control actual de un email o teléfono puede iniciar una solicitud, pero nunca es suficiente por sí sola para adjudicar una ficha histórica. Antes de que una de esas vías sea aprobada, el sistema no revela datos, reservas, observaciones ni siquiera la existencia de la ficha candidata.

La dueña puede crear fichas y reservas para personas sin cuenta. La vinculación posterior conserva ese historial únicamente después de la aprobación correspondiente. Un contacto compartido, reciclado o mal cargado nunca autoriza acceso a datos, reservas u observaciones de otra persona.

**Rationale.** Preserva la operación asistida prevista por RF 3 y RF 4, habilita la autogestión confirmada y evita que un identificador de contacto se convierta en autorización para acceder a una identidad histórica.

**Alternativas reales.**

1. **Una sola entidad cliente-cuenta desde el alta.** Simplifica el modelo y puede usar una invitación para que el cliente establezca su propia contraseña; no exige que la dueña la conozca. Aun así, acopla la ficha operativa al ciclo de acceso y dificulta registrar a quien todavía no quiere cuenta.
2. **Vinculación automática por email o teléfono coincidente.** Reduce pasos, pero expone fichas ante direcciones compartidas, números reasignados o errores de carga; no se recomienda.

**Ejemplos de aceptación propuestos.**

- Normal: la dueña crea la ficha de Ana, le envía una invitación y Ana la acepta; ese vínculo confiable le permite ver exclusivamente los dos turnos de su ficha.
- Borde: Ana crea una cuenta nueva y demuestra que hoy controla el email cargado en una ficha anterior. Puede solicitar el vínculo, pero no ve esa ficha hasta que la dueña verifique su identidad histórica y lo apruebe.
- Borde: dos clientes comparten el teléfono familiar; una de ellas crea cuenta. El sistema no le muestra la ficha de la otra ni fusiona historiales por esa coincidencia.
- Borde: una cuenta solicita una ficha con email viejo al que no puede acceder. La solicitud no revela datos y queda pendiente de verificación de identidad histórica por la dueña.

**Subdecisiones pendientes para discusión posterior.**

- Aprobar la separación entre ficha y cuenta sin convertir la prueba de control actual de contacto en prueba de identidad histórica.
- Definir qué evidencia y qué procedimiento usa la dueña para verificar la identidad histórica, incluida la resolución de solicitudes sin contacto confiable previo.
- Definir si la invitación de la dueña es el único modo inicial de establecer un vínculo confiable o si habrá otro flujo con la misma garantía.

---

## D02 — Reprogramación individual, cancelación de ocurrencia y clase suelta

**Evidencia actual.** RF 17 limita la reprogramación a un turno individual, con la misma trabajadora, y ordena cancelar una clase grupal para anotarse en otra con lugar. RF 18 libera solo esa semana cuando se cancela una clase fija. RF 4 distingue clases fijas de clase suelta, pero no explicita cómo operar desde el panel una clase suelta ya anotada ni cómo impedir que una cancelación modifique la recurrencia.

**Recomendación propuesta.** Tratar toda participación fechada como una **ocurrencia** independiente de la regla recurrente:

- El turno individual puede reprogramarse al horario libre de la misma trabajadora, como indica RF 17.
- Una clase fija o suelta solo puede cancelarse para esa fecha. La cancelación libera ese lugar y no borra ni altera la elección fija del abono.
- Para ir a otra clase, el cliente realiza una nueva inscripción a una clase suelta con cupo; no existe “traslado” implícito de la ocurrencia cancelada.
- La clase suelta se cancela igual que cualquier otra ocurrencia grupal y, si fue pagada, aplica D07.

Como **valor propuesto**, establecer un corte configurable de dos horas antes del inicio para cancelación o reprogramación autogestionada. Fuera del corte, la dueña puede registrar la acción y la consecuencia financiera se resuelve según D07.

**Rationale.** Evita que una acción puntual destruya una reserva recurrente, conserva el control real de cupos y no crea recuperaciones de clases de abono, que RF 10, RF 12 y RF 18 excluyen.

**Alternativas reales.**

1. **Trasladar automáticamente una clase fija cancelada a otra clase.** Parece cómodo, pero cambia la reserva fija, puede exceder cupos y contradice la ausencia de recuperación.
2. **Permitir cambiar de trabajadora al reprogramar un turno.** Aumenta opciones, pero contradice el límite expreso de RF 17 y la continuidad con la profesional elegida.

**Ejemplos de aceptación propuestos.**

- Normal: Sofía cancela el martes su clase fija del miércoles; se libera solo el cupo del miércoles y continúa anotada los martes y miércoles siguientes mientras su abono esté vigente.
- Normal: Lucía reprograma su turno individual de las 15:00 a las 17:00 con la misma trabajadora; el horario de las 15:00 vuelve a ofrecerse.
- Borde: Sofía intenta anotarse en otra clase tras cancelar; obtiene lugar solo si la nueva clase tiene cupo en ese momento.
- Borde: una clase suelta pagada se cancela después del corte; se conserva la cancelación y se registra cualquier devolución manual conforme a D07.

**Pregunta posterior.** ¿Se aprueba que las clases solo se cancelen por ocurrencia y que el panel aplique un corte configurable de dos horas como valor inicial propuesto?

**Opciones.** A) Sí, con dos horas propuestas. B) Sí, pero sin corte inicial. C) Permitir traslado explícito entre clases, sujeto a cupo.

---

## D03 — Independencia, vínculo e importe de cobros y asistencias

**Evidencia actual.** RF 5 calcula y registra cobros; RF 10 registra asistencia; RF 11 describe el registro de una atención sin turno y su cobro, sin establecer que puedan registrarse en cualquier orden. La independencia y el orden flexible se proponen aquí, no son una regla expresa del RF vigente. RF 13 usa cobros para el resultado del período. Falta precisar el vínculo cuando existen pagos previos, pagos posteriores, diferencias de importe o correcciones de tarifa.

**Recomendación propuesta.** Mantener **asistencia** y **movimiento financiero** como eventos independientes, pero con vínculo obligatorio cuando ya existe la reserva, atención o abono que les da origen:

- La asistencia expresa si se prestó el servicio; el cobro expresa importe, fecha y concepto percibido. Ninguno cambia por sí solo el estado del otro.
- Si existe una reserva, atención registrada o abono, cada asistencia y movimiento debe enlazarse a ese origen; un abono puede vincularse a varias ocurrencias fijas, sin duplicar cobros. El vínculo solo es opcional cuando no hubo una reserva previa y se registra un motivo claro, como una atención sin turno contemplada por RF 11.
- El importe se captura al acordar la reserva, inscripción o abono; para una atención sin reserva, se captura al acordar esa atención. El pago usa ese importe acordado: un cambio posterior del tarifario no puede aumentarlo silenciosamente. El tarifario solo propone precios para nuevos acuerdos.
- Cada movimiento conserva el importe efectivamente registrado. Todo ajuste posterior exige motivo y responsable, y se registra de modo trazable sin reescribir el movimiento original.
- Una devolución se registra como movimiento manual asociado al cobro original; no se requiere una billetera, saldo interno ni una pasarela de pagos.

**Rationale.** Amplía lo descrito en RF 5 y RF 11 con una independencia propuesta, conserva trazabilidad para RF 13 y permite que una clase suelta se cobre al llegar sin falsear la asistencia, el origen del cobro ni el precio que el cliente aceptó.

**Alternativas reales.**

1. **Un único estado “cobrado/atendido”.** Es más corto, pero no representa pago anticipado, pago posterior ni ausencias con pago.
2. **Recalcular o reemplazar el importe acordado al cambiar el tarifario.** Simplifica informes aparentes, pero altera el acuerdo y el registro histórico.

**Ejemplos de aceptación propuestos.**

- Normal: al inscribir a Lucía en una clase suelta se captura el importe acordado; al cobrarla al llegar y registrar la asistencia, ambos eventos quedan enlazados a esa inscripción y conservan sus momentos distintos.
- Normal: una promoción acuerda $8.000 para una reserva cuando el tarifario vigente sugería $10.000; el cobro posterior usa $8.000 con el motivo “promoción”, aunque luego cambie el tarifario.
- Borde: se registra una atención sin turno; el cobro puede no tener reserva previa, pero conserva el motivo y el vínculo con la atención creada conforme a RF 11.
- Borde: se corrige un cobro duplicado; se registra un reintegro manual enlazado al original, en vez de borrar el ingreso.

**Subdecisiones pendientes para discusión posterior.**

- Ratificar que el vínculo es obligatorio cuando existe reserva, atención o abono, y qué motivos claros habilitan la excepción sin reserva previa.
- Definir en qué acto se confirma el importe acordado para cada concepto y cómo se documentan descuentos o excepciones.
- Definir la mecánica de un ajuste posterior sin alterar el movimiento original.

---

## D04 — Retenciones de cupo para inscripciones pendientes de pago

**Evidencia actual.** RF 4 exige verificar vacante permanente, registra la inscripción pendiente de pago y anota solo la primera clase fija; RF 5 incorpora a listas fijas al cobrar y anula la inscripción si no se paga al llegar a esa primera clase. Falta definir qué reserva la vacante antes del pago, cuándo expira y cómo se resuelve la concurrencia entre pago, vencimiento y otra inscripción.

**Recomendación propuesta.** Crear una retención de cupo fijo en estado **pendiente de pago** para cada clase elegida. La retención consume capacidad fija desde la inscripción hasta el inicio de la primera ocurrencia elegida, que es el límite ya implícito en RF 5. Al registrar el pago antes de que venza, la retención se convierte atómicamente en abono vigente y reserva fija. Si llega el inicio sin pago, vence, se libera y la inscripción queda sin efecto.

La comprobación de cupo y la creación, confirmación o vencimiento de la retención deben ejecutarse como una sola operación lógica: dos solicitudes concurrentes no pueden confirmar la misma vacante. Si un pago llega después de la liberación, no reactiva el cupo: debe volver a comprobar disponibilidad y, si no hay lugar, registrarse o revertirse manualmente según D07.

**Rationale.** Da significado operativo a “cupo permanente” de RF 4 sin reservar indefinidamente ni permitir sobreventa cuando dos personas actúan al mismo tiempo.

**Alternativas reales.**

1. **No retener cupo hasta cobrar.** Evita estados pendientes, pero la promesa de vacante fija puede desaparecer antes del primer pago.
2. **Retención con plazo fijo corto, por ejemplo 15 minutos.** Reduce inmovilización, pero contradice el flujo de pago al llegar previsto en RF 4 y RF 5.

**Ejemplos de aceptación propuestos.**

- Normal: Paula se inscribe a dos clases fijas; sus dos vacantes quedan retenidas y se confirma el abono al pagar en su primera clase.
- Borde: Paula no paga al inicio de esa primera clase; ambas retenciones vencen y sus vacantes quedan disponibles para otra inscripción.
- Borde: dos alumnas intentan tomar la última vacante; solo una retención se confirma y la otra recibe “Cupo permanente no disponible”.
- Borde: un pago se registra justo cuando vence la retención; el orden único de las operaciones decide el resultado y deja una traza sin duplicar cupo.

**Pregunta posterior.** ¿Se aprueba retener cada cupo fijo hasta el inicio de la primera clase y convertirlo atómicamente al registrar el pago?

**Opciones.** A) Sí. B) Retener solo hasta un plazo corto. C) No retener hasta cobrar.

---

## D05 — Matriz de notificaciones y significado de cada mensaje

**Evidencia actual.** RF 6 envía recordatorio por email 24 horas antes para turnos y clases; RF 7 pide confirmar asistencia; RF 12 prepara, pero no envía, avisos de suspensión. La intención confirmada requiere que el cliente reciba avisos de cancelación de clase y vencimiento de abono, y que la confirmación de asistencia aplique solo a turnos individuales. También expresa preferencia por no recibir recordatorios de clases recurrentes de abono.

**Recomendación propuesta.** Aprobar una matriz separando evento, destinatario, canal, momento y efecto. El email es el canal inicial **propuesto** porque RF 1 requiere email y RF 6 lo usa; no se asume aprobado ni se define proveedor.

| Evento | Destinatario | Canal y momento propuestos | Efecto |
|---|---|---|---|
| Recordatorio de turno individual | Cliente | Email; 24 h antes, valor RF 6 | Informa el turno. |
| Solicitud de confirmación | Cliente de turno individual | Email; junto al recordatorio | Confirma o cancela la ocurrencia. |
| Recordatorio de clase suelta | Cliente | Email; 24 h antes | Solo informa; no pide asistencia. |
| Clase fija de abono | Cliente | No enviar recordatorio recurrente | Respeta la preferencia actual; queda pendiente de ratificación. |
| Clase suspendida | Cada alumno anotado | Email inmediato tras suspender | Notifica la suspensión; RF 12 hoy solo deja el mensaje preparado. |
| Vencimiento próximo de abono | Alumno | Email; 7 y 1 días antes, valores propuestos | Invita a renovar, sin mantener cupo tras vencer. |
| Abono vencido | Alumno | Email el día del vencimiento | Comunica la liberación del cupo fijo. |

Un recordatorio no es una solicitud de asistencia; una solicitud de asistencia no es asistencia real; y una clase “suspendida” se diferencia de un mensaje “preparado” o “enviado”. Cada emisión debe dejar estado suficiente para evitar duplicados y permitir reintento controlado.

**Rationale.** Elimina la ambigüedad entre RF 6, RF 7 y RF 12, aplica la intención confirmada sin convertir una preferencia en regla aprobada y evita una arquitectura de mensajería fuera de alcance.

**Alternativas reales.**

1. **Enviar recordatorio a todas las clases fijas.** Aumenta cobertura, pero contradice la preferencia manifestada y genera ruido semanal.
2. **Conservar solo mensajes preparados por la dueña.** Reduce automatización, pero no cumple la intención de que el cliente reciba avisos de suspensión y vencimiento.

**Ejemplos de aceptación propuestos.**

- Normal: el día anterior, Mario recibe un único correo que recuerda y solicita confirmar su turno individual; al confirmar, se actualiza su ocurrencia, no su asistencia.
- Normal: una clase fija se suspende; cada persona anotada recibe un aviso enviado y la grilla semanal permanece intacta.
- Borde: un alumno con abono no recibe recordatorio semanal, pero sí los avisos de proximidad y vencimiento de su abono.
- Borde: un reintento técnico no manda dos correos de suspensión al mismo alumno para la misma clase.

**Pregunta posterior.** ¿Se aprueba esta matriz, con email como canal inicial y sin recordatorios recurrentes para clases fijas?

**Opciones.** A) Sí, tal cual. B) Sí, pero incluir recordatorio de clase fija. C) Mantener avisos de suspensión solo preparados para envío manual.

---

## D06 — Vencimiento, renovación, liberación y generación semanal

**Evidencia actual.** RF 5 cubre un mes desde el pago; RF 19 genera alumnos fijos solo con abono vigente y libera el lugar si no renovaron. RF 4 y RF 5 introducen inscripciones pendientes; no determinan cómo se coordinan vencimiento, aviso, una renovación tardía y semanas ya generadas. Tampoco precisan la semántica de “un mes” en límites de fecha, horario o meses de distinta duración.

**Recomendación propuesta.** Mantener una única fuente de verdad: el abono tiene inicio, vencimiento calculado bajo una semántica aún pendiente y estado. Al vencer, el cupo fijo queda disponible de inmediato. Las anotaciones y ocurrencias ya generadas se conservan como registros históricos: no se borran para liberar capacidad.

La renovación anticipada empieza al vencimiento existente, no en la fecha del nuevo pago, y extiende la cobertura sin duplicar anotaciones. La renovación tardía debe volver a validar cada cupo fijo antes de crear cobertura; no recupera automáticamente un lugar ya liberado. Una vez superada esa comprobación, comienza en la fecha de acuerdo o de pago que se defina para ese caso, no retroactivamente.

Como **valores propuestos**, enviar avisos a 7 y 1 días y el mismo día del vencimiento; cualquier “gracia” posterior solo puede facilitar el pago administrativo, nunca conservar cupo ni generar clases sin abono vigente. La generación semanal debe ser idempotente: al repetirse, conserva las cancelaciones explícitas de clientes y las clases suspendidas, nunca las recrea como anotaciones activas ni las transforma en ausencias. También conserva los demás registros históricos y genera solo las ocurrencias futuras que correspondan al estado vigente.

**Rationale.** Hace compatibles RF 5 y RF 19 con D04: la retención protege una inscripción inicial hasta su primera clase, mientras que un abono vencido libera lugares futuros y una renovación tardía compite de nuevo por disponibilidad, sin perder la traza de lo ya sucedido.

**Alternativas reales.**

1. **Período de gracia que conserva el lugar.** Favorece continuidad, pero reduce transparencia de cupos y puede impedir nuevas inscripciones.
2. **Regenerar reemplazando las listas ya creadas.** Simplifica la agenda inicial, pero puede borrar el historial o recrear cancelaciones y suspensiones como si no hubieran ocurrido.

**Ejemplos de aceptación propuestos.**

- Normal: el abono vence el 15; si se renueva el 14, la nueva cobertura comienza el 15 al vencer la existente y las clases posteriores mantienen la reserva fija sin duplicarse.
- Borde: vence el 15 y la semana siguiente ya fue generada; las ocurrencias posteriores dejan de ocupar cupo, pero se conservan en el historial con su estado, sin eliminarlas.
- Borde: una clienta canceló expresamente su clase del miércoles y luego se repite la generación semanal; esa cancelación se conserva y no reaparece como anotación activa.
- Borde: una clase fue suspendida; una repetición de la generación conserva la suspensión y no la convierte en una ausencia de cada alumno.
- Borde: el alumno paga el 17 y su lugar fue tomado el 16; primero se vuelve a validar capacidad y, si no hay vacante, el pago no desplaza a la nueva persona y requiere elegir vacante o resolver el dinero manualmente.

**Subdecisiones pendientes para discusión posterior.**

- Ratificar que la renovación anticipada comienza al vencimiento existente y que la tardía exige revalidar capacidad antes de empezar una nueva cobertura.
- Definir para la renovación tardía si el inicio es la fecha del acuerdo, la del pago u otra fecha expresamente acordada, una vez aprobada la capacidad.
- Definir la semántica de “un mes”, incluidos cierres de mes, fecha y hora de vencimiento y cualquier zona horaria aplicable; esos límites no quedan resueltos por esta propuesta.
- Ratificar que la regeneración conserva historial, cancelaciones explícitas y suspensiones, sin borrarlos ni recrearlos.

---

## D07 — Cancelaciones, pagos, reintegros y ausencias

**Evidencia actual.** RF 10 indica que una falta con abono pierde la clase; RF 12 establece que una clase suspendida no se recupera ni devuelve dinero y que la clase suelta se paga al llegar; RF 17 y RF 18 liberan turnos o lugares, pero no detallan consecuencias de una atención o clase suelta ya pagada. RF 5 registra pagos, sin política de reintegro.

**Recomendación propuesta.** Separar el estado de la ocurrencia, el **derecho** financiero que nace de una cancelación y la **ejecución manual** de un reintegro o traslado. La propuesta de política es:

- **Cancelación o reprogramación oportuna del cliente:** dentro del corte de dos horas propuesto en D02, una reserva ya cobrada tiene derecho a reintegro total. El reintegro se ejecuta manualmente y queda registrado, no se acredita automáticamente.
- **Cancelación tardía o no-show del cliente:** no tiene derecho a reintegro. Una excepción exige motivo, responsable y ejecución manual registrada.
- **Cancelación por el negocio:** una reserva ya cobrada tiene derecho a reintegro total o a un traslado que el cliente acepte expresamente. La alternativa elegida se ejecuta y registra manualmente. Para una clase suelta suspendida antes de su inicio no hay cobro que devolver, porque RF 12 indica que se paga al llegar.
- **Abono:** RF 10 y RF 12 ya excluyen recuperación o devolución ante falta y ante suspensión de una clase fija. Se propone mantener esas exclusiones, pero su ratificación como política de este documento permanece pendiente.
- **Traslado de un pago:** no crea saldo general ni billetera. Es una relación explícita entre el cobro original y la nueva reserva o concepto, por el importe acordado.

El corte de D02 determina si la cancelación del cliente es oportuna; no ejecuta por sí mismo un reintegro. Todo reintegro o traslado aceptado afecta el resultado de RF 13 como movimiento negativo o de reversión claramente relacionado.

**Rationale.** Evita confundir el derecho del cliente con el paso operativo de devolver el dinero, conserva trazabilidad y no permite que una cancelación borre o falsifique movimientos financieros. También respeta las exclusiones actuales de los abonos mientras se las lleva a ratificación explícita.

**Alternativas reales.**

1. **Crédito automático para toda cancelación.** Es cómodo, pero crea una billetera y reglas de vencimiento, consumo y reporte fuera de alcance.
2. **Borrar el cobro cuando se cancela.** Es simple en apariencia, pero destruye auditoría e impide explicar el resultado del período.

**Ejemplos de aceptación propuestos.**

- Normal: una clienta cancela un turno pagado con más de dos horas de anticipación; el turno se libera, nace el derecho al reintegro total y la dueña registra manualmente el reintegro vinculado al cobro.
- Normal: el negocio cancela un turno ya cobrado; la clienta acepta trasladarlo a otra reserva y la dueña registra ese traslado. Si no acepta, se ejecuta el reintegro total manual.
- Borde: una alumna cancela una clase suelta pagada después del corte; el lugar se libera, pero no hay reintegro salvo excepción manual motivada.
- Borde: se suspende una clase fija de abono; la ocurrencia queda suspendida, no se marca a ningún alumno como ausente y no se crea reintegro ni crédito mientras siga vigente la exclusión de RF 12.
- Borde: una clienta no se presenta a un turno pagado; queda ausente y no tiene reintegro, salvo una excepción manual motivada.

**Subdecisiones pendientes para discusión posterior.**

- Ratificar la política de derecho a reintegro: total para cancelación oportuna del cliente; sin reintegro para cancelación tardía o no-show, salvo excepción motivada.
- Ratificar que una cancelación del negocio habilita reintegro total o traslado expresamente aceptado por el cliente.
- Ratificar o modificar las exclusiones de recuperación y devolución de abonos ya previstas en RF 10 y RF 12.
- Definir el procedimiento y responsables para ejecutar manualmente reintegros, excepciones y traslados después de que se determine el derecho.

---

## D08 — Capacidad por trabajadora, segmento, puesto, negocio y servicio

**Evidencia actual.** RF 2 configura duración y cupo máximo; RF 3 valida huecos de la trabajadora; RF 8 registra horarios de trabajadoras; RF 9 valida disponibilidad de profesora; RF 11 exige un puesto disponible para atención sin turno; RF 4 y RF 19 controlan cupos de clases. Los RF no explicitan cómo combinar esas restricciones cuando hay turnos simultáneos, segmentos de servicio, puestos físicos o carga manual.

**Recomendación propuesta.** Evaluar la disponibilidad como la intersección de restricciones aplicables, antes de confirmar una reserva o inscripción:

1. **Servicio/clase:** no superar el cupo máximo de esa ocurrencia.
2. **Trabajadora:** respetar jornada, pausas y los segmentos que efectivamente requieren a esa persona; el tiempo de espera no consume su disponibilidad si el servicio lo declara así.
3. **Puesto o estación:** consumir una unidad física durante los segmentos en que el cliente ocupa el lugar. Configurar solo una cantidad de puestos, no una asignación nominal, salvo que el negocio necesite distinguir recursos incompatibles.
4. **Negocio:** no exceder la capacidad simultánea global durante los segmentos donde hay clientes presentes, incluso si la trabajadora está en espera.

Una reserva del cliente o cargada por la dueña aplica estas validaciones de forma atómica. Para una atención sin turno, RF 11 conserva el rechazo si no hay puesto; si se habilita una excepción operativa futura, debe quedar marcada como conflicto y no alterar silenciosamente la capacidad. Las clases compiten por su cupo y, cuando compartan espacio o profesora, también por las restricciones de estación y negocio.

**Rationale.** Unifica la disponibilidad de RF 3 con los cupos de RF 4/RF 19 y el puesto de RF 11, sin modelar una asignación de silla, sala o arquitectura de recursos más compleja de la necesaria.

**Alternativas reales.**

1. **Controlar solo la agenda de la trabajadora.** Es insuficiente cuando falta espacio físico o una clase comparte recursos.
2. **Asignar una estación identificada a cada turno.** Da detalle, pero agrega operación diaria y no es necesaria si basta con contar puestos.

**Ejemplos de aceptación propuestos.**

- Normal: dos servicios de distintas trabajadoras se superponen; se confirman si cada una está disponible, hay puestos y no se supera la capacidad global.
- Normal: durante el segmento de espera de una coloración, la trabajadora puede tomar otro servicio, pero el puesto y la capacidad global siguen ocupados por la primera clienta.
- Borde: la última vacante de una clase se reserva concurrentemente; solo una inscripción se confirma.
- Borde: llega una persona sin turno y todos los puestos están ocupados; RF 11 rechaza el registro y ofrece volver más tarde o reservar.

**Pregunta posterior.** ¿Se aprueba este control por capas, usando cantidad de puestos y capacidad global sin asignar una estación nominal a cada turno?

**Opciones.** A) Sí. B) Controlar solo trabajadora y cupo del servicio. C) Asignar estaciones identificadas.

---

## Checklist de discusión ordenado

1. [ ] D01: validar la separación ficha–cuenta y las vías seguras para reclamar identidad histórica, sin usar solo el control actual de un contacto.
2. [ ] D04: decidir primero la retención pendiente, porque condiciona cupos y renovación.
3. [ ] D06: acordar inicio de renovación anticipada y tardía, revalidación de capacidad y semántica de los límites mensuales, sin gracia de capacidad.
4. [ ] D02: confirmar qué acciones puede hacer el cliente sobre cada ocurrencia y el corte propuesto.
5. [ ] D07: definir el derecho a reintegro o traslado y, por separado, su ejecución manual registrada.
6. [ ] D03: confirmar vínculos obligatorios cuando existe reserva o abono, importe acordado, ajustes y reintegros.
7. [ ] D08: validar las restricciones de capacidad que se aplican a reservas y atenciones sin turno.
8. [ ] D05: cerrar la matriz de notificaciones con los demás estados ya definidos.

Nada de este documento actualiza los RF vigentes hasta que el usuario acepte expresamente las decisiones correspondientes.
