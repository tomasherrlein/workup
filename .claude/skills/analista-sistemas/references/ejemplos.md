# Ejemplos de Referencia — Metodología Casanovas

## Ejemplo Completo: Sistema de Gestión de Biblioteca

### Requerimiento del Cliente (Relevamiento)
El bibliotecario gestiona el catálogo de libros por ISBN. Para préstamos, el socio presenta carnet. El sistema controla disponibilidad, registra préstamos (1 semana máx.) y devoluciones. Sanciones por devolución fuera de término. Baja de socios con más de 5 sanciones acumuladas al fin de mes.

---

### SRS IEEE 830 — Sistema de Gestión de Biblioteca (v1.0 / 20-02-2020)

**RF#1: DAR DE ALTA CATÁLOGO**
- Descripción: El bibliotecario registra libros en el catálogo por ISBN
- Inputs: ISBN, Título, Autor/es, Nº de ejemplar, Año de Edición, Categoría temática, Estado (disponible/en préstamo)
- Proceso: Se ingresan los datos. Se registran en el almacenamiento. El Nº de inventario se genera automáticamente en forma secuencial.
- Outputs: Pantalla muestra registro completo: datos de entrada + Nº de inventario
- Error Handling: El sistema valida formato y rango de datos. En caso de error, muestra mensaje por pantalla y no registra hasta que los datos sean correctos.

**RF#2: DAR DE ALTA SOCIOS**
- Inputs: DNI, teléfono, e-mail
- Proceso: Se ingresa DNI → se verifica que no esté registrado ni tenga sanciones → si no hay inconvenientes, se ingresan datos de contacto → se registra nuevo socio → se muestra confirmación
- Outputs: Se imprime el carnet del nuevo socio
- Error Handling: Si ya está registrado o tiene sanciones: mensaje de error correspondiente y cancelación del proceso

**RF#3: REGISTRAR PRÉSTAMO**
- Inputs: DNI, Título, Autor
- Proceso: Ingreso título, autor y DNI → verificar ejemplar disponible → actualizar estado del libro (en préstamo) → registrar préstamo (DNI, ISBN, Nº Ejemplar, fecha retiro, fecha devolución = fechahoy + 7 días) → mostrar confirmación en pantalla
- Outputs: Comprobante de préstamo impreso (con fecha y número) para el socio
- Error Handling: Si no hay ejemplar disponible → mensaje de error por pantalla, cancelar transacción

**RF#4: REGISTRAR DEVOLUCIÓN**
- Inputs: DNI, Nº de comprobante de préstamo
- Proceso: Registrar devolución → actualizar estado del ejemplar (disponible) → controlar si fechahoy > fecha máxima de devolución → si es así, registrar sanción
- Outputs: Registro de devolución, actualización estado del libro, sanción al socio si corresponde
- Error Handling: Controla fecha; si excede, registra sanción y avisa al socio

**RF#5: DAR DE BAJA SOCIO**
- Inputs: DNI, sanciones (proceso automático mensual)
- Proceso: El último día del mes el sistema recorre el almacenamiento de Sanciones → tabula sanciones acumulativas por socio → si superan 5, cambia estado socio a "baja por sanciones" → avisa al socio → emite reporte de bajas
- Outputs: Baja de socio, reporte de bajas
- Error Handling: N/A

**Requerimientos No Funcionales**: no fueron determinados en este relevamiento

---

### Contra-ejemplo: Esto NO es un RF separado

En el ejemplo de la biblioteca, "Verificar si el socio tiene sanciones" NO es un RF separado. ¿Por qué?

| Criterio | ¿Cumple? | Razón |
|---|---|---|
| Momento propio | NO | Ocurre *durante* el alta de socios, no en un momento aparte |
| Output propio | NO | No produce un documento o resultado propio mencionado en el relevamiento |
| Disparador independiente | NO | Solo se activa como parte de dar de alta un socio |
| Bloque separado | NO | Se describe dentro del mismo párrafo que el alta de socios |

→ No cumple ninguno de los 4 criterios. Va como paso dentro del Proceso de RF#2 (Dar de alta Socios).

**Regla práctica:** Si un candidato a RF no cumple al menos 3 de los 4 criterios, debe integrarse dentro de otro RF como paso del Proceso o como Error Handling.

---

### Lista de Eventos

| EVENTO | PROCESO | ORIGEN/EE | TIPO | FLUJO ACTIVADOR | SALIDA |
|---|---|---|---|---|---|
| El bibliotecario ingresa libros al catálogo | Dar de alta Catálogo | Bibliotecario (EE): ISBN | E | ISBN | Nº Inventario |
| Una persona se asocia a la biblioteca | Dar de alta Socios | Socio (EE): Identificación | E | Identificación | Nuevo socio (carnet) |
| Un socio solicita un libro en préstamo | Registrar préstamo | Socio (EE): Pedido | E | Pedido | Préstamo (comprobante) |
| Un socio devuelve un libro | Registrar devolución | Socio (EE): Devolución | E | Devolución | Devolución, Sanción |
| A fin de mes se dan de baja socios con reiteradas sanciones | Dar de baja Socio | Excede sanciones (almacén) | T | ---- | Baja, Reporte |

---

### DFD Nivel 0 (Diagrama de Contexto)

```
[Socio] →identificación→ (SISTEMA BIBLIOTECA)
[Socio] →pedido→        (SISTEMA BIBLIOTECA)
[Socio] →devolución→    (SISTEMA BIBLIOTECA)
(SISTEMA BIBLIOTECA) →nuevo socio→  [Socio]
(SISTEMA BIBLIOTECA) →préstamo→     [Socio]
(SISTEMA BIBLIOTECA) →no préstamo→  [Socio]
(SISTEMA BIBLIOTECA) →sanción→      [Socio]
(SISTEMA BIBLIOTECA) →baja→         [Socio]
(SISTEMA BIBLIOTECA) →aviso→        [Socio]
[Bibliotecario] →ISBN→              (SISTEMA BIBLIOTECA)
(SISTEMA BIBLIOTECA) →inventario→   [Bibliotecario]
```

---

### DFD Nivel 1

```
[Socio] →identificación, contacto→  (1 Dar de alta Socios)
(1 Dar de alta Socios) →nuevo socio→ [Socio]
(1 Dar de alta Socios) →aviso→       [Socio]
(1 Dar de alta Socios) →socio→       =Socios=
(1 Dar de alta Socios) →estado socio← =Socios=

[Bibliotecario] →ISBN→               (2 Dar de alta Catálogo)
(2 Dar de alta Catálogo) →inventario→ [Bibliotecario]
(2 Dar de alta Catálogo) →nuevo libro→ =Catálogo=

[Socio] →pedido→                     (3 Registrar préstamo)
(3 Registrar préstamo) →préstamo→    [Socio]
(3 Registrar préstamo) →no préstamo→ [Socio]
=Socios= →IdSocio→                   (3 Registrar préstamo)
=Catálogo= →disponibilidad→          (3 Registrar préstamo)
(3 Registrar préstamo) →estado ejemplar→ =Catálogo=
(3 Registrar préstamo) →préstamo→    =Préstamos=

[Socio] →devolución→                 (4 Registrar devolución)
(4 Registrar devolución) →sanción→   [Socio]
=Préstamos= →fechamax→               (4 Registrar devolución)
(4 Registrar devolución) →devolución→ =Préstamos=
(4 Registrar devolución) →estado ejemplar→ =Catálogo=
(4 Registrar devolución) →sanción→   =Sanciones=

=Sanciones= →excede→                 (5 Dar de baja Socio)
(5 Dar de baja Socio) →baja→         [Socio]
(5 Dar de baja Socio) →reporte→      =Reportes=
(5 Dar de baja Socio) →baja→         =Socios=
```

---

### Diccionario de Datos

**ALMACENAMIENTOS:**

| NOMBRE | Atributos | Flujos de Entrada (escritura) | Flujos de Salida (lectura) |
|---|---|---|---|
| CATÁLOGO | ISBN, Título, Autor/es, NºEjemplar, AñoEdición, Categoría, Estado, NºInventario | nuevo libro, estado ejemplar | disponibilidad |
| PRÉSTAMOS | DNI, ISBN, NºEjemplar, FechaRetiro, FechaDevolución | préstamo, devolución | fechamax, excede |
| SOCIOS | IdSocio, NombreSocio, DNI, Direc, TE, mail, EstadoSocio | socio, baja | IdSocio, estado socio |
| SANCIONES | DNI, FechaSanción, Motivo | sanción | excede |
| REPORTES | FechaBaja, DNI, Motivo | reporte | - |

**FLUJOS:**

| NOMBRE FLUJO | Atributos | Origen | Destino |
|---|---|---|---|
| identificación | DNI | Socio (EE) | 1 Dar de alta Socios |
| contacto | TE, mail | Socio (EE) | 1 Dar de alta Socios |
| nuevo socio | carnet | 1 Dar de alta Socios | Socio (EE) |
| estado socio | IdSocio, EstadoSocio | Socios | 1 Dar de alta Socios |
| ISBN | ISBN | Bibliotecario (EE) | 2 Dar de alta Catálogo |
| inventario | NºInventario | 2 Dar de alta Catálogo | Bibliotecario (EE) |
| pedido | Título, Autor, DNI | Socio (EE) | 3 Registrar préstamo |
| préstamo | comprobante | 3 Registrar préstamo | Socio (EE) |
| devolución | DNI, NºComprobante | Socio (EE) | 4 Registrar devolución |
| sanción | DNI, FechaSanción | 4 Registrar devolución | Socio (EE) / Sanciones |

---

## Ejemplo: Objetivo bien formulado

**EXPRESIÓN DE DESEOS**: "Incrementar las ventas en un 20%"
**APROXIMACIÓN AL MEDIO**: "mediante una campaña publicitaria"
→ Objetivo completo: "Incrementar las ventas en un 20% mediante una campaña publicitaria"

---

## Ejemplo: Árbol Causa-Efecto (estructura)

```
                    PROBLEMA CENTRAL
                    ┌──────────────┐
          ┌─────────┤   Problema   ├─────────┐
          │         └──────────────┘         │
    ┌─────┴─────┐                      ┌─────┴─────┐
    │  Causa A  │                      │  Causa B  │
    └─────┬─────┘                      └─────┬─────┘
    ┌─────┴─────┐                  ┌─────────┴────────┐
    │ Causa A.1 │             ┌────┴───┐         ┌────┴───┐
    └───────────┘             │ B.1 🟢 │         │ B.2 🔴 │
                              └────────┘         └────────┘

🟢 Responsabilidad directa  🟡 Indirecta  🔴 No influenciable
```
