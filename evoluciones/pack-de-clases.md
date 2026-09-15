# Evolución futura — Pack de clases

> **Fuera del alcance actual.** Este documento **no forma parte del relevamiento ni de la especificación de
> requerimientos**. Registra una modalidad de pago que se analizó y se dejó para una versión posterior. El
> sistema actual trabaja con abono mensual con clases fijas y sin recuperación (`srs.md`, D-13 y D-15).

**Fecha:** 14-09-2026
**Equipo:** Bapton Solutions

---

## 1. Qué es

En lugar de pagar un abono mensual por horarios fijos, el alumno compra un **pack con una cantidad de clases**
(por ejemplo cuatro, ocho o doce), cada uno con su precio, y las usa en las clases de la grilla que tengan lugar.
Da más flexibilidad al alumno que no puede comprometerse a horarios fijos.

## 2. Por qué se dejó para después

- **Suma lógica que el abono fijo no necesita:** un saldo de clases que se descuenta y se devuelve, una
  anticipación mínima para avisar la falta y ajustes manuales de la dueña. Serían seis o siete reglas nuevas.
- **No es un valor del modelo actual, es una regla nueva.** El cupo máximo varía entre rubros sin cambiar las
  reglas; el pack agrega un contador con su propio ciclo de vida.
- **La flexibilidad ya tiene salida:** quien no puede comprometerse a horarios fijos toma clases sueltas.
- **Se puede sumar sin rediseñar.** El abono fijo es un alumno inscripto en clases con un vencimiento; el pack es
  esa misma inscripción más un contador de clases.

## 3. Lógica propuesta

| Aspecto | Comportamiento |
|---|---|
| Qué compra el alumno | Un pack con una cantidad de clases, o un pase libre sin tope |
| Vencimiento | A las cuatro semanas de la fecha de pago |
| Límite semanal | No tiene: el alumno usa sus clases cuando quiere mientras le queden |
| Anotarse a una clase | Descuenta una clase del pack; el pase libre no descuenta |
| Clases fijas | Opcionales; descuentan una clase cada semana al quedar anotado |
| Avisa la falta con la anticipación mínima | La clase vuelve al pack y el lugar se libera |
| Avisa tarde o no se presenta | Pierde la clase y queda ausente |
| Se suspende la clase | La clase vuelve al pack de cada alumno anotado; no se devuelve dinero |
| Clases sin usar al vencimiento | Se pierden; no pasan al pack siguiente |
| Pack vencido antes de usar una clase devuelta | La clase se pierde |
| Casos excepcionales | La dueña agrega o quita clases de cualquier pack a mano, anotando el motivo |
| Anticipación mínima | La fija y la cambia la dueña |

## 4. Vocabulario

"Clase" pasaría a tener un sentido más: además del tipo de clase, la clase de la grilla y la clase de la semana,
también cada **clase del pack** que el alumno pagó y todavía puede usar. El glosario tendría que distinguirla
explícitamente.

## 5. Impacto estimado sobre la especificación actual

| Elemento | Cambio |
|---|---|
| Glosario | Agregar pack de clases, clase del pack, pase libre, anticipación mínima y la modalidad `Con pack` |
| RF-01 | Agregar el parámetro de anticipación mínima |
| RF-05 | Pasa a registrar packs: cantidad de clases, clases restantes y ajuste manual con motivo |
| RF-06 | Las clases fijas descuentan del pack; sin clases disponibles no se anota |
| RF-07 | Las clases de cupo máximo mayor a 1 admiten anotación `Con pack` además de `Suelta` |
| RF-08 | Devuelve la clase si el aviso llega con la anticipación mínima |
| RF-09 | Devuelve la clase al pack de cada alumno anotado |
| RF-11 | Aclara que la ausencia no devuelve la clase |
| Reglas de negocio | Reemplazar las reglas 16 a 19 por las del pack: saldo, descuento, devolución, vencimiento y ajuste manual |
| Decisiones | Reincorporar la anticipación mínima y el control manual de la dueña, hoy retirados como D-14 |
