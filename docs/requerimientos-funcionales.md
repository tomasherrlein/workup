# Requerimientos Funcionales WorkUp

**Equipo:** Bapton Solutions — **Fecha:** 21-09-2026

**Nota de versión.** Este documento incorpora las decisiones D01 a D08 aprobadas en
`docs/propuestas-resolucion-requerimientos.md` y sustituye a `Requerimientos Funcionales WorkUp.md` (raíz), que
se conserva sin modificar como antecedente histórico. Las rutas mencionadas en este documento son relativas a la
raíz del repositorio.

Del RF 1 al RF 13 se mantiene el orden y la numeración del documento original del grupo. Del RF 14 al RF 20
siguen los requerimientos incorporados en la primera revisión. Del RF 21 en adelante se agregan los
requerimientos nuevos de esta versión, para funcionalidad que no encajaba en un RF existente. La numeración no
indica orden de ejecución.

---

### Requerimiento Funcional 1: Registrar nuevo cliente

**Descripción:** Permite dar de alta a un cliente nuevo en el sistema para asociarle turnos, inscripciones u
observaciones. La cuenta y la contraseña son opcionales: la dueña puede registrar la ficha sin ellas para
reservar o inscribir a la persona; la persona también puede crear su propia cuenta más adelante (RF 23).

**Inputs:** Nombre, apellido, teléfono, email, contraseña (opcional) y observaciones.

**Processing:**

1. Se ingresan los datos personales del cliente.
2. Se verifica que el email ingresado no exista previamente en el registro de clientes.
3. Se registran los datos personales y las observaciones. Si se informa contraseña, se registran también las
   credenciales de acceso; si no, la ficha queda sin cuenta asociada y puede vincularse una más adelante por
   invitación (RF 24).

**Outputs:** Mensaje de confirmación y ficha de cliente, con o sin cuenta según lo informado.

**Error Handling:**

- Si el cliente ya está registrado, el sistema muestra un mensaje de error.
- Si falta ingresar el teléfono, el email o el nombre, el sistema impide el guardado e indica los campos
  obligatorios incompletos.
- Si el teléfono o el email no tienen un formato válido, se pide corregirlo.
- Si se informa contraseña, debe cumplir la política mínima de seguridad definida por el sistema.

---

### Requerimiento Funcional 2: Registrar y configurar servicios

**Descripción:** Permite dar de alta los servicios que ofrece el negocio, definiendo las duraciones de los
servicios o los cupos de las clases.

**Inputs:** Descripción, precio, cupo máximo y tiempo de duración.

**Processing:**

1. Se rellenan los datos del servicio.
2. Se guarda el servicio en el tarifario.

**Outputs:** Servicio registrado en el tarifario.

**Error Handling:**

- Si el precio ingresado es menor o igual a cero, se solicitará un valor válido.
- Si la duración total suma 0 minutos, no se permitirá guardar el servicio.

---

### Requerimiento Funcional 3: Agendar Turno (Peluquería)

**Descripción:** Permite registrar la reserva de un turno individual para un servicio específico con una
trabajadora, evaluando huecos libres. El cliente reserva desde el enlace que le envía el negocio o desde su panel
si tiene cuenta, o la dueña carga el turno cuando el cliente le escribe o concurre sin usar el sistema.

**Inputs:** Email del cliente, servicio, trabajadora, fecha y horario elegido entre los disponibles.

**Processing:**

1. El sistema busca al cliente por el email ingresado; si es la primera vez, se lo da de alta sin exigir
   contraseña (RF 1).
2. Se consulta el horario de atención de la trabajadora elegida (días de trabajo, hora de inicio y fin, pausas no
   laborables) para verificar que el turno esté dentro de su jornada.
3. Se busca en la agenda un espacio libre que alcance para la duración total del servicio y se muestran los
   horarios disponibles.
4. Se guarda el turno incluyendo: código de turno, fecha, hora de inicio, servicio, trabajadora, cliente y
   estado.
5. Se envía inmediatamente al cliente un email con los detalles del turno y el enlace privado "Ver mi turno"
   (RF 21).

**Outputs:** Confirmación del turno, nuevo turno y email con el enlace privado enviado.

**Error Handling:**

- Si no hay espacio libre suficiente de la misma trabajadora, el sistema rechaza la reserva y ofrece el horario
  libre más cercano que tenga esa misma persona.

---

### Requerimiento Funcional 4: Inscripción a Clases (Estudio de Yoga)

**Descripción:** Se da de alta un cliente a una clase suelta, o a las clases fijas de la grilla según la cantidad
semanal que desee. La inscripción a clases fijas no requiere pago por adelantado: queda pendiente de pago y sus
lugares se retienen hasta que la dueña registre el cobro o anule la inscripción.

**Inputs:** Email del cliente, frecuencia semanal elegida y clases fijas seleccionadas de la grilla, o clase
suelta elegida.

**Processing:**

1. El sistema busca al cliente por su email; si es su primera inscripción, se lo da de alta (RF 1).
2. El sistema verifica que exista cupo disponible de manera permanente en las clases fijas seleccionadas de la
   grilla, o cupo en la clase suelta que eligió.
3. Se registra la inscripción con la frecuencia semanal y las clases fijas elegidas, en estado pendiente de pago.
4. Los lugares elegidos quedan ocupados por la inscripción pendiente desde este momento, sin ofrecerse
   simultáneamente a otro alumno.

**Outputs:** Inscripción registrada, pendiente de pago, con sus lugares retenidos.

**Error Handling:**

- Si alguna de las clases elegidas ya alcanzó su capacidad máxima y no tiene vacantes fijas, el sistema emite el
  mensaje "Cupo permanente no disponible" y solicita elegir otra clase u horario.
- Si la cantidad de clases fijas seleccionadas supera la frecuencia semanal elegida para el abono, el sistema
  impide guardar el registro.
- Mientras la inscripción esté pendiente de pago, los lugares elegidos permanecen ocupados y no se liberan por el
  solo inicio de la primera clase. Si el alumno no paga o no se presenta, la dueña anula la inscripción pendiente
  y libera los lugares retenidos (RF 30).
- Un cambio posterior de los horarios fijos ya activos es una operación distinta (RF 26).

---

### Requerimiento Funcional 5: Registrar cobro

**Descripción:** Se calcula el importe a cobrar y se registran los datos del cobro, sea por un turno individual,
una atención sin turno, una clase suelta o un abono mensual. El cobro y la asistencia son datos independientes:
pueden registrarse en cualquier orden y ninguno modifica automáticamente al otro. Cuando el cobro corresponde a
un abono, el abono se activa en esa fecha y el alumno queda incorporado a sus listas fijas (RF 19).

**Inputs:** Email del cliente, concepto cobrado (turno, atención sin turno, clase suelta o abono) y su referencia
concreta, monto acordado, motivo del monto si difiere del tarifario (opcional), pago efectuado, fecha.

**Processing:**

1. Se busca al alumno o cliente en el registro a través de su email.
2. Si es su primera inscripción, darlo de alta sin exigir contraseña (RF 1).
3. El sistema calcula el importe sugerido según el concepto, tomado del tarifario, salvo que la dueña haya
   acordado un precio distinto:
    - Atención de la peluquería o atención sin turno: el precio del servicio, tomado del tarifario.
    - Clase suelta: el precio del tipo de clase, tomado del tarifario.
    - Abono mensual: el importe correspondiente a la frecuencia semanal y las clases fijas en que está
      inscripto, revisando las clases fijas elegidas que hay en la grilla.
4. Se registra el cobro con el email, el monto acordado, el concepto cobrado con su referencia concreta (turno,
   atención, clase suelta o abono), la fecha y, si el monto difiere del tarifario, el motivo informado. En la
   peluquería se registran además la hora, el servicio y la trabajadora que atendió; en el estudio, la clase o el
   período de abono cobrado.
5. Si el cobro corresponde a un abono, se incorpora al alumno a la lista fija de esas clases y el abono cubre un
   mes desde la fecha de pago (RF 29).
6. El precio registrado queda acordado para ese turno, atención, clase suelta o abono: un cambio posterior del
   tarifario no lo modifica.

**Outputs:** Cobro registrado en el registro de movimientos, con su concepto y referencia concreta, y, cuando
corresponde, abono vigente con el alumno en sus listas fijas.

**Error Handling:**

- El cobro de un turno o una clase ya cobrados muestra el pago existente y no genera un cobro adicional.
- El cobro de una atención sin turno se registra mediante el RF 11, que crea el movimiento sin turno asociado.
- Un cobro registrado no se edita ni se borra: para corregirlo se anula con motivo (RF 27) y, si corresponde, se
  registra el cobro correcto.
- Si la dueña devuelve dinero fuera del sistema, la devolución se registra mediante RF 28, enlazada al cobro
  original.

---

### Requerimiento Funcional 6: Enviar recordatorio de turno

**Descripción:** Avisar al cliente de su turno individual o su clase suelta con la anticipación configurada (24
horas por defecto). Las clases con abono no generan este recordatorio.

**Inputs:** Turnos individuales y clases sueltas próximos, anticipación configurada (24 horas por defecto).

**Processing:**

1. Al cumplirse la anticipación configurada, recuperar los turnos individuales y las clases sueltas alcanzados,
   excluyendo las clases fijas de abono y los turnos cancelados.
2. Para un turno individual, emitir por email el recordatorio con el enlace privado "Ver mi turno" (RF 21)
   vigente, solicitando confirmar la intención de asistir (RF 7) solo si el cliente todavía no confirmó. Si ya
   confirmó, puede recibir el recordatorio como información.
3. Para una clase suelta, emitir por email el recordatorio con fecha y horario, sin solicitar confirmación.
4. Si el turno o la inscripción se realizó con menos de 24 horas de anticipación, no se emite este recordatorio:
   alcanza con el email inicial enviado al reservar o inscribirse (RF 3, RF 21, RF 4).
5. Si el turno se reservó exactamente 24 horas antes de su inicio, se emite un único email en lugar de dos
   simultáneos, con el contenido aplicable del recordatorio.

**Outputs:** Recordatorio enviado al cliente, sin duplicar envíos.

**Error Handling:**

- Si el turno individual fue cancelado, no se envía recordatorio.
- Un reintento técnico no debe duplicar el envío del recordatorio para el mismo turno o clase.

---

### Requerimiento Funcional 7: Confirmar intención de asistencia (turno individual)

**Descripción:** Antes del inicio de un turno individual, el cliente puede confirmar su intención de asistir
desde el enlace privado "Ver mi turno" (RF 22) o desde su panel si tiene cuenta, sin contraseña obligatoria ni
plazo mínimo. Aplica únicamente a turnos individuales; no se aplica a clases recurrentes de abono ni a clases
sueltas.

**Inputs:** Turno individual y respuesta del cliente.

**Processing:**

1. Se recibe la confirmación del cliente, sea en respuesta al recordatorio (RF 6) o por iniciativa propia, antes
   del inicio del turno.
2. Si el cliente ya había confirmado, una nueva confirmación no genera un cambio adicional.
3. Se registra que el cliente confirmó su intención de asistir.

**Outputs:** Turno con la intención de asistir confirmada.

**Error Handling:**

- La confirmación no registra asistencia real ni modifica un estado ya registrado como "Atendido" o "Cobrado".
- La falta de respuesta conserva la reserva: no genera una cancelación automática.
- Si el cliente prefiere no asistir, cancela el turno (RF 18) en lugar de confirmar.

---

### Requerimiento Funcional 8: Registrar trabajadora

**Descripción:** Registrar y modificar trabajadora con sus datos, definiendo sus horarios de atención o
disponibilidad semanal para la asignación de turnos o clases.

**Inputs:** Nombre, apellido, teléfono, CUIL, días de trabajo, hora de inicio y de fin por día, pausas no
laborables.

**Processing:**

1. Se ingresan los datos de la trabajadora.
2. Se verifica que el CUIL ingresado no se encuentre registrado previamente.
3. Se validan los horarios ingresados.
4. Se almacenan los datos de la trabajadora.

**Outputs:** Trabajadora registrada con su horario de atención habilitado.

**Error Handling:**

- Si ya existe una trabajadora con ese CUIL, se informa y no se duplica.
- Si el rango horario o la pausa configurada son inconsistentes, el sistema solicita corregir el tramo horario.

---

### Requerimiento Funcional 9: Definir clase en la grilla semanal

**Descripción:** Registrar o modificar en la grilla semanal qué tipo de clase se dicta, qué día, a qué horario y
con qué profesora.

**Inputs:** Tipo de clase, día de la semana, horario y profesora.

**Processing:**

1. Recibir los datos de la clase.
2. Se verifica la disponibilidad horaria de la profesora seleccionada.
3. Registrar la clase en la grilla semanal, que se repite todas las semanas.
4. Publicar la grilla actualizada.

**Outputs:** Clase incorporada a la grilla semanal publicada.

**Error Handling:**

- Si el horario queda fuera de la disponibilidad de la profesora, no se registra.
- El cambio de horario fijo de un alumno ya inscripto (RF 26) requiere que el nuevo horario exista en esta grilla
  con cupo disponible; no modifica esta grilla en sí.

---

### Requerimiento Funcional 10: Registrar asistencia

**Descripción:** Registrar si cada cliente asistió a su turno o clase, sea porque la dueña lo comprueba al
finalizar la atención o porque el cliente confirma su inasistencia desde el enlace privado una vez iniciado su
turno individual (RF 22).

**Inputs:** Turno o clase y fecha, cliente, asistencia u origen del aviso de ausencia.

**Processing:**

1. El sistema recupera el turno individual, la clase suelta o la lista de la clase grupal correspondientes a la
   fecha seleccionada.
2. Si es un turno individual o una clase suelta y el cliente asistió, se marca como "Atendido"; si no asistió, se
   marca como "Ausente".
3. Si es una clase grupal con abono, el sistema recorre la lista de alumnos anotados para esa fecha. Se marca
   como "Atendido" a los alumnos que hayan asistido y "Ausente" a los que no avisaron ni cancelaron antes del
   inicio.
4. Si el cliente de un turno individual confirma "No voy a asistir" desde el enlace privado una vez iniciado el
   turno, se registra "Ausente" con constancia de que lo informó el cliente, sin intervención de la dueña.
5. Se guarda el registro de asistencia.

**Outputs:** Turno o lista de la clase con la asistencia registrada, incluido el origen del aviso cuando
corresponde.

**Error Handling:**

- El aviso confirmado del cliente no sobrescribe un turno ya marcado "Atendido" ni un turno completado.
- Si el alumno con abono no avisó ni canceló antes del inicio y falta a la clase, se lo registra como ausente; no
  se recupera la clase ni se genera una devolución automática por esa falta.
- Si el cliente había cancelado antes del inicio (RF 18), el lugar ya quedó liberado: no corresponde registrar
  ausencia sobre esa ocurrencia.

---

### Requerimiento Funcional 11: Registrar atención sin turno

**Descripción:** Registrar la atención de un cliente que llega sin turno, aunque se superponga con otra atención
o turno de la misma trabajadora, y su cobro cuando corresponde.

**Inputs:** Servicio, trabajadora, fecha, hora y monto.

**Processing:**

1. Se ingresan los datos de la atención.
2. Se registra la atención sin turno asociado, aunque se superponga con otra atención o turno existente; la
   superposición no impide guardar el registro ni exige inventar otro horario.
3. Se registra el cobro con fecha, hora, monto, servicio y trabajadora que atendió, identificando que corresponde
   a esta atención sin turno.

**Outputs:** Atención y cobro registrados en el registro de movimientos, sin modificar las reservas existentes.

**Error Handling:**

- El registro no se bloquea por superposición con otras atenciones o turnos; la dueña puede cargarlo cuando lo
  necesite.

---

### Requerimiento Funcional 12: Suspensión de clases y notificación

**Descripción:** Suspender la clase de una semana puntual y enviar inmediatamente el aviso a cada alumno
anotado.

**Inputs:** Clase y fecha.

**Processing:**

1. Recibir la clase cancelada con su fecha.
2. Marcar la clase de esa fecha como suspendida, sin alterar la grilla.
3. Identificar a los alumnos anotados para esa clase, tanto fijos como sueltos.
4. Enviar inmediatamente por email a cada alumno anotado el aviso de la suspensión.

**Outputs:** Clase suspendida, lista de alumnos anotados y aviso enviado por email a cada uno.

**Error Handling:**

- Si no hay alumnos anotados, se suspende la clase sin enviar avisos.
- La clase suspendida no se recupera ni se devuelve el dinero automáticamente: los alumnos con abono la pierden
  igual que si hubieran faltado y quien iba a una clase suelta no la paga, porque abona al llegar.
- Un reintento técnico no debe duplicar el aviso de suspensión para el mismo alumno y clase.

---

### Requerimiento Funcional 13: Reporte del período

**Descripción:** Obtener el resultado de un período sumando los cobros vigentes y restando las devoluciones y los
gastos, junto con los indicadores del negocio.

**Inputs:** Período a consultar.

**Processing:**

1. Recibir el período.
2. Sumar los cobros vigentes, no anulados, de atenciones con turno, atenciones sin turno, clases sueltas y
   abonos del período.
3. Restar las devoluciones manuales registradas en el período (RF 28) y los gastos del período.
4. Calcular los indicadores del negocio: margen por servicio, clientes que vuelven con frecuencia, clientes que
   dejaron de venir, recuento de turnos perdidos por ausencia y trabajo de cada trabajadora.

**Outputs:** Reporte del resultado del período e indicadores del negocio.

**Error Handling:**

- Si el período es posterior a la fecha actual, no se calcula y se solicita corregir el período.
- Los cobros anulados no integran el resultado del período, aunque su historial se conserva junto con la
  anulación (RF 27).

---

### Requerimiento Funcional 14: Registrar gasto

**Descripción:** Registrar un gasto del negocio con su fecha, monto, tipo y descripción.

**Inputs:** Fecha, monto, tipo de gasto, descripción.

**Processing:**

1. Se ingresan los datos del gasto.
2. Se registra el gasto clasificado por tipo.

**Outputs:** Gasto registrado en el registro de movimientos, disponible para el reporte del período.

**Error Handling:** N/A.

---

### Requerimiento Funcional 15: Iniciar sesión

**Descripción:** Permite que el cliente con cuenta acceda a su panel con sus credenciales. La cuenta es opcional:
no reemplaza la atención sin cuenta ni el enlace privado "Ver mi turno" (RF 21).

**Inputs:** Email y contraseña.

**Processing:**

1. Se verifica que exista una cuenta registrada con ese email.
2. Se valida la contraseña ingresada.
3. Se habilita el acceso al panel del cliente.

**Outputs:** Sesión iniciada.

**Error Handling:**

- Si el email no está registrado o la contraseña no coincide, no se habilita el acceso y se informa que los
  datos son incorrectos.
- Si el cliente olvidó su contraseña, puede recuperarla mediante RF 25.

---

### Requerimiento Funcional 16: Consultar mis turnos

**Descripción:** Muestra al cliente con sesión iniciada los turnos y las clases que tiene reservados. El enlace
privado sin sesión (RF 22) solo muestra el turno exacto al que se limita, sin este historial completo.

**Inputs:** Cliente con sesión iniciada.

**Processing:**

1. Se recuperan los turnos y las clases del cliente con su fecha, horario, servicio, trabajadora y estado.

**Outputs:** Detalle de los turnos y clases del cliente.

**Error Handling:** N/A.

---

### Requerimiento Funcional 17: Reprogramar turno

**Descripción:** Pasar un turno individual a otro horario libre de la misma trabajadora, antes del inicio del
turno original, a pedido del cliente autenticado desde su panel o de la dueña. El turno original permanece
reservado mientras se consultan alternativas y solo se libera al confirmar exitosamente el nuevo horario.

**Inputs:** Turno a reprogramar, nuevo horario elegido entre los disponibles.

**Processing:**

1. Se recupera el turno a reprogramar, siempre que no haya comenzado.
2. Se calculan los horarios libres de esa misma trabajadora que alcancen para la duración del servicio.
3. Se muestran los horarios disponibles.
4. Se recibe el horario elegido y se cambian la fecha y la hora del turno, liberando recién en ese momento el
   horario anterior.

**Outputs:** Turno con su nueva fecha y hora, y confirmación al cliente. Si no se confirma un nuevo horario, el
turno original permanece reservado.

**Error Handling:**

- Si no hay ningún horario de esa trabajadora que le sirva al cliente, o si abandona el cambio, el turno original
  se conserva sin cambios; no se cancela automáticamente.
- Desde el inicio del turno original no se admite reprogramación: una atención posterior requiere una reserva
  nueva (RF 3).
- Las clases grupales no se reprograman: el alumno cancela su inscripción si corresponde (RF 18) y se anota en
  otra clase con lugar mediante una nueva inscripción (RF 4); no hay traslado automático.
- El enlace privado sin sesión (RF 21, RF 22) no habilita esta reprogramación.

---

### Requerimiento Funcional 18: Cancelar turno o clase

**Descripción:** Registrar que el cliente no va a asistir a su turno individual, su clase con abono o su clase
suelta, antes del inicio, liberando el lugar correspondiente. Sin plazo mínimo ni aprobación de la dueña.

**Inputs:** Cliente, turno o clase a cancelar.

**Processing:**

1. Se recupera el turno o la clase elegida, siempre que no haya comenzado.
2. Se registra la anotación como cancelada.
3. Según el tipo:
    - **Turno individual:** el horario queda disponible para otra persona. El cliente puede cancelarlo
      directamente desde el enlace privado "Ver mi turno" (RF 22) sin sesión, o la dueña puede registrarlo.
    - **Clase con abono:** se libera únicamente el lugar de esa fecha. Los días y horarios fijos del abono se
      conservan; cambiarlos es una operación distinta (RF 26).
    - **Clase suelta:** si el cliente tiene cuenta, cancela desde su panel; si no tiene cuenta, avisa a la dueña
      y ella registra la cancelación en su nombre.

**Outputs:** Turno o clase cancelada y lugar liberado.

**Error Handling:**

- Desde el inicio del turno o la clase, ya no se admite esta cancelación; la dueña registra la ausencia cuando
  corresponda (RF 10), o el cliente confirma "No voy a asistir" desde el enlace si se trata de un turno individual
  (RF 10, RF 22).
- La cancelación no genera devolución, crédito ni penalización automáticos, ni borra los cobros ya registrados;
  las políticas de reintegro las gestiona la dueña fuera de WorkUp.
- Para asistir a otra clase se requiere una nueva inscripción sujeta a cupo (RF 4); no hay traslado automático.

---

### Requerimiento Funcional 19: Generar las clases de la semana

**Descripción:** Crear las clases de una semana a partir de la grilla y anotar en ellas a los alumnos que las
tienen como clases fijas, según el estado de su abono.

**Inputs:** Grilla semanal, semana a generar, clases fijas de los alumnos, abonos.

**Processing:**

1. Se recorren las clases de la grilla.
2. Se crea una clase por cada una, con su fecha, su profesora y su cupo máximo.
3. Se anota en cada clase, en su lugar fijo, a los alumnos que la tienen como clase fija: con asistencia
   habilitada si el abono está vigente, o como "Pendiente de renovación" si el abono está vencido y el lugar
   todavía no fue liberado por la dueña (RF 29).
4. Regenerar la lista de una semana ya generada no duplica inscripciones ni borra el historial, las
   cancelaciones o las suspensiones existentes.

**Outputs:** Lista de cada clase de la semana, con su fecha y sus alumnos fijos anotados, incluidos los marcados
"Pendiente de renovación".

**Error Handling:**

- Un alumno "Pendiente de renovación" conserva su lugar pero no puede registrarse como asistido hasta renovar el
  abono (RF 29, RF 10).
- Si la dueña dio de baja la inscripción del alumno, no se lo anota y su lugar queda disponible para otra
  persona.

---

### Requerimiento Funcional 20: Consultar la agenda del día

**Descripción:** Mostrar a quiénes se atiende ese día, con su horario, servicio, trabajadora y estado.

**Inputs:** Fecha de la jornada.

**Processing:**

1. Se recuperan los turnos y las clases de esa fecha con sus anotados y sus estados.

**Outputs:** Detalle de la jornada.

**Error Handling:** N/A.

---

### Requerimiento Funcional 21: Enviar enlace privado "Ver mi turno"

**Descripción:** Al reservar un turno individual, enviar inmediatamente por email al cliente sus detalles y un
enlace privado, reutilizable, que le permite consultar y gestionar ese turno exacto mientras esté vigente.

**Inputs:** Turno individual reservado, email del cliente.

**Processing:**

1. Al confirmarse la reserva de un turno individual (RF 3), generar un enlace privado asociado únicamente a ese
   turno.
2. Enviar inmediatamente por email al cliente los detalles del turno junto con el enlace.
3. El enlace vence 24 horas después del inicio del turno, no de su fin ni de su creación; puede abrirse y usarse
   varias veces durante su vigencia sin consumirse.
4. La dueña puede copiar y compartir manualmente el enlace vigente por el canal que elija.

**Outputs:** Email enviado con los detalles del turno y el enlace privado vigente.

**Error Handling:**

- Si la dueña sospecha que el enlace fue comprometido o enviado al destinatario equivocado, puede revocarlo; el
  enlace revocado deja de autorizar acceso.
- Al revocar, se emite un reemplazo que conserva la fecha de vencimiento ya acordada del turno original, sin
  extenderla.
- Vencido el enlace, la persona debe contactar al negocio; el enlace no se renueva automáticamente.
- No se aprueba una integración automática con WhatsApp ni su uso obligatorio. La mecánica técnica para generar,
  almacenar, validar, revocar y auditar el enlace queda pendiente de diseño.

---

### Requerimiento Funcional 22: Gestionar turno individual desde el enlace privado

**Descripción:** Permite que la persona que posee el enlace privado "Ver mi turno" (RF 21) consulte, confirme su
intención de asistir o cancele su turno individual antes del inicio, y confirme su inasistencia desde el inicio,
sin contraseña ni cuenta.

**Inputs:** Enlace privado vigente, acción elegida (consultar, confirmar intención, cancelar, confirmar "No voy a
asistir").

**Processing:**

1. Se valida que el enlace esté vigente y no haya sido revocado.
2. Se muestra únicamente la fecha y el horario del turno exacto al que se limita el enlace, sin historial,
   clases, abonos, observaciones ni otros datos del cliente.
3. Antes del inicio del turno, la persona puede confirmar su intención de asistir (RF 7) o cancelarlo
   directamente (RF 18), sin plazo mínimo ni aprobación de la dueña.
4. Desde el inicio del turno, la persona puede confirmar "No voy a asistir", lo que registra Ausente con
   constancia de que lo informó el cliente (RF 10).

**Outputs:** Consulta del turno y, según la acción elegida, intención de asistir confirmada, turno cancelado o
Ausente registrado.

**Error Handling:**

- El enlace no habilita reprogramación (RF 17), no muestra otros datos del cliente y no vincula una cuenta con
  fichas existentes.
- "No voy a asistir" no sobrescribe un turno ya marcado Atendido o completado.
- Un turno posterior se registra como una reserva nueva (RF 3); una ocurrencia pasada nunca se reprograma desde
  el enlace.
- Un enlace vencido o revocado no permite ninguna acción; se indica contactar al negocio.

---

### Requerimiento Funcional 23: Registrar cliente en forma autónoma

**Descripción:** Permite que una persona sin ficha previa cree su propia cuenta para gestionar sus reservas desde
el panel de cliente.

**Inputs:** Nombre, apellido, email, contraseña.

**Processing:**

1. La persona completa sus datos y solicita el registro.
2. Se verifica su email mediante un mensaje de confirmación.
3. Establece su contraseña y se crea su cuenta y ficha de cliente.

**Outputs:** Cuenta y ficha de cliente creadas, con acceso al panel (RF 15).

**Error Handling:**

- Si el email ya está registrado, se informa y no se crea una ficha duplicada; se indica iniciar sesión (RF 15) o
  recuperar la contraseña (RF 25).
- Si no se verifica el email, la cuenta no se activa.

---

### Requerimiento Funcional 24: Invitar vinculación de ficha existente

**Descripción:** Permite que la dueña asocie una cuenta a una ficha de cliente ya existente sin cuenta, mediante
una invitación que el cliente debe confirmar.

**Inputs:** Ficha de cliente, email de destino confirmado con la persona.

**Processing:**

1. La dueña abre la ficha del cliente y confirma con la persona a qué email enviar la invitación, que puede ser
   distinto del registrado en la ficha.
2. Se genera y envía una invitación válida por 48 horas, de un solo uso y revocable por la dueña.
3. El destinatario crea su cuenta o inicia sesión y confirma la vinculación.
4. Al confirmar, la cuenta queda asociada a la ficha existente sin crear una ficha nueva.

**Outputs:** Ficha vinculada a la cuenta del cliente.

**Error Handling:**

- Emitir una nueva invitación invalida la anterior.
- El vencimiento o la revocación de la invitación no afecta las reservas existentes de la ficha.
- Si la cuenta del destinatario ya está vinculada a otra ficha, la invitación no cambia esa vinculación ni revela
  el historial de la ficha invitada; se indica contactar al negocio para revisión de la dueña. No se fusionan
  historiales automáticamente.
- El acceso obtenido por esta vía no incluye observaciones ni notas internas de la ficha.

---

### Requerimiento Funcional 25: Recuperar contraseña

**Descripción:** Permite que un cliente con cuenta restablezca su contraseña mediante un enlace enviado a su
email verificado.

**Inputs:** Email verificado del cliente.

**Processing:**

1. El cliente solicita la recuperación desde el ingreso de sesión (RF 15).
2. Se envía un enlace de recuperación válido por 30 minutos y de un solo uso.
3. El cliente establece una nueva contraseña desde el enlace.

**Outputs:** Contraseña actualizada.

**Error Handling:**

- Un enlace ya utilizado o con más de 30 minutos no permite cambiar la contraseña; debe solicitarse uno nuevo.
- La dueña no ve ni asigna contraseñas.
- Si la persona perdió el acceso a su email, se deriva a atención asistida por la dueña; esta versión no
  incorpora un cambio automático de titularidad de cuenta.

---

### Requerimiento Funcional 26: Cambiar horario fijo del abono

**Descripción:** Permite que la dueña registre, a pedido del alumno, el cambio de los días u horarios fijos de su
abono, sujeto a cupo disponible.

**Inputs:** Alumno, horario fijo actual, nuevo horario fijo elegido de la grilla, fecha de vigencia.

**Processing:**

1. La dueña recibe el pedido del alumno y elige el nuevo horario fijo entre las clases de la grilla (RF 9).
2. Se verifica que el nuevo horario tenga cupo disponible.
3. Se registra el cambio con la fecha de vigencia elegida por la dueña.
4. Desde la fecha de vigencia, se libera el lugar del horario anterior y las listas semanales se generan con el
   nuevo horario (RF 19).

**Outputs:** Horario fijo del abono actualizado desde la fecha de vigencia.

**Error Handling:**

- Si el nuevo horario no tiene cupo, el cambio no se registra y el alumno conserva sus horarios actuales; nunca
  queda sin lugar durante el cambio.
- Las fechas anteriores a la vigencia, con sus asistencias, cancelaciones y suspensiones, no se modifican.
- El cambio no altera el vencimiento, el día de referencia ni el precio del abono (RF 29).

---

### Requerimiento Funcional 27: Anular cobro

**Descripción:** Permite que la dueña anule un cobro registrado por error o para corregirlo, sin editarlo ni
borrarlo.

**Inputs:** Cobro a anular, motivo.

**Processing:**

1. Se recupera el cobro a anular.
2. Se registra la anulación con motivo, fecha y responsable, enlazada al cobro original.
3. El cobro original se conserva en el historial junto con su anulación.

**Outputs:** Cobro anulado, con su historial conservado. Excluido del resultado del período (RF 13).

**Error Handling:**

- Un cobro no puede editarse ni borrarse directamente; solo anularse.
- Si corresponde, la dueña registra el cobro correcto por separado (RF 5).

---

### Requerimiento Funcional 28: Registrar devolución manual

**Descripción:** Permite que la dueña registre una devolución de dinero realizada fuera del sistema, enlazada al
cobro original.

**Inputs:** Cobro original, importe, fecha, motivo.

**Processing:**

1. Se recupera el cobro original al que corresponde la devolución.
2. Se registra la devolución con importe, fecha y motivo, enlazada a ese cobro.

**Outputs:** Devolución registrada, descontada del resultado del período en que se registra (RF 13).

**Error Handling:**

- No se calcula automáticamente el importe a devolver, saldo a favor ni crédito.
- La política de cuándo devolver la gestiona la dueña fuera de WorkUp.

---

### Requerimiento Funcional 29: Renovar o dar de baja el abono

**Descripción:** Gestiona el vencimiento y la renovación del abono mensual: calcula su vencimiento, conserva el
lugar del alumno hasta que la dueña resuelva y registra la renovación o la baja.

**Inputs:** Abono del alumno, fecha de pago o de baja.

**Processing:**

1. El abono dura un mes desde su alta, que ocurre cuando se registra su primer cobro (RF 5). Vence el mismo
   número de día del mes siguiente; si ese día no existe, vence el último día de ese mes.
2. La fecha de vencimiento es el primer día sin cobertura: el período cubre desde el alta hasta el día anterior
   al vencimiento inclusive, sin depender de la hora del pago.
3. Al renovar sin interrupción, se registra el cobro (RF 5) y se conserva el día de referencia original; la
   nueva cobertura comienza al vencimiento del período anterior, aunque el pago sea anticipado.
4. Al renovar tarde, la nueva cobertura comienza el día del pago, sin cobertura ni cobro retroactivos por el
   intervalo vencido; el día de referencia pasa a ser el día del pago.
5. Al vencer sin renovar, el abono queda vencido; el lugar fijo se conserva pendiente de resolución y no se
   ofrece a otra persona (RF 19).
6. La dueña registra la renovación o da de baja la inscripción; al dar de baja, se liberan los lugares fijos. La
   baja no elimina la ficha del cliente, su cuenta ni su historial.

**Outputs:** Abono vigente con su nuevo vencimiento, o inscripción dada de baja con los lugares liberados.

**Error Handling:**

- Conservar el lugar no habilita asistencia sin abono vigente; el alumno debe renovar antes de volver a asistir
  (RF 10).
- Si el alumno vuelve tras una baja, se registra como una nueva inscripción sujeta a cupo disponible (RF 4), sin
  garantía de recuperar sus horarios anteriores.
- Si la inscripción todavía conserva sus lugares y solo venció el abono, corresponde renovar, no crear una
  inscripción nueva.

---

### Requerimiento Funcional 30: Enviar aviso de vencimiento de abono

**Descripción:** Avisar por email al alumno sobre el vencimiento próximo o vencido de su abono, sin insistencia
diaria ni anuncio de liberación automática del lugar.

**Inputs:** Abonos con vencimiento próximo o vencido, fecha de vencimiento (RF 29).

**Processing:**

1. Tres días antes del vencimiento, enviar un email recordando la fecha.
2. Al vencer, si el alumno todavía no renovó, enviar un email informando el vencimiento.

**Outputs:** Avisos de vencimiento enviados al alumno.

**Error Handling:**

- Si el alumno renovó antes del vencimiento, no se envía el aviso de abono vencido correspondiente a ese
  período.
- No se envían mensajes diarios ni se anuncia liberación automática del lugar; la conservación y liberación
  manual se rigen por RF 29.
