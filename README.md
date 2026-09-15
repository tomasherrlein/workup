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
| [`Relevamiento WorkUp.md`](./Relevamiento%20WorkUp.md) | **Relevamiento narrativo**, formato de cátedra. Fuente de todos los demás documentos |
| [`relevamiento.md`](./relevamiento.md) | Análisis del relevamiento: hechos identificados, QQCCD, ambigüedades y variabilidad entre rubros |
| [`srs.md`](./srs.md) | Especificación de Requerimientos IEEE 830: 19 requerimientos funcionales, RNF y restricciones |
| [`Propuestas Bapton Solutions.docx`](./Propuestas%20Bapton%20Solutions.docx) | Propuestas de sistemas presentadas por el equipo |
| [`evoluciones/`](./evoluciones/) | Funcionalidades analizadas y dejadas para versiones futuras. **No forman parte del alcance** |
| [`historial/`](./historial/) | Versiones anteriores del relevamiento y su PDF |

## Skill de Claude Code

[`.claude/skills/analista-sistemas/`](./.claude/skills/analista-sistemas/SKILL.md) contiene la skill usada para
producir estos documentos. Aplica la metodología de la cátedra: reconocimiento, relevamiento con QQCCD, SRS IEEE
830 y análisis estructurado (Lista de Eventos, DFD, Diccionario de Datos y DER).

Si abrís Claude Code dentro de esta carpeta, la skill se carga sola. Para usarla en cualquier proyecto, copiá la
carpeta a `~/.claude/skills/`.

## Casos relevados

- **Peluquería** — atención individual, dos trabajadoras, servicios con tiempo de procesado.
- **Estudio de yoga** — clases grupales de cupo limitado, grilla semanal recurrente, dos salas, abono
  mensual con clases fijas y sin recuperación de clases.

El análisis concluye que **el turno individual es el caso particular de un servicio con cupo máximo 1**, por lo
que un único modelo cubre ambas formas de atención.

## Estado

| Etapa | Producto | Estado |
|---|---|---|
| Relevamiento | `Relevamiento WorkUp.md` · `relevamiento.md` | Cerrado |
| Especificación de requerimientos | `srs.md` | Cerrado |
| Análisis estructurado | Lista de Eventos, DFD, DD, DER | Pendiente |
