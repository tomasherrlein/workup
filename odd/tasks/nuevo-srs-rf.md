# Nuevo SRS y Requerimientos Funcionales en docs/

**Objective.** Create new versions of the SRS and the Functional Requirements under `docs/`, incorporating every decision approved in `docs/propuestas-resolucion-requerimientos.md` (D01–D08).

**Why.** All business decisions D01–D08 are approved; the proposals document states they do not change the current RF/SRS until incorporated.

**Scope.**
- New files: `docs/srs.md` (v3.0) and `docs/requerimientos-funcionales.md`.
- Root `srs.md` and `Requerimientos Funcionales WorkUp.md` stay untouched as history.
- Out of scope: technical implementation decisions (link mechanics, email provider, concurrency), which the proposals doc marks as non-normative or pending technical design.

**Constraints.** Artifacts in neutral professional Spanish (existing project language). Preserve structure, numbering style and traceability of the source documents. Do not invent rules not approved in the proposals doc.

**TDD.** Not applicable (documentation only). Checks: structural readback, every approved decision D01–D08 traceable in the new docs.

**Route.** Delegated direct: one writer (writer trigger: 2 non-trivial files; preparation trigger: reads 3 large sources).

## Tasks

- [x] T1 — Write `docs/requerimientos-funcionales.md` with approved decisions incorporated.
- [x] T2 — Write `docs/srs.md` v3.0 consistent with T1.
- [x] T3 — Readback and traceability check; commit on `docs/nuevo-srs-rf`.

## Progress

- Branch `docs/nuevo-srs-rf` created from `main` (6781950).
- T1/T2 written by delegated writer: `docs/requerimientos-funcionales.md` (810 lines, RF 1–30), `docs/srs.md` (1103 lines, v3.0). Root files untouched. Not checked off until T3 readback.
- Decision (user, 2026-09-21): remove "capacidad simultánea"; reservations do not block by stations. D-11 superseded by D-25; Reglas 13-14 and Restricción 8 retired.
- T3 readback: RF 1-30 headings present; SRS v3.0 header cites proposals doc; remaining "tramo"/"capacidad simultánea" matches are only superseded notes; root files untouched.
- Engram mirror: pending (memory save failed earlier in session with ambiguous active sessions).
