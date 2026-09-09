# WorkUp

Sistema web de gestión para emprendimientos que prestan servicios por turno: agenda, clientes, caja e insumos
en una sola herramienta.

**Equipo:** Bapton Solutions — Ana Belén Tovar Valenzuela · Tomás Ezequiel Herrlein · Patricio Leonardo Manna ·
Victor Nahuel Velasquez · Erika Angelina Romeo

**Asignatura:** Metodología de Sistemas I — UTN FRGP, Técnico Universitario en Programación

---

## Documentos

| Archivo | Qué es |
|---|---|
| [`Relevamiento WorkUp.pdf`](./Relevamiento%20WorkUp.pdf) | **Entrega.** Relevamiento narrativo, formato de cátedra |
| [`relevamiento-narrativo.md`](./relevamiento-narrativo.md) | Fuente del PDF anterior |
| [`relevamiento.md`](./relevamiento.md) | Informe de Relevamiento extendido: reconocimiento, QQCCD, ambigüedades, variabilidad y volumetría |
| [`srs.md`](./srs.md) | Especificación de Requerimientos IEEE 830: 16 requerimientos funcionales, RNF y restricciones |
| [`Propuestas Bapton Solutions.docx`](./Propuestas%20Bapton%20Solutions.docx) | Propuestas de sistemas presentadas por el equipo |
| `relevamiento-v1-individual.md` · `relevamiento-v2-ampliado.md` | Versiones previas, se conservan como historial |

## Casos relevados

- **Peluquería** — atención individual, dos trabajadoras, servicios con tiempo de procesado.
- **Estudio de yoga** — clases grupales de cupo limitado, grilla semanal recurrente, dos salas.

El análisis concluye que **el turno individual es el caso particular de un servicio con cupo 1**, por lo que un
único modelo cubre ambas formas de atención.

## Estado

| Etapa | Producto | Estado |
|---|---|---|
| Relevamiento | `relevamiento.md` · `Relevamiento WorkUp.pdf` | Cerrado |
| Especificación de requerimientos | `srs.md` | Cerrado |
| Análisis estructurado | Lista de Eventos, DFD, DD, DER | Pendiente |
