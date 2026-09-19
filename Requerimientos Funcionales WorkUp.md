# Requerimientos Funcionales WorkUp

**Equipo:** Bapton Solutions — **Fecha:** 15-09-2026

Del RF 1 al RF 13 se mantiene el orden y la numeración del documento del grupo. Del RF 14 al RF 20 siguen los requerimientos que faltaban. La numeración no indica orden de ejecución.

---

### Requerimiento Funcional 1: Registrar nuevo cliente

**Descripción:** Permite dar de alta a un cliente nuevo en el sistema para asociarle turnos, inscripciones u observaciones.

**Inputs:** Nombre, apellido, teléfono, email, contraseña y observaciones.

**Processing:**

1. Se ingresan los datos personales del cliente.
2. Se verifica que el email ingresado no exista previamente en el registro de clientes.
3. Se registran los datos personales, las observaciones y las credenciales de acceso.

**Outputs:** Mensaje de confirmación y ficha de cliente.

**Error Handling:**

- Si el cliente ya está registrado, el sistema muestra un mensaje de error.
- Si falta ingresar el teléfono, el email, el nombre o la contraseña, el sistema impide el guardado e indica los campos obligatorios incompletos.
- Si el teléfono o el email no tienen un formato válido, se pide corregirlo.

---

### Requerimiento Funcional 2: Registrar y configurar servicios

**Descripción:** Permite dar de alta los servicios que ofrece el negocio, definiendo las duraciones de los servicios o los cupos de las clases.

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

**Descripción:** Permite registrar la reserva de un turno individual para un servicio específico con una trabajadora, evaluando huecos libres. El cliente reserva desde el link que le envía el negocio, o la dueña carga el turno cuando el cliente le escribe.

**Inputs:** Email del cliente, servicio, trabajadora, fecha y horario elegido entre los disponibles.

**Processing:**

1. El sistema busca al cliente por el email ingresado.
2. Se consulta el horario de atención de la trabajadora elegida (días de trabajo, hora de inicio y fin, pausas no laborables) para verificar que el turno esté dentro de su jornada.
3. Se busca en la agenda un espacio libre que alcance para la duración total del servicio y se muestran los horarios disponibles.
4. Se guarda el turno incluyendo: código de turno, fecha, hora de inicio, servicio, trabajadora, cliente y estado.

**Outputs:** Confirmación del turno y nuevo turno.

**Error Handling:**

- Si no hay espacio libre suficiente de la misma trabajadora, el sistema rechaza la reserva y ofrece el horario libre más cercano que tenga esa misma persona.

---

### Requerimiento Funcional 4: Inscripción a Clases (Estudio de Yoga)

**Descripción:** Se da de alta un cliente a una clase suelta, o a las clases fijas de la grilla según la cantidad semanal que desee. La inscripción a clases fijas no requiere pago por adelantado: el alumno queda anotado en la primera de esas clases y el abono comienza cuando paga.

**Inputs:** Email del cliente, frecuencia semanal elegida y clases fijas seleccionadas de la grilla, o clase suelta elegida.

**Processing:**

1. El sistema busca al cliente por su email; si es su primera inscripción, se lo da de alta (RF 1).
2. El sistema verifica que exista cupo disponible de manera permanente en las clases fijas seleccionadas de la grilla, o cupo en la clase suelta que eligió.
3. Se registra la inscripción con la frecuencia semanal y las clases fijas elegidas, en estado pendiente de pago.
4. Se anota al alumno únicamente en la primera de sus clases fijas, o en la clase suelta elegida. El lugar de las semanas siguientes queda reservado recién cuando registra el pago (RF 5).

**Outputs:** Inscripción registrada y alumno anotado en esa clase, pendiente de pago.

**Error Handling:**

- Si alguna de las clases elegidas ya alcanzó su capacidad máxima y no tiene vacantes fijas, el sistema emite el mensaje "Cupo permanente no disponible" y solicita elegir otra clase u horario.
- Si la cantidad de clases fijas seleccionadas supera la frecuencia semanal elegida para el abono, el sistema impide guardar el registro.

---

### Requerimiento Funcional 5: Registrar cobro

**Descripción:** Se calcula el importe a cobrar y se registran los datos del cobro, sea por una atención de la peluquería, por una clase suelta o por un abono mensual. Cuando el cobro corresponde a un abono, el abono comienza en esa fecha y el alumno queda incorporado a sus listas fijas.

**Inputs:** Email del cliente, concepto cobrado (atención, clase suelta o abono), monto, pago efectuado, fecha.

**Processing:**

1. Se busca al alumno o cliente en el registro a través de su email.
2. Si es su primera inscripción, darlo de alta (RF 1).
3. El sistema calcula el importe total a cobrar según el concepto:
    - Atención de la peluquería: el precio del servicio del turno atendido, tomado del tarifario.
    - Clase suelta: el precio del tipo de clase, tomado del tarifario.
    - Abono mensual: el importe correspondiente a la frecuencia semanal y las clases fijas en que está inscripto, revisando las clases fijas elegidas que hay en la grilla.
4. Se registra el cobro con el email, el monto, el concepto cobrado y la fecha. En la peluquería se registran además la hora, el servicio y la trabajadora que atendió; en el estudio, la clase cobrada.
5. Si el cobro corresponde a un abono, se incorpora al alumno a la lista fija de esas clases y el abono cubre un mes desde la fecha de pago.

**Outputs:** Cobro registrado en el registro de movimientos y, cuando corresponde, abono vigente con el alumno en sus listas fijas.

**Error Handling:**

- Si el alumno no paga al llegar a su primera clase, la inscripción queda sin efecto: no se lo incorpora a las listas fijas y su lugar queda disponible para otra persona.
- El cobro de una atención sin turno se registra mediante el RF 11, que crea el movimiento sin turno asociado.

---

### Requerimiento Funcional 6: Enviar recordatorio de turno

**Descripción:** Avisar al cliente de su turno o clase un día antes.

**Inputs:** Turnos y clases próximos, anticipación configurada (24 horas por defecto).

**Processing:**

1. Al cumplirse la anticipación configurada, recuperar los turnos y clases alcanzados.
2. Emitir el aviso al mail de cada cliente pendiente.

**Outputs:** Recordatorio enviado al cliente.

**Error Handling:** N/A.

---

### Requerimiento Funcional 7: Confirmar turno

**Descripción:** El día anterior a cada turno, se le pide al cliente que confirme su asistencia.

**Inputs:** Turnos del día siguiente (cliente, email, fecha, hora, servicio y trabajadora) y respuesta del cliente.

**Processing:**

1. El día anterior, se obtienen los turnos agendados para el día siguiente.
2. Se le envía a cada cliente un mensaje para confirmar que va a asistir.
3. Si el cliente confirma, el turno pasa a estado confirmado.
4. Si el cliente avisa que no puede asistir, el turno pasa a estado cancelado y ese horario queda libre en la agenda de la trabajadora.

**Outputs:** Mensaje de confirmación enviado y turno confirmado.

**Error Handling:**

- Si en lugar de cancelarlo quiere pasarlo a otro horario, se reprograma (RF 17).

---

### Requerimiento Funcional 8: Registrar trabajadora

**Descripción:** Registrar y modificar trabajadora con sus datos, definiendo sus horarios de atención o disponibilidad semanal para la asignación de turnos o clases.

**Inputs:** Nombre, apellido, teléfono, CUIL, días de trabajo, hora de inicio y de fin por día, pausas no laborables.

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

**Descripción:** Registrar o modificar en la grilla semanal qué tipo de clase se dicta, qué día, a qué horario y con qué profesora.

**Inputs:** Tipo de clase, día de la semana, horario y profesora.

**Processing:**

1. Recibir los datos de la clase.
2. Se verifica la disponibilidad horaria de la profesora seleccionada.
3. Registrar la clase en la grilla semanal, que se repite todas las semanas.
4. Publicar la grilla actualizada.

**Outputs:** Clase incorporada a la grilla semanal publicada.

**Error Handling:**

- Si el horario queda fuera de la disponibilidad de la profesora, no se registra.

---

### Requerimiento Funcional 10: Registrar asistencia

**Descripción:** Registrar si cada cliente asistió a su turno o si cada alumno asistió a su clase.

**Inputs:** Turno o clase y fecha, cliente, asistencia.

**Processing:**

1. El sistema recupera el turno individual o la lista de la clase grupal correspondientes a la fecha seleccionada.
2. Si es un turno individual y el cliente asistió, se marca el turno como "Atendido"; si no asistió, se marca como "Ausente".
3. Si es una clase grupal, el sistema recorre la lista de alumnos anotados para esa fecha. Se marca como "Atendido" a los alumnos que hayan asistido y "Ausente" a los que no.
4. Se guarda el registro de asistencia.

**Outputs:** Turno o lista de la clase con la asistencia registrada.

**Error Handling:**

- Si el alumno falta a la clase, se lo registra como ausente. Si tiene abono, pierde esa clase, haya avisado o no.

---

### Requerimiento Funcional 11: Registrar atención sin turno

**Descripción:** Registrar la atención y el cobro de un cliente que llega sin turno.

**Inputs:** Servicio, trabajadora, fecha, hora y monto.

**Processing:**

1. Se ingresan los datos de la atención.
2. Verificar que haya un puesto disponible en ese momento.
3. Registrar el cobro con fecha, hora, monto, servicio y trabajadora que atendió, sin turno asociado.

**Outputs:** Cobro registrado en el registro de movimientos.

**Error Handling:**

- Si todos los puestos están ocupados, no se registra y se ofrece volver más tarde o sacar un turno.

---

### Requerimiento Funcional 12: Suspensión de clases y notificación

**Descripción:** Suspender la clase de una semana puntual y dejar preparado el aviso para cada alumno anotado.

**Inputs:** Clase y fecha.

**Processing:**

1. Recibir la clase cancelada con su fecha.
2. Marcar la clase de esa fecha como suspendida, sin alterar la grilla.
3. Identificar a los alumnos anotados para esa clase, tanto fijos como sueltos.
4. Redactar para cada alumno un mensaje de aviso listo para enviar.

**Outputs:** Clase suspendida, lista de alumnos anotados y mensajes de aviso redactados.

**Error Handling:**

- Si no hay alumnos anotados, se suspende la clase sin redactar mensajes.
- La clase suspendida no se recupera ni se devuelve el dinero: los alumnos con abono la pierden igual que si hubieran faltado y quien iba a una clase suelta no la paga, porque abona al llegar.

---

### Requerimiento Funcional 13: Reporte del período

**Descripción:** Obtener el resultado de un período sumando los cobros y restando los gastos, junto con los indicadores del negocio.

**Inputs:** Período a consultar.

**Processing:**

1. Recibir el período.
2. Sumar los cobros de atenciones con turno, atenciones sin turno, clases sueltas y abonos del período.
3. Restar los gastos del período.
4. Calcular los indicadores del negocio: margen por servicio, clientes que vuelven con frecuencia, clientes que dejaron de venir, recuento de turnos perdidos por ausencia y trabajo de cada trabajadora.

**Outputs:** Reporte del resultado del período e indicadores del negocio.

**Error Handling:**

- Si el período es posterior a la fecha actual, no se calcula y se solicita corregir el período.

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

**Descripción:** Permite que el cliente acceda a su panel con sus credenciales.

**Inputs:** Email y contraseña.

**Processing:**

1. Se verifica que exista una cuenta registrada con ese email.
2. Se valida la contraseña ingresada.
3. Se habilita el acceso al panel del cliente.

**Outputs:** Sesión iniciada.

**Error Handling:**

- Si el email no está registrado o la contraseña no coincide, no se habilita el acceso y se informa que los datos son incorrectos.

---

### Requerimiento Funcional 16: Consultar mis turnos

**Descripción:** Muestra al cliente los turnos y las clases que tiene reservados.

**Inputs:** Cliente con sesión iniciada.

**Processing:**

1. Se recuperan los turnos y las clases del cliente con su fecha, horario, servicio, trabajadora y estado.

**Outputs:** Detalle de los turnos y clases del cliente.

**Error Handling:** N/A.

---

### Requerimiento Funcional 17: Reprogramar turno

**Descripción:** Pasar un turno individual a otro horario libre de la misma trabajadora, a pedido del cliente desde su panel o de la dueña.

**Inputs:** Turno a reprogramar, nuevo horario elegido entre los disponibles.

**Processing:**

1. Se recupera el turno a reprogramar.
2. Se calculan los horarios libres de esa misma trabajadora que alcancen para la duración del servicio.
3. Se muestran los horarios disponibles.
4. Se recibe el horario elegido y se cambian la fecha y la hora del turno, liberando el horario anterior.

**Outputs:** Turno con su nueva fecha y hora, y confirmación al cliente.

**Error Handling:**

- Si no hay ningún horario de esa trabajadora que le sirva al cliente, el turno se cancela (RF 18).
- Las clases grupales no se reprograman: se cancelan y el alumno se anota en otra clase con lugar.

---

### Requerimiento Funcional 18: Cancelar turno o clase

**Descripción:** Registrar que el cliente no va a asistir, sea porque lo cancela desde su panel o porque avisa al negocio, liberando el lugar.

**Inputs:** Cliente, turno o clase a cancelar.

**Processing:**

1. Se recupera el turno o la clase elegida.
2. Se registra el turno o la anotación como cancelada.
3. El horario o el lugar queda disponible para otra persona.

**Outputs:** Turno cancelado y lugar liberado.

**Error Handling:**

- Si se trata de una clase fija del abono, se libera el lugar solo para esa semana: la clase no se recupera y no se devuelve el dinero.

---

### Requerimiento Funcional 19: Generar las clases de la semana

**Descripción:** Crear las clases de una semana a partir de la grilla y anotar en ellas a los alumnos que las tienen como clases fijas.

**Inputs:** Grilla semanal, semana a generar, clases fijas de los alumnos, abonos vigentes.

**Processing:**

1. Se recorren las clases de la grilla.
2. Se crea una clase por cada una, con su fecha, su profesora y su cupo máximo.
3. Se anota en cada clase a los alumnos que la tienen como clase fija y tienen el abono vigente.

**Outputs:** Lista de cada clase de la semana, con su fecha y sus alumnos fijos anotados.

**Error Handling:**

- Si el alumno no renovó su abono al vencimiento, no se lo anota y su lugar queda disponible para otra persona.

---

### Requerimiento Funcional 20: Consultar la agenda del día

**Descripción:** Mostrar a quiénes se atiende ese día, con su horario, servicio, trabajadora y estado.

**Inputs:** Fecha de la jornada.

**Processing:**

1. Se recuperan los turnos y las clases de esa fecha con sus anotados y sus estados.

**Outputs:** Detalle de la jornada.

**Error Handling:** N/A.
