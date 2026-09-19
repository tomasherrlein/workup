Universidad Tecnológica Nacional – Facultad Regional General Pacheco 

Técnico Universitario en Programación 

**Relevamiento WorkUp** 

Para el relevamiento se tomaron dos casos de negocios que trabajan con turnos: una peluquería, donde se atiende de a un cliente por turno, y un estudio de yoga, donde las clases son grupales y tienen un cupo limitado. Se eligieron estos dos casos porque cubren las dos formas de atención que se dan en el rubro. 

**Peluquería** 

La peluquería de Marina Gómez ofrece servicios de corte, coloración y peinado. Trabajan Marina, la dueña, y Sofía Paz, que es una empleada, y cada una atiende sus propios turnos en un local con cuatro puestos de trabajo. 

Marina mantiene la lista de precios pegada en el espejo con el servicio y el precio, y la actualiza a mano con cada aumento. Cada servicio tiene una duración fija, que es el tiempo que la trabajadora queda ocupada con esa atención.

Marina guarda en una carpeta los datos de cada trabajadora: nombre, apellido, teléfono y CUIL, junto con su horario de atención (días de trabajo, hora de inicio/fin y pausas no laborables), y los actualiza cuando cambia un horario.

Cuando un cliente quiere atenderse, entra al link de reserva que le envía la peluquería. Elige el servicio y con cuál de las dos se quiere atender, y el sistema le muestra los horarios libres de esa trabajadora, calculados sobre su horario de atención, descontando sus pausas no laborables y los turnos que ya tiene tomados, y verificando que entre la duración del servicio. El cliente elige uno de esos horarios y el turno queda reservado. Marina también puede cargar un turno ella misma cuando un cliente le escribe por WhatsApp. Cada turno registrado incluye: fecha, hora de inicio, servicio, trabajadora, cliente y estado. 

Dos turnos pueden coincidir en el mismo horario si corresponden a trabajadoras distintas.

Para reservar, el cliente necesita una cuenta. Si es nuevo, registra su nombre, su apellido, su email, un teléfono de contacto y una contraseña; si ya tiene cuenta, inicia sesión y el turno nuevo se agrega a su ficha. Los clientes se identifican por su email. El teléfono queda como dato de contacto, para que Marina pueda comunicarse con ellos cuando lo necesita. En la ficha del cliente Marina anota además las observaciones que le sirven para la próxima atención, como el tono de tintura que le aplicó o las alergias que haya declarado.

Desde su cuenta, cada cliente entra a su propio panel, donde ve los turnos y las clases que tiene reservados, con su fecha, su horario, el servicio y la trabajadora. Desde ahí puede cancelar un turno, y en ese caso el horario queda libre para otra persona, o reprogramarlo eligiendo otro de los horarios libres de esa misma trabajadora. Si ninguno de los horarios disponibles le sirve, cancela el turno. 

Cada mañana Marina repasa la agenda de turnos para ver a quiénes atienden ese día. El día anterior a cada turno el sistema le envía al cliente un mail recordándole el turno y pidiéndole que confirme que va a venir. Si confirma, el turno queda confirmado. Si avisa que no puede, o lo cancela desde su panel, el turno se cancela y ese horario queda libre para ofrecérselo a otra persona. 

Al terminar el turno del cliente se le cobra y Marina anota el monto en el cuaderno de movimientos. Los cobros se encuentran identificados por: fecha, hora, monto, servicio y trabajador que atendió. No entrega ningún comprobante al cliente. Si el cliente no se presenta y tampoco avisa, el turno se anota como ausente, y al final del mes se hace un recuento de turnos perdidos por ausencias.

También llegan clientes sin turno a preguntar si los pueden atender. Marina observa cuántos de los cuatro puestos están ocupados y, si hay uno libre, lo atiende y luego anota el cobro en el cuaderno. Si están todos ocupados le ofrece volver más tarde o sacar turno para otro día. Estas atenciones no las anota en la libreta, porque la libreta la utiliza solo para los turnos agendados. Por eso en el cuaderno quedan cobros que no coinciden con ningún turno. 

En el mismo cuaderno registra los gastos. Los gastos se encuentran identificados por: fecha, monto, tipo de gasto y descripción. Los tipos de gasto son seis: insumos, alquiler, servicios, transporte, impuestos y tasas, y varios. 

A fin de mes Marina suma con la calculadora todos los cobros, les resta todos los gastos y obtiene un reporte del período. 

Para trabajar usa insumos que se van consumiendo, como tinturas, shampoo, oxidantes y guantes. No lleva registro de cuánto tiene de cada uno ni de cuánto usa. Al terminar una atención recuerda qué productos utilizó, pero no lo anota en ningún lado, y el consumo varía de una atención a otra. Se da cuenta de que le falta un producto cuando abre el cajón y ve que queda poco. 

Marina no dispone de información consolidada del negocio. No puede establecer qué servicio le deja más margen, qué clientas vuelven con frecuencia y cuáles dejaron de venir, ni cuánto trabajó cada una. 

**Estudio de yoga** 

El estudio de yoga dicta clases grupales. Trabajan Lucía Fernández, la dueña, y Julieta Ríos, una profesora que dicta sus propias clases. Como el estudio tiene dos salas, ambas pueden dar clase en el mismo horario.

Lucía registra previamente en el tarifario los tipos de clases disponibles, definiendo su descripción, precio, duración y el cupo máximo de alumnos permitido según el espacio.  Actualmente el estudio cuenta con clases de hatha y vinyasa.

Cada profesora tiene su disponibilidad dentro del horario del estudio. Lucía lleva una planilla de profesoras con el nombre, el apellido, el teléfono y el CUIL de cada una y su disponibilidad horaria. La actualiza cuando algo cambia y la usa para armar la grilla.

Lucía arma de antemano una grilla semanal que se repite todas las semanas y la deja publicada, y los alumnos eligen sus clases. Para cada clase de la grilla define el tipo de clase, el día de la semana, el horario y la profesora. La revisa cuando cambia la temporada o la disponibilidad de alguna profesora.  

Cuando una persona se inscribe por primera vez, ya sea con un pase mensual o para una clase suelta, se registran en el registro de alumnos su nombre, su apellido, su email, un teléfono de contacto y las observaciones de salud relevantes, como lesiones o un embarazo en curso. Igual que en la peluquería, el alumno se identifica por su email y accede con su cuenta a su propio panel, donde ve las clases en las que está anotado.

Para asistir de forma regular, los alumnos pagan un pase mensual. Al inscribirse, cada alumno elige cuántas veces por semana quiere asistir y, entre las clases de la grilla que tienen lugar, cuáles van a ser sus clases fijas. Lucía lo incorpora a la lista fija de esas clases y el lugar le queda reservado todas las semanas. El precio del pase se calcula según la frecuencia semanal y las clases fijas elegidas. Se paga el día de la inscripción y cubre un mes desde esa fecha, por lo que el alumno vuelve a pagar el mismo día del mes siguiente. En la planilla de pases Lucía anota el alumno, la frecuencia, las clases fijas, la fecha de pago, el monto y la fecha de vencimiento. Si el alumno no renueva al vencimiento, sale de las listas fijas y su lugar queda disponible para otra persona. 

Las clases del pase mensual no se recuperan. Si un alumno falta, haya avisado o no, pierde esa clase y se lo registra como ausente. Avisar con anticipación, o cancelar la clase desde su panel, sirve para liberar el lugar esa semana, de modo que Lucía pueda ofrecérselo a otra persona.

Las clases no requieren un cupo mínimo de alumnos para dictarse (se dictan aunque asista un solo alumno). Cuando una clase no se puede dictar una semana puntual, por un feriado o porque la profesora no puede, no se modifica la grilla fija. Lucía revisa la lista de esa clase para saber qué alumnos estaban anotados y les avisa por mail, uno por uno. La clase suspendida no se recupera y tampoco se devuelve el dinero: los alumnos con pase mensual la pierden igual que si hubieran faltado, y quien estaba anotado para una clase suelta simplemente no la paga, porque las clases sueltas se abonan al llegar. Lucía quisiera que, al suspender una clase, el sistema le indique qué alumnos estaban anotados y le deje el aviso redactado y listo para enviar, sin tener que buscar cada contacto ni escribir cada mensaje. 

Quien no tiene pase mensual puede asistir a una clase suelta. Entra por el mismo link de reserva y ve las clases de la semana en las que queda lugar, contando los lugares fijos y los avisos de falta, y se anota para esa fecha. Paga la clase al llegar al estudio. Si la clase que quería está completa, el sistema le muestra las otras clases con lugar.

Cada profesora toma asistencia en su clase sobre la lista de esa semana. En la lista figuran la fecha, los alumnos anotados (fijos y clases sueltas) y si cada uno asistió o faltó. La profesora también cobra a quienes asisten sin pase mensual. Al terminar su turno, Julieta le entrega a Lucía la lista y lo cobrado en sus clases, y Lucía anota cada cobro en su registro de movimientos con la fecha, el alumno y el monto.

Para las clases usan mats, bandas elásticas, cintas y toallas. Son elementos que se reutilizan y Lucía no lleva registro de su estado: los repone cuando los ve gastados. En el registro de movimientos anota también cada gasto del estudio con la fecha, el monto y el tipo (alquiler, servicios, reposición de estos elementos, impuestos). A fin de mes suma a mano lo cobrado por pases mensuales y por clases sueltas, le resta los gastos y obtiene el resultado del período.

## Requerimientos funcionales

---

### Requerimiento Funcional 1: Registrar nuevo cliente

**Descripción:** Permite dar de alta a un cliente nuevo en el sistema para asociarle turnos, inscripciones u observaciones.

**Inputs:** Nombre, apellido, teléfono, email y observaciones.

**Processing:**

1. Se ingresan los datos personales del cliente.
2. Se verifica que el email ingresado no exista previamente en el registro de clientes.
3. Se registran los datos personales y las observaciones.

**Outputs:** Mensaje de confirmación y ficha de cliente.

**Error Handling:**

- Si el cliente ya está registrado, el sistema muestra un mensaje de error.
- Si falta ingresar el teléfono, el email o el nombre, el sistema impide el guardado e indica los campos obligatorios incompletos.
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

**Descripción:** Permite registrar la reserva de un turno individual para un servicio específico con una trabajadora, evaluando huecos libres.

**Inputs:** Email del cliente, servicio, trabajadora, fecha y hora solicitada.

**Processing:**

1. El sistema busca al cliente por el email ingresado.
2. Se consulta el horario de atención de la trabajadora elegida (días de trabajo, hora de inicio y fin) para verificar que el turno esté dentro de su jornada.
3. Se busca en la agenda un espacio libre que alcance para la duración total del servicio.
4. Se guarda el turno incluyendo: código de turno, fecha, hora de inicio, servicio, trabajadora, cliente y estado.

**Outputs:** Confirmación del turno y nuevo turno.

**Error Handling:**

- Si no hay espacio libre suficiente de la misma trabajadora, el sistema rechaza la reserva y ofrece el horario libre más cercano que tenga esa misma persona.

---

### Requerimiento Funcional 4: Inscripción a Clases (Estudio de Yoga)

**Descripción:** Se da de alta un cliente a una clase particular o durante todo el mes a la cantidad semanal que desee, en las clases fijas que el cliente seleccione.

**Inputs:** Email del cliente, frecuencia semanal elegida y clases fijas seleccionadas de la grilla.

**Processing:**

1. El sistema verifica que exista cupo disponible de manera permanente en las clases fijas seleccionadas de la grilla, o en la clase particular que eligió.
2. El sistema incorpora al alumno de forma automática a la lista fija de esas clases, reservando su lugar de manera recurrente todas las semanas del mes.

**Outputs:** Cupos reservados a nombre del cliente.

**Error Handling:**

- Si alguna de las clases elegidas ya alcanzó su capacidad máxima y no tiene vacantes fijas, el sistema emite el mensaje "Cupo permanente no disponible" y solicita elegir otra clase u horario.
- Si la cantidad de clases fijas seleccionadas supera la frecuencia semanal elegida para el abono, el sistema impide guardar el registro.

---

### Requerimiento Funcional 5: Registrar abono

**Descripción:** Se calcula el importe a pagar y se registran los datos del pago.

**Inputs:** Email del cliente, pago efectuado, fecha, mes pago.

**Processing:**

1. Se busca al alumno en el registro de alumnos a través de su email.
2. Si es su primera inscripción, darlo de alta (RF 1).
3. Revisar las clases fijas elegidas que hay en la grilla.
4. El sistema calcula el importe total a pagar del cliente según la o las clases en que está inscripto.
5. Se registra el pago con el email, el monto, los códigos de clases pagas, la fecha y el mes pago.

**Outputs:** Pago registrado.

**Error Handling:** N/A.

---

### Requerimiento Funcional 6: Enviar recordatorio de turno

**Descripción:** Avisar al cliente de su turno o clase un día antes.

**Inputs:** Turnos y clases próximos, anticipación configurada (24 horas por defecto).

**Processing:**

1. Al cumplirse la anticipación configurada, recuperar los turnos y clases alcanzados.
2. Emitir el aviso a cada cliente pendiente.

**Outputs:** Recordatorio enviado al cliente.

**Error Handling:** N/A.

---

### Requerimiento Funcional 7: Confirmar turno

**Descripción:** El día anterior a cada turno, se le pide al cliente que confirme su asistencia.

**Inputs:** Turnos del día siguiente (cliente, teléfono, fecha, hora, servicio y trabajadora) y respuesta del cliente.

**Processing:**

1. El día anterior, se obtienen los turnos agendados para el día siguiente.
2. Se le envía a cada cliente un mensaje para confirmar que va a asistir.
3. Si el cliente confirma, el turno pasa a estado confirmado.
4. Si el cliente avisa que no puede asistir, el turno pasa a estado cancelado y ese horario queda libre en la agenda de la trabajadora.

**Outputs:** Mensaje de confirmación enviado y turno confirmado.

**Error Handling:**

- Si en lugar de cancelarlo quiere pasarlo a otro horario, se reprograma (RF de reprogramación).

---

### Requerimiento Funcional 7: Registrar trabajadora

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

### Requerimiento Funcional 8: Definir clase en la grilla semanal

**Descripción:** Registrar o modificar en la grilla semanal qué tipo de clase se dicta, qué día, a qué horario y con qué profesora.

**Inputs:** Tipo de clase, descripción, precio, duración y cupo máximo de alumnos.

**Processing:**

1. Recibir los datos de la clase.
2. Se verifica la disponibilidad horaria de la profesora seleccionada.
3. Registrar la clase en la grilla semanal, que se repite todas las semanas.
4. Publicar la grilla actualizada.

**Outputs:** Clase incorporada a la grilla semanal publicada.

**Error Handling:**

- Si el horario queda fuera de la disponibilidad de la profesora, no se registra.
