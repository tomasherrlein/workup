# Propuestas de resolución para brechas de requerimientos

## Estado, alcance y línea de base

**Estado:** documento de propuesta; D01 está **Aprobada para el enlace privado de turnos individuales** y D02 está **Parcialmente aprobada para la cancelación de turnos individuales y de clases con abono**. D03 tiene **Aprobado el núcleo de cobros, asistencia y precio acordado**. D05 tiene **Aprobadas las notificaciones de turnos, clases y vencimiento de abonos**, incluido el caso de inscripción tardía a clase suelta. Las demás reglas de D02, los detalles de ajustes de D03 y las decisiones D07–D08 permanecen **Pendientes**. D06 tiene **Aprobado el ciclo de abono y la resolución manual del vencimiento**, con detalles de generación semanal pendientes. D04 está **Aprobada con liberación manual de cupos**.

**Contexto:** WorkUp es un caso ficticio. Las decisiones se acuerdan para el proyecto con criterios realistas y simples; la dueña es un rol del sistema, no una entrevistada real.

**Alcance:** resolver coherentemente ocho ambigüedades observadas en los RF vigentes. No modifica los RF, no define una implementación técnica, no incorpora pasarela de pagos ni obliga a integrar SMS o WhatsApp.

**Línea de base:** la fuente normativa para estas propuestas es `Requerimientos Funcionales WorkUp.md` (RF 1–20). `srs.md` se usa solo como antecedente cuando ayuda a explicar una diferencia. En particular, su decisión de no tener cuenta de cliente está superada por los RF 1, 15–18 vigentes y por la intención confirmada de autogestión del cliente.

> Los plazos y horarios que aparecen como **valor propuesto** no son reglas aprobadas. Solo entrarían a los RF luego de una aceptación explícita.

## Resumen de decisiones a conversar

| ID | Tema | Recomendación resumida | Estado |
|---|---|---|---|
| D01 | Acceso a un turno, cuenta opcional y vinculación | Sin cuenta obligatoria: la persona puede reservar o asistir con ayuda de la dueña; un enlace privado, reutilizable y acotado permite gestionar un único turno durante su vigencia aprobada. La cuenta y cualquier asociación histórica siguen siendo opcionales o pendientes según corresponda. | Aprobada para el enlace de turnos individuales |
| D02 | Reprogramación y cancelación | Para un turno individual, el enlace permite cancelar directamente antes del inicio, sin plazo mínimo ni aprobación de la dueña; una notificación posterior no altera asistencia ni estados completados. En clases con abono se libera solo la fecha cancelada antes del inicio, conservando horarios fijos y sin recuperación ni devolución automáticas. Reprogramación y clases sueltas siguen pendientes. | Parcialmente aprobada |
| D03 | Cobros y asistencia | Cobro y asistencia independientes; cada cobro identifica el concepto concreto; se respeta el precio acordado y el abono no se cobra por cada clase. | Núcleo aprobado; ajustes pendientes |
| D04 | Inscripción pendiente y cupos | La inscripción pendiente ocupa los lugares fijos; el cobro activa el abono. Si no paga o no se presenta, la dueña anula la inscripción y libera los lugares, sin vencimiento automático al inicio. | Aprobada |
| D05 | Notificaciones | Al reservar un turno individual se envía inmediatamente un email con sus detalles y el enlace privado; 24 horas antes se envía un recordatorio con ese mismo enlace, sujeto a las reglas aprobadas de confirmación y deduplicación. La dueña puede compartir manualmente el enlace vigente. Clases con abono sin recordatorios ni confirmación; clases sueltas con recordatorio por email 24 h antes; suspensión con email inmediato a los anotados. Abonos: email 3 días antes y al vencer si no renovó, sin insistencia diaria ni liberación automática. | Reglas aprobadas |
| D06 | Vencimiento y renovación | Un mes desde el alta del abono; renovación anticipada desde el vencimiento y tardía desde el pago. El lugar se conserva hasta resolución manual, pero no habilita asistencia sin renovar. | Núcleo aprobado; detalles pendientes |
| D07 | Consecuencias financieras | Mantener el registro trazable de cobros; las políticas de depósitos, cobro, reintegro, traslado y penalidades quedan pendientes y fuera de WorkUp, a cargo de la dueña. | Pendiente |
| D08 | Capacidades y solapamientos | Validar una combinación de capacidad de servicio, trabajadora, puesto y negocio antes de reservar. | Pendiente |

---

## D01 — Acceso a un turno, cuenta opcional y vinculación histórica

**Estado:** **Aprobada para el enlace privado de turnos individuales**. La asociación histórica de cuentas y los detalles técnicos de implementación permanecen pendientes.

**Evidencia actual.** RF 1 incorpora contraseña al alta de cliente y controla la unicidad del email. RF 3 permite que el cliente reserve desde un enlace enviado por el negocio o que la dueña cargue el turno cuando la persona le escribe. RF 4 y RF 5 buscan al cliente por email y prevén darlo de alta si es la primera vez. RF 15–18 describen un panel con sesión iniciada y sus acciones. Los RF vigentes no precisan el alcance de un enlace ni separan expresamente ficha operativa, cuenta y asociación histórica.

### Requisitos de negocio aceptados

- **No hay cuenta obligatoria para reservar ni para asistir.** La dueña puede registrar la ficha y reservar o inscribir a una persona sin contraseña ni cuenta. Para clientes mayores o de baja familiaridad tecnológica, la alternativa es llamar, escribir o concurrir al negocio para que la dueña los asista; el enlace no es requisito de asistencia.
- Al reservar cada **turno individual**, se envía inmediatamente por email un **enlace privado** para esa única reserva o turno exacto, junto con los detalles del turno. El enlace ofrece una acción visible, **“Ver mi turno”**, que muestra su fecha y horario. La dueña puede copiar y compartir manualmente el enlace vigente por el canal que elija, por ejemplo WhatsApp; no se aprueba una integración automática ni el uso obligatorio de WhatsApp.
- Para un **turno individual antes de su inicio**, la persona puede confirmar su intención de asistir o cancelarlo directamente desde el enlace, sin contraseña, plazo mínimo ni aprobación de la dueña. La confirmación no registra asistencia real y la falta de respuesta conserva la reserva; no genera una cancelación automática. La cancelación libera el horario. Estas reglas no se aplican a confirmaciones de clases recurrentes.
- Desde el inicio del turno en adelante, el enlace solo permite **ver el turno y notificar** la situación. La dueña evalúa y registra una ausencia cuando corresponda; esa notificación no genera una ausencia automática ni sobrescribe un turno ya atendido o completado. No permite cancelar ni reprogramar retroactivamente.
- El enlace vence 24 horas después del **inicio** del turno, no de su fin ni de su creación. Puede abrirse y reutilizarse varias veces durante esa vigencia: verlo no lo consume. Tras el vencimiento, la persona debe contactar al negocio. Si la dueña sospecha que fue comprometido o enviado al destinatario equivocado, puede revocarlo y emitir un reemplazo; el anterior deja de autorizar acceso y el reemplazo conserva la fecha de vencimiento ya acordada, sin extenderla silenciosamente.
- El enlace no habilita reprogramación, no muestra historial, clases, abonos, observaciones ni otros datos sensibles, y no vincula una cuenta con fichas existentes. Un turno posterior se registra como una nueva reserva; nunca reprograma una ocurrencia pasada.
- El panel de cliente autenticado existente y el autoregistro de clientes nuevos permanecen **opcionales**. Su uso no reemplaza las alternativas de atención por la dueña.

La cancelación directa por enlace antes del inicio de un turno individual está aceptada y se detalla también en **D02**. La cancelación de clases con abono también está aprobada en D02, sin ampliar a clases el enlace individual. La reprogramación autenticada y las reglas de clases sueltas siguen pendientes. Las notificaciones de clases grupales se especifican por separado en **D05**, sin extenderles el enlace individual.

### Seguridad e implementación no aprobadas

El enlace es un mecanismo de posesión: si se entrega al destinatario equivocado o se reenvía, quien lo posea puede usar las acciones acotadas aprobadas. La revocación por la dueña limita ese acceso desde el momento en que se aplica, pero no convierte al enlace en prueba de identidad.

La entrega inmediata, vigencia, reutilización y revocación son reglas de negocio aprobadas. Quedan pendientes únicamente los mecanismos técnicos para generar, almacenar, validar, revocar y auditar el enlace y para enviar los emails; deben preservar las reglas aprobadas. No se aprueba una arquitectura, modelo de datos, API, proveedor de mensajería ni política de autenticación para este flujo.

La recomendación previa de vincular automáticamente una ficha histórica al verificar un email queda **sustituida** por este enfoque; no es una política aceptada ni recomendada. La asociación de una cuenta futura con una ficha histórica sigue siendo una decisión separada y pendiente. En particular, esta decisión no aprueba de forma implícita ningún procedimiento manual de asociación.

### Alcance para una futura actualización de RF (no normativa)

- **Actores:** dueña; persona sin cuenta; cliente autenticado opcional.
- **Flujo aceptado:** la dueña registra la ficha y reserva o inscripción sin credenciales; al reservar cada turno individual el sistema envía inmediatamente por email sus detalles y el enlace “Ver mi turno”, que la dueña también puede copiar y compartir manualmente mientras esté vigente. La persona lo usa para consultar fecha y horario, declarar intención de asistir o, antes de su inicio, cancelarlo directamente. La falta de respuesta conserva la reserva y la confirmación no registra asistencia. Desde el inicio puede ver el turno y notificar; la dueña registra una ausencia solo si corresponde.
- **Límites aceptados:** el enlace queda limitado a la reserva exacta, es reutilizable hasta 24 horas después de su inicio y no expone información adicional ni permite reprogramar o asociar cuentas. Una ocurrencia pasada no se reprograma: un turno posterior es una nueva reserva. La revocación por la dueña invalida el enlace anterior sin ampliar la vigencia acordada del reemplazo.
- **Fuera de este alcance:** vinculación histórica de cuentas, reglas concretas de cancelación autenticada y reprogramación antes del inicio, y canales de cancelación o notificación para clases grupales; la regla de cancelación con abono se registra en D02.

### Ejemplos de aceptación

- Normal: Ana llama al negocio; la dueña registra su ficha y turno sin crear una cuenta. Ana puede asistir aunque no reciba ni use un enlace.
- Normal: al reservar el turno individual de Bruno, el sistema le envía inmediatamente por email los detalles y un enlace. La dueña puede copiar ese mismo enlace vigente y compartirlo manualmente por WhatsApp si lo elige. Bruno elige “Ver mi turno”, ve fecha y horario, y confirma su intención de asistir sin contraseña; si no responde, su reserva se conserva.
- Borde: Carla cancela su turno individual desde el enlace un minuto antes de su inicio. La cancelación es directa y libera el horario, sin aprobación de la dueña.
- Borde: al iniciarse el turno, Carla puede notificar mediante el enlace. La dueña decide si corresponde registrar una ausencia; el aviso no sobrescribe una asistencia o un turno completado.
- Borde: si Carla quiere otro horario después del inicio, se crea una reserva nueva; no se reprograma la ocurrencia pasada.
- Borde: un enlace compartido manualmente permite solo consultar, declarar intención de asistir, cancelar antes del inicio o notificar desde el inicio para el turno exacto al que se limita; no permite ver el historial de la persona ni entrar a su panel.
- Borde: el enlace de Carla se abre varias veces y sigue funcionando hasta 24 horas después del inicio de su turno. Después vence y debe contactar al negocio.
- Borde: la dueña revoca un enlace comprometido y emite otro para el mismo turno. El enlace anterior deja de funcionar; el nuevo vence en la misma fecha y hora acordadas para el turno original.
- Borde: Diego crea una cuenta opcional en el futuro. Esa creación no le da acceso automático a una ficha histórica aunque coincida un email; la asociación histórica requiere una decisión posterior separada.

**Nota de implementación no normativa.** El stack informado es C# con ASP.NET MVC, sin Entity Framework; todavía no está confirmado si será ASP.NET Core o ASP.NET MVC clásico. La persistencia SQL podría resolverse con ADO.NET o, si se permite, Dapper. Para acceso pueden evaluarse stores personalizados de Identity o autenticación por cookies del framework con servicios de registro; no se elige arquitectura en esta propuesta. Las contraseñas deben usar un mecanismo de hash de contraseñas revisado y mantenido, nunca criptografía implementada a mano. Esta nota no prescribe tablas, API ni código.

### Decisiones pendientes para discusión posterior

- Definir la presentación y el tratamiento operativo de la notificación posterior al inicio, sin convertirla en una ausencia automática ni alterar registros atendidos o completados.
- Definir, por separado, si habrá asociación de una cuenta con una ficha histórica y bajo qué criterio; no está aprobado ni el enlace ni un procedimiento manual para realizarla.
- Confirmar el alcance y el flujo del autoregistro opcional de clientes nuevos, incluida la recuperación de acceso, sin volverlo requisito para reservar o asistir.

---

## D02 — Reprogramación individual, cancelación de ocurrencia y clase suelta

**Estado:** **Parcialmente aprobada para la cancelación de turnos individuales y de clases con abono**.

**Evidencia actual.** RF 17 limita la reprogramación a un turno individual, con la misma trabajadora, y ordena cancelar una clase grupal para anotarse en otra con lugar. RF 18 libera solo esa semana cuando se cancela una clase fija. RF 4 distingue clases fijas de clase suelta, pero no explicita cómo operar desde el panel una clase suelta ya anotada ni cómo impedir que una cancelación modifique la recurrencia.

### Regla aceptada: turno individual por enlace

- Antes de su inicio, la persona puede cancelar directamente su turno individual desde el enlace privado, sin plazo mínimo ni aprobación de la dueña. La cancelación libera el horario.
- Desde el inicio en adelante, el enlace permite notificar la situación. La dueña evalúa si registra una ausencia; la notificación no crea una ausencia automática ni sobrescribe una asistencia o un turno atendido o completado.
- Un turno posterior se registra como una reserva nueva. Una ocurrencia pasada nunca se reprograma.
- El enlace no habilita reprogramación. Esta decisión no aprueba ni infiere una ventana temporal para la reprogramación autenticada antes del inicio.

### Regla aceptada: cancelación de una clase con abono

- El alumno puede cancelar hasta antes del inicio de la clase, sin anticipación mínima. Se libera únicamente su lugar en esa fecha.
- Los días y horarios fijos del abono se conservan. Cambiarlos constituye una operación distinta, todavía pendiente de definir.
- La cancelación no genera recuperación ni devolución automáticas.
- Desde el inicio, la dueña registra la ausencia cuando corresponda; el aviso no modifica automáticamente la asistencia ni sobrescribe una asistencia registrada.
- Esta decisión no extiende a clases el enlace privado de turnos individuales ni define un nuevo canal de acceso.

### Reglas pendientes

- La reprogramación autenticada de turnos individuales, incluidas sus restricciones antes del inicio, sigue pendiente.
- La cancelación de clases sueltas y el cambio de horario fijo del abono siguen pendientes.
- El alta en una clase posterior y cualquier traslado entre clases siguen pendientes; no se aprueba una reprogramación por enlace.

**Rationale.** La cancelación individual aprobada libera el horario sin demorar la decisión en la dueña. La separación posterior entre notificación y registro preserva la trazabilidad de asistencia y evita alterar registros ya atendidos o completados. En clases con abono, separar la ocurrencia de la recurrencia permite liberar un lugar puntual sin modificar los horarios fijos.

**Ejemplos de aceptación.**

- Normal: Lucía cancela su turno individual desde el enlace a cualquier momento antes de las 15:00; el horario queda disponible sin intervención de la dueña.
- Borde: a las 15:00 o después, Lucía notifica que no asistirá. La dueña puede registrar una ausencia si corresponde, pero la notificación no modifica automáticamente el registro.
- Borde: Lucía ya figura como atendida; una notificación posterior no reemplaza ese estado por ausencia.
- Borde: Lucía pide otro horario después de iniciado su turno; se crea una nueva reserva y no se reprograma la ocurrencia pasada.

- Normal: Sofía cancela la clase de su abono un minuto antes del inicio; se libera solo ese lugar y conserva los días y horarios de las semanas siguientes, sin recuperación ni devolución automáticas.
- Borde: Sofía avisa después del inicio; la dueña registra la ausencia si corresponde, sin cambiar la recurrencia ni sobrescribir una asistencia previa.

**Discusión pendiente.** Definir reprogramación autenticada, cambio de horario fijo y clases sueltas, sin reabrir la cancelación con abono ya aprobada.

---

## D03 — Independencia, vínculo e importe de cobros y asistencias

**Estado:** **Núcleo aprobado; ajustes pendientes**.

**Reglas aceptadas en la conversación.**

- Cobro y asistencia se mantienen como información independiente: se puede pagar antes o después de asistir. Registrar únicamente uno no modifica automáticamente el otro; el flujo habitual permite cargar ambos juntos, sin exigir dos operaciones.
- Para un turno individual o una clase suelta, la acción **“Finalizar atención”** presenta la asistencia **“Asistió”**, el concepto y el importe acordado, con la opción **“Registrar cobro ahora”**. Al guardar, registra asistencia y cobro si se eligió cobrar, o solo asistencia si no se eligió.
- Si la atención ya estaba pagada, muestra el pago existente y no genera otro cobro al finalizarla. Se conserva la posibilidad de registrar solo un cobro, sin marcar asistencia.
- Para clases con abono se registra la asistencia por clase; el cobro se carga por separado una vez por período, no por cada asistencia.
- Cada cobro identifica qué se paga y el turno, atención sin turno, clase suelta o abono concreto al que corresponde. No basta con un importe y el nombre del cliente.
- El cobro de un abono corresponde a su período; no se duplica por cada clase cubierta.
- Se respeta el precio acordado al reservar o contratar el abono. Las nuevas tarifas se aplican a acuerdos posteriores, no retroactivamente. Para una atención sin reserva, se conserva el importe acordado para esa atención.

**Evidencia actual.** RF 5 calcula y registra cobros; RF 10 registra asistencia; RF 11 describe el registro de una atención sin turno y su cobro, sin establecer que puedan registrarse en cualquier orden. La independencia y el orden flexible se aprobaron en esta conversación, pero todavía no son una regla expresa del RF vigente. RF 13 usa cobros para el resultado del período. Falta precisar el vínculo cuando existen pagos previos, pagos posteriores, diferencias de importe o correcciones de tarifa.

**Detalle aprobado y propuestas adicionales.** Los tres primeros puntos desarrollan el núcleo acordado; las políticas de ajustes y devoluciones de los dos últimos siguen pendientes:

- La asistencia expresa si se prestó el servicio; el cobro expresa importe, fecha y concepto percibido. Ninguno cambia por sí solo el estado del otro.
- Si existe una reserva, atención registrada o abono, cada asistencia y movimiento debe enlazarse a ese origen; un abono puede vincularse a varias ocurrencias fijas, sin duplicar cobros. Una atención sin reserva también debe identificarse en el cobro: no tener reserva previa no exime de indicar el concepto concreto.
- El importe se captura al acordar la reserva, inscripción o abono; para una atención sin reserva, se captura al acordar esa atención. El pago usa ese importe acordado: un cambio posterior del tarifario no puede aumentarlo silenciosamente. El tarifario solo propone precios para nuevos acuerdos.
- **Pendiente de aprobación:** conservar el importe efectivamente registrado y exigir motivo y responsable para ajustes posteriores, sin reescribir el movimiento original.
- **Pendiente de aprobación:** registrar una devolución manual asociada al cobro original. No se aprueba aquí una billetera, saldo interno ni pasarela de pagos; el alcance de reintegros se resolverá junto con D07.

**Rationale.** Amplía lo descrito en RF 5 y RF 11 con una independencia propuesta, conserva trazabilidad para RF 13 y permite que una clase suelta se cobre al llegar sin falsear la asistencia, el origen del cobro ni el precio que el cliente aceptó.

**Alternativas reales.**

1. **Un único estado “cobrado/atendido”.** Es más corto, pero no representa pago anticipado, pago posterior ni ausencias con pago.
2. **Recalcular o reemplazar el importe acordado al cambiar el tarifario.** Simplifica informes aparentes, pero altera el acuerdo y el registro histórico.

**Ejemplos de aceptación propuestos.**

- Normal: Lucía finaliza un turno individual y paga el precio acordado. La dueña usa “Finalizar atención” con “Registrar cobro ahora” y guarda una sola vez; quedan registrados la asistencia y el cobro, como datos independientes vinculados a ese turno.
- Borde: Lucía asiste pero paga después. La dueña guarda solo la asistencia y posteriormente registra el cobro sin repetirla.
- Borde: la clase suelta ya estaba pagada. Al finalizarla se muestra el pago previo y solo se registra la asistencia, sin duplicar el ingreso.
- Normal: se registra la asistencia a una clase con abono; no se genera un cobro adicional por esa clase.
- Normal: una promoción acuerda $8.000 para una reserva cuando el tarifario vigente sugería $10.000; el cobro posterior usa $8.000 con el motivo “promoción”, aunque luego cambie el tarifario.
- Borde: se registra una atención sin turno; el cobro puede no tener reserva previa, pero conserva el motivo y el vínculo con la atención creada conforme a RF 11.
- Borde: se corrige un cobro duplicado; se registra un reintegro manual enlazado al original, en vez de borrar el ingreso.

**Subdecisiones pendientes para discusión posterior.**

- Definir cómo se documentan descuentos o excepciones, sin alterar la regla aprobada de respetar el precio pactado.
- Definir la mecánica de un ajuste posterior sin alterar el movimiento original.

---

## D04 — Retenciones de cupo para inscripciones pendientes de pago

**Evidencia actual.** RF 4 exige verificar vacante permanente, registra la inscripción pendiente de pago y anota solo la primera clase fija; RF 5 incorpora a listas fijas al cobrar y anula la inscripción si no se paga al llegar a esa primera clase. Falta definir qué reserva la vacante antes del pago, cuándo expira y cómo se resuelve la concurrencia entre pago, vencimiento y otra inscripción.

**Estado:** **Aprobada con liberación manual de cupos**.

**Flujo aprobado.**

1. El alumno elige días y horarios fijos con vacante.
2. La inscripción queda **pendiente de pago** y los lugares elegidos ya cuentan como ocupados; no se ofrecen simultáneamente a otro alumno.
3. El alumno paga al llegar a su primera clase.
4. La dueña registra el cobro y activa el abono, conservando los lugares retenidos sin ocuparlos por duplicado.
5. Si el alumno no paga o no se presenta, la dueña anula la inscripción pendiente y libera todos los lugares retenidos por ella.

El comienzo de la primera clase **no vence ni libera automáticamente** la inscripción. Hasta que la dueña registre el cobro o anule el pendiente, los lugares siguen ocupados. Revisar y resolver esos pendientes es una responsabilidad operativa del rol dueña.

**Justificación.** Evita que el sistema libere una vacante mientras el alumno está pagando y el cobro aún no fue registrado. Se elige una gestión manual sencilla en lugar del vencimiento automático propuesto anteriormente. La contrapartida es que un pendiente no revisado continúa ocupando cupo.

**Ejemplos de aceptación.**

- Normal: Paula elige dos clases fijas; sus lugares quedan ocupados por la inscripción pendiente. Al registrar el cobro de su primera clase se activa el abono sin duplicar la ocupación.
- Borde: la clase comienza mientras se registra el cobro; el sistema no libera los lugares por el solo transcurso del tiempo.
- Borde: Paula no se presenta; cuando la dueña anula la inscripción pendiente, ambos lugares quedan disponibles.
- Borde: la dueña todavía no revisó un pendiente sin pago; los lugares siguen retenidos, no se liberan automáticamente.

Esta aprobación no define la política de renovación ni vencimiento de abonos activos de D06, ni incorpora pagos online. La concurrencia y la prevención de sobrecupos se detallarán al implementar, respetando que cada lugar solo pueda ocuparse una vez.

---

## D05 — Matriz de notificaciones y significado de cada mensaje

**Estado:** **Reglas de notificación aprobadas para turnos, clases y vencimiento de abonos**. Incluye el tratamiento aprobado de inscripciones a clases sueltas con menos de 24 horas de anticipación.

**Evidencia actual.** RF 6 envía recordatorio por email 24 horas antes para turnos y clases; RF 7 pide confirmar asistencia; RF 12 prepara, pero no envía, avisos de suspensión. La intención confirmada requiere que el cliente reciba avisos de cancelación de clase y vencimiento de abono, y que la confirmación de asistencia aplique solo a turnos individuales. También expresa preferencia por no recibir recordatorios de clases recurrentes de abono.

### Reglas de negocio aprobadas para turnos individuales

- Al reservar un turno individual, se envía **inmediatamente** un email con los detalles del turno y el enlace privado “Ver mi turno”.
- Se envía un recordatorio por email **24 horas antes del inicio** con el **mismo enlace**. Si la reserva se realiza con menos de 24 horas de anticipación, se envía solo el email inicial, sin un recordatorio duplicado.
- Si la reserva se realiza exactamente 24 horas antes del inicio, se envía un único email en vez de dos simultáneos. Ese email conserva el contenido aplicable del recordatorio: pide confirmación solo si aún no ocurrió. Es una **deduplicación** de emisiones, no un nuevo plazo ni un cambio de la regla de 24 horas.
- El recordatorio pide confirmar la intención de asistir solo si la persona todavía no confirmó. Si ya confirmó, puede recibir el recordatorio como información, sin una nueva solicitud de confirmación.
- Si el turno fue cancelado, no se envía recordatorio. La confirmación declara intención de asistir, no registra asistencia real; la falta de respuesta conserva la reserva y no la cancela automáticamente. No se aplica a clases recurrentes.
- El enlace incluido en ambos emails es el mismo enlace vigente definido en D01: sirve para ese turno individual, es reutilizable hasta 24 horas después de su inicio y puede ser compartido manualmente por la dueña mientras esté vigente. No se aprueba integración automática ni uso obligatorio de WhatsApp.

### Reglas de negocio aprobadas para clases

- Las clases con abono no generan recordatorios recurrentes ni solicitudes de confirmación.
- Las clases sueltas generan un recordatorio por email 24 horas antes, sin solicitar confirmación de asistencia.
- Al suspender una clase, se envía inmediatamente un email a cada alumno anotado; el aviso no queda solo preparado para envío manual.
- Si una clase suelta se reserva con menos de 24 horas de anticipación, se envía un único email al inscribirse con fecha y horario, sin solicitar confirmación ni agregar otro recordatorio.
- No se extiende a clases el enlace privado de turnos individuales.

### Reglas aprobadas para avisos de abono

- Enviar un email 3 días antes del vencimiento, recordando la fecha.
- Enviar otro email al vencer, únicamente si todavía no renovó.
- No enviar mensajes diarios ni presentar el vencimiento como liberación del lugar. La conservación y liberación manual se rigen por D06.

### Matriz de estado

| Evento | Destinatario | Canal y momento | Estado y efecto |
|---|---|---|---|
| Email inicial “Ver mi turno” | Cliente de turno individual | Email inmediato al reservar, con detalles y enlace privado. | **Aprobado.** Da acceso privado y acotado a ese turno individual. |
| Recordatorio de turno individual | Cliente de turno individual | Email 24 h antes del inicio, con el mismo enlace. No se envía si el turno fue cancelado; si se reservó con menos de 24 h, solo se emite el email inicial. | **Aprobado.** Informa el turno; la coincidencia exacta de 24 h se deduplica en un solo email con el contenido aplicable del recordatorio. |
| Solicitud de confirmación | Cliente de turno individual aún no confirmado | En el recordatorio de 24 h. | **Aprobado.** Declara intención de asistir; no registra asistencia real. La falta de respuesta conserva la reserva. |
| Recordatorio de clase suelta | Cliente | Email 24 h antes; si se inscribe con menos de 24 h, un único email inmediato al inscribirse. | **Aprobado.** Informa fecha y horario sin solicitar confirmación de asistencia ni agregar otro recordatorio para la inscripción tardía. |
| Clase fija de abono | Cliente | Sin recordatorio recurrente ni solicitud de confirmación. | **Aprobado.** No exige respuesta semanal del alumno. |
| Clase suspendida | Cada alumno anotado | Email inmediato tras suspender. | **Aprobado.** Envía el aviso; amplía RF 12, que hoy solo deja el mensaje preparado. |
| Vencimiento próximo de abono | Alumno | Email 3 días antes del vencimiento. | **Aprobado.** Recuerda la fecha de vencimiento. |
| Abono vencido | Alumno que todavía no renovó | Email al vencer. | **Aprobado.** Informa el vencimiento y la necesidad de renovar para asistir; no anuncia liberación automática del lugar ni se repite diariamente. |

Una clase “suspendida” se diferencia de un mensaje “preparado” o “enviado”. Para las reglas aprobadas, la implementación debe evitar duplicados y soportar reintentos controlados; la mecánica técnica para hacerlo permanece pendiente y no altera los momentos ni efectos de negocio definidos arriba.

**Rationale.** Elimina la ambigüedad entre RF 6 y RF 7 para turnos individuales, conserva el recordatorio de 24 horas sin duplicar envíos en los casos límite y evita una arquitectura de mensajería fuera de alcance. Las reglas de clases aprobadas evitan recordatorios repetitivos para abonos y comunican suspensiones sin depender del envío manual. Los avisos de abono informan sin insistencia diaria y no modifican la retención manual del lugar.

**Ejemplos de aceptación aprobados para turnos individuales.**

- Normal: al reservar un turno de Mario con 48 horas de anticipación, recibe inmediatamente un email con los detalles y el enlace. A las 24 horas del inicio recibe un recordatorio con el mismo enlace; como todavía no confirmó, el correo le solicita confirmar. Al confirmar, se actualiza su ocurrencia, no su asistencia.
- Normal: Mario ya confirmó; puede recibir el recordatorio de 24 horas como información, sin otra solicitud de confirmación.
- Borde: al reservar un turno con 12 horas de anticipación, Mario recibe solo el email inicial; no recibe un recordatorio adicional.
- Borde: al reservar exactamente 24 horas antes, Mario recibe un único email. La deduplicación evita dos envíos simultáneos y no cambia el plazo de recordatorio.
- Borde: si Mario cancela el turno antes de su inicio, no se le envía recordatorio.

**Ejemplos de aceptación para clases.**

- Normal: una clase se suspende; cada alumno anotado recibe inmediatamente un aviso por email.
- Normal: un alumno con abono no recibe recordatorios ni pedidos de confirmación para sus clases recurrentes.
- Normal: una alumna anotada con anticipación en una clase suelta recibe un email 24 horas antes, sin solicitud de confirmación.
- Borde: un reintento técnico no debe duplicar el aviso de suspensión para el mismo alumno y clase.

**Ejemplos de aceptación para abonos.**

- Un abono vence el 15: se envía un recordatorio el 12 y, si sigue sin renovar al vencer, el aviso de vencimiento.
- Si renovó antes del vencimiento, no se envía el aviso de abono vencido correspondiente a ese período.
- Un abono vencido no genera emails diarios ni libera el lugar por el envío del aviso.

**Caso aprobado de inscripción tardía.** Una alumna se inscribe a una clase suelta que comienza en dos horas: recibe un único email con fecha y horario al inscribirse, sin solicitud de confirmación ni otro recordatorio.

**Criterio de vencimiento aprobado.** Los avisos usan la fecha de vencimiento de D06: es el primer día sin cobertura. La hora del pago no determina el vencimiento ni cambia la frecuencia aprobada de avisos.

---

## D06 — Vencimiento, renovación, liberación y generación semanal

**Evidencia actual.** RF 5 cubre un mes desde el pago; RF 19 genera alumnos fijos solo con abono vigente y libera el lugar si no renovaron. RF 4 y RF 5 introducen inscripciones pendientes; no determinan cómo se coordinan vencimiento, aviso, una renovación tardía y semanas ya generadas. Tampoco precisan la semántica de “un mes” en límites de fecha, horario o meses de distinta duración.

**Estado:** **Núcleo aprobado; detalles pendientes**.

### Reglas de negocio aprobadas

- El abono dura **un mes desde su alta**, que ocurre cuando la dueña registra el cobro y lo activa según D04. No comienza al crear la ficha ni la inscripción pendiente.
- Vence en el mismo número de día del mes siguiente. Si ese día no existe, se utiliza el último día de ese mes; no se reemplaza el mes por un plazo fijo de 30 días.
- Al renovar sin interrupción se conserva el día de referencia original, aunque un mes corto obligue a ajustar el vencimiento. Por ejemplo, con referencia 31, vence el último día de febrero y luego el 31 de marzo. Una renovación tardía establece como nueva referencia el día del pago.
- La **fecha de vencimiento es el primer día sin cobertura**: el período incluye la fecha de alta y llega hasta el día anterior al vencimiento inclusive. Se trabaja por fechas, sin depender de la hora a la que se pagó.
- La renovación anticipada comienza al vencimiento del período actual, no el día del pago: no se pierden días por pagar antes.
- Al vencer, el abono queda **vencido**, sin renovación automática. El lugar fijo se conserva pendiente de resolución y no se ofrece a otra persona.
- La dueña registra la renovación o da de baja la inscripción y libera el lugar. Mientras no resuelva, el lugar permanece retenido.
- La renovación tardía comienza **el día del pago**, sin cobertura ni cobro retroactivos por el intervalo vencido. Si vence el 12 y paga el 15, la nueva cobertura va del 15 al 15 del mes siguiente.
- **Conservar el lugar no habilita asistencia sin abono vigente.** El alumno debe renovar antes de volver a asistir; puede pagar al llegar y la dueña registra la renovación.

Esta decisión sustituye la propuesta anterior de liberación automática y requiere revisar posteriormente RF 19. No modifica aún ese RF ni el SRS. La retención manual evita quitar un horario mientras el pago está pendiente, a cambio de que la dueña revise los vencidos.

### Ejemplos de aceptación

- Normal: alta el 12 de abril; cobertura hasta el 11 de mayo inclusive y vencimiento el 12 de mayo. Desde el 12 necesita renovar para asistir. Un pago anticipado el 10 de mayo inicia la siguiente cobertura el 12 de mayo, sin huecos ni días duplicados.
- Borde: alta el 31 de enero de un año no bisiesto; vencimiento el 28 de febrero. Si renueva sin interrupción, el período siguiente empieza el 28 de febrero y vence el 31 de marzo, conservando la referencia 31.
- Borde: en ese caso, si renueva tarde el 3 de marzo, la nueva cobertura empieza el 3 de marzo y vence el 3 de abril; la referencia pasa a ser el día 3.
- Borde: vence el 12 de mayo y paga el 15 de mayo; la nueva cobertura comienza el 15 de mayo y vence el 15 de junio. Los días intermedios no quedan cubiertos retroactivamente.
- Borde: el abono está vencido y la dueña aún no resolvió; el lugar sigue retenido, pero el alumno necesita renovar antes de asistir.
- Normal: la dueña da de baja una inscripción vencida y libera sus lugares para nuevas inscripciones.

### Generación semanal y pendientes

La conservación de lugar debe distinguirse de la cobertura para asistir. No se aprobó todavía cómo reflejar un lugar retenido con abono vencido en las listas semanales; su generación no puede ofrecer ese lugar como libre ni habilitar asistencia sin renovación.

Se mantiene como propuesta pendiente que repetir la generación no duplique anotaciones ni borre historial, cancelaciones o suspensiones. Su coordinación con la renovación y la baja manual se definirá antes de actualizar RF 19.

**Regreso tras baja aprobado.** La baja termina la inscripción a los horarios fijos y libera sus lugares; no elimina la ficha del cliente, su cuenta ni su historial. Si vuelve, usa la misma cuenta si ya tenía una y realiza una nueva inscripción en horarios con cupo, con un nuevo abono. No tiene garantizados sus antiguos horarios. Si la inscripción todavía conserva sus lugares y solo venció el abono, corresponde renovar, no crear una inscripción nueva.

Los avisos se rigen por D05: email 3 días antes y al vencer si no renovó, sin mensajes diarios ni liberación automática del lugar.

---

## D07 — Cancelaciones, pagos, reintegros y ausencias

**Estado:** **Pendiente**.

**Evidencia actual.** RF 10 indica que una falta con abono pierde la clase; RF 12 establece que una clase suspendida no se recupera ni devuelve dinero y que la clase suelta se paga al llegar; RF 17 y RF 18 liberan turnos o lugares, pero no detallan consecuencias de una atención o clase suelta ya pagada. RF 5 registra pagos, sin política de reintegro.

**Alcance pendiente.** La cancelación directa de un turno individual antes de su inicio, aprobada en D02, libera el horario pero **no decide** reintegros, traslados, penalidades ni cobros. No hay un corte de dos horas aprobado o propuesto para esa cancelación individual.

Las políticas de depósitos, cobro, reintegro, traslado y aplicación de penalidades quedan fuera de alcance de WorkUp y son gestionadas por la dueña fuera del sistema. Esto no elimina el registro de cobros ya previsto por los RF: cuando la dueña registre un cobro u otro movimiento admitido, conserva la trazabilidad requerida por D03 y RF 13. No se aprueba por esta propuesta una política financiera nueva ni un mecanismo de ejecución automática.

**Rationale.** Separa la disponibilidad del turno de una consecuencia financiera aún no decidida y conserva el registro histórico de cobros sin convertir WorkUp en el gestor de la política o su ejecución.

**Ejemplos de alcance.**

- Normal: una clienta cancela directamente un turno individual antes de su inicio; el horario se libera y la decisión financiera, si existiera, queda a cargo de la dueña fuera de WorkUp.
- Borde: una notificación posterior al inicio puede llevar a que la dueña registre una ausencia; no determina por sí sola un cobro, reintegro, penalidad ni cambio de asistencia.
- Borde: se suspende una clase fija de abono; la ocurrencia queda suspendida y no se marca a ningún alumno como ausente. Las consecuencias financieras continúan sujetas a las reglas vigentes y a la decisión pendiente.

**Subdecisiones pendientes para discusión posterior.**

- Definir, fuera de WorkUp, si una cancelación, ausencia o suspensión genera reintegro, traslado, depósito, cobro o penalidad y bajo qué criterio.
- Ratificar o modificar las exclusiones de recuperación y devolución de abonos ya previstas en RF 10 y RF 12.
- Definir qué movimientos financieros ya admitidos debe registrar WorkUp para preservar los cobros originales y su trazabilidad, sin ejecutar la política financiera.

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

1. [x] D01: se aprobó que no haya cuenta obligatoria para reservar o asistir, con atención alternativa por la dueña y enlace privado de “Ver mi turno” enviado inmediatamente por email al reservar cada turno individual. El enlace es reutilizable hasta 24 horas después del inicio, puede revocarse y reemplazarse sin extender silenciosamente esa vigencia; la dueña puede compartir manualmente el enlace vigente, sin integración automática ni uso obligatorio de WhatsApp.
2. [x] D02: se aprobó la cancelación directa por enlace de un turno individual antes de su inicio, sin plazo mínimo ni aprobación de la dueña; después solo permite notificar y no altera automáticamente asistencia ni turnos atendidos o completados.
3. [x] D05: para turnos individuales, se aprobó un recordatorio por email 24 horas antes con el mismo enlace; las reservas con menos de 24 horas reciben solo el email inicial y la coincidencia exacta se deduplica. La confirmación declara intención de asistir, solo se solicita si aún no ocurrió y la falta de respuesta conserva la reserva; no se aplica a clases recurrentes.
4. [ ] D01: definir la presentación y el tratamiento operativo de la notificación posterior al inicio, la asociación histórica de cuentas y el flujo de autoregistro opcional. La mecánica técnica de enlaces y emails queda pendiente sin reabrir sus reglas de negocio aprobadas.
5. [x] D02: cancelar una clase con abono antes del inicio libera solo esa fecha, conserva horarios fijos y no genera recuperación ni devolución automáticas; desde el inicio la dueña registra ausencia cuando corresponde. Siguen pendientes reprogramación autenticada, cambio de horario fijo y clases sueltas.
6. [x] D04: inscripción pendiente ocupa lugares fijos, cobro registrado activa el abono y anulación manual por falta de pago o inasistencia libera los lugares. Sin vencimiento automático al inicio de la primera clase.
7. [x] D06: aprobado mes desde alta, ajuste al último día si no existe fecha equivalente, renovación anticipada desde vencimiento y tardía desde pago, conservación manual del lugar sin asistencia hasta renovar. Aprobado que el vencimiento sea el primer día sin cobertura, sin depender de la hora de pago. Aprobado conservar el día de referencia al renovar sin interrupción y reiniciarlo con el día de pago al renovar tarde. Aprobado el regreso tras baja mediante nueva inscripción sujeta a cupo, conservando cliente, cuenta e historial. Solo queda pendiente la generación semanal de D06.
8. [ ] D07: definir fuera de WorkUp las consecuencias financieras y conservar en WorkUp el registro trazable de cobros ya previsto.
9. [x] D03: aprobados cobro y asistencia independientes, concepto y referencia concretos, cobro por abono sin duplicación por clase y respeto del precio pactado. Ajustes, descuentos y reintegros continúan pendientes.
10. [ ] D08: validar las restricciones de capacidad que se aplican a reservas y atenciones sin turno.
11. [x] D05: aprobadas clases con abono sin recordatorio ni confirmación, clase suelta con email 24 h antes sin pedir confirmación y suspensión con email inmediato a los anotados. También aprobados email de abono 3 días antes y al vencer si no renovó, sin mensajes diarios. Aprobada también la inscripción a clase suelta con menos de 24 h: único email inmediato con fecha y horario, sin confirmación ni recordatorio adicional.

Nada de este documento actualiza los RF vigentes hasta que el usuario acepte expresamente las decisiones correspondientes.
