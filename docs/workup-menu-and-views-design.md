# WorkUp UX/UI Design Specification

> **Status:** Design source for future screen generation; no screens have been generated.
> **Artifact language:** Technical English. UI-copy examples are Argentine Spanish.
> **Primary source of truth:** `srs.md` v2.2 (14-09-2026).

## 1. Decisions first

### 1.1 Scope and source precedence

WorkUp is a mobile-first responsive web application for one business owner/admin to operate an appointment-based business. It supports two patterns through one operational model:

- **Individual services:** capacity `1`; a service has Application, Wait, and Finishing segments. Only Application and Finishing occupy the worker.
- **Recurring group classes:** capacity `>1`; the owner publishes a recurring weekly grid, explicitly generates dated weekly classes, and manages fixed monthly memberships and drop-in class reservations.

| Priority | Source | Use |
|---|---|---|
| 1 | `srs.md` | Functional rules, terminology, inclusions, exclusions, non-functional requirements. Wins every conflict. |
| 2 | `Relevamiento WorkUp.md` | Operational context and examples. |
| 3 | `relevamiento.md` | Analysis and ambiguity context. |
| 4 | `README.md` | Repository/document context. |
| 5 | `Propuestas Bapton Solutions.docx` | Only non-conflicting visual/accessibility input: mobile browser, keyboard use, high contrast, larger text, screen readers, simple language, own public booking link. |

**Excluded from this design:** all material under `historial/**`; all future scope under `evoluciones/**`; customer accounts or log-in; customer-managed history; worker accounts; online payments; fiscal invoices; packs/passes; rooms/seats; automatic inventory consumption; class make-ups or refunds; CRM/messaging; and delivery analytics.

### 1.2 UX assumptions that do not change the SRS

| Ambiguity | Conservative design decision |
|---|---|
| Public reservation access | A confirmation page creates an opaque, unguessable reservation link. With that token, the customer reaches only that reservation’s limited detail. Without a token, `Gestionar mi reserva` requires phone plus a one-time verification code and returns only verified reservations for that phone. This is **not** a customer account; the code transport, expiry, and rate limits require a technical security contract. |
| Owner roles | There is exactly one authenticated owner/admin account. “Worker” is display data only; no worker navigation, permissions, or sign-in exists. |
| Reservation versus manual capacity | Every reservation—public or owner-assisted for a future dated class—blocks when worker availability, class capacity, or simultaneous capacity is exhausted. A manual record of an attendance that has already occurred never blocks: it shows a persistent conflict warning and requires an explicit confirmation before saving. For a spontaneous at-arrival `Suelta` class entry, this non-blocking rule is a conservative product decision pending stakeholder validation because RF-11/Rule 11 do not unambiguously define that case. |
| Walk-in timestamp | A walk-in’s date and time are assigned automatically when the owner saves it and are not editable. Historical correction of that timestamp is out of scope until validated; this deliberately does not reintroduce D-12. |
| Week generation | The owner explicitly starts generation, sees a preview, then confirms. Repeating the action for the same week is idempotent: existing dated classes are shown as existing, never duplicated. |
| Annotation state | Attendance/booking status and payment marker are separate for individual appointments and `Suelta` class attendees: `Atendido` and `Cobrado` remain independently visible and editable in either order where the SRS permits. A `Fija` attendee never receives a per-class payment marker or payment action: membership payment is recorded only at creation or renewal. |
| Referenced setup data | Services, workers, grid entries, expense types, and inventory items with history are archived/deactivated rather than destructively deleted. The UI explains that history is retained. |
| Notice delivery failure | Failed automated reminders, daily summaries, or stock-review reminders are visible in owner feedback with retry. No delivery/open/read analytics are invented. |
| Reports | The period result is exactly individual-attention charges + drop-in-class charges + membership charges − expenses. Every other report label has an information tooltip defining its included data; where its formula is not specified, it says `Definición pendiente de validación de negocio`. |

### 1.3 UX assumptions and non-goals

- The owner operates private screens. A customer only sees public availability, their own reservation data, and the public business identity.
- The reservation flow displays availability, not other customers’ names, phone numbers, annotations, attendance, or health observations.
- “WhatsApp” actions prepare/copy/open a prefilled message; the suspension notice is never automatically sent.
- “Cobrado” records a received amount but produces no invoice, receipt, payment gateway, or fiscal artifact.
- Availability conflicts are explained in plain language. Internal capacity calculations are not exposed to customers.

---

## 2. Product principles and visual direction

### 2.1 Product principles

1. **The day must be readable at a glance.** The owner should understand today before tapping into details.
2. **Operations before administration.** Jornada, Agenda, and class lists are closer than configuration.
3. **One clear next action.** A primary button states the immediate action; secondary choices are quieter.
4. **Never punish a real-world exception.** Booking rules protect availability; owner records preserve what actually happened.
5. **Explain before asking.** Short, plain Spanish labels, examples, and inline consequences reduce fear of making a mistake.
6. **Reveal complexity only when relevant.** Segments are shown only for individual services; memberships/grid controls only for group classes.
7. **Trust through visible state.** Status, payment marker, timestamps, warnings, and retry paths are explicit.

### 2.2 Visual direction: “calm counter notebook”

The interface should feel like a well-organized counter notebook: warm paper-like surfaces, dark ink, and restrained colored marks that support daily work. It must not resemble a dense generic SaaS dashboard: avoid cold white-on-blue layouts, oversized metric walls, excessive cards, decorative gradients, tiny table controls, and productivity jargon.

- **Mood:** warm, practical, calm, familiar, and assured.
- **Shape language:** soft but not playful; paper cards and clear list rows.
- **Data emphasis:** time, person, service/class, state, and money before ornamental charts.
- **Illustration:** sparse, simple line illustrations only in empty states; never decorative visual clutter in operational views.

### 2.3 Design tokens

| Token | Value | Intended use |
|---|---:|---|
| `--ink` | `#20251F` | Primary text, navigation icons |
| `--ink-muted` | `#5B6258` | Secondary text |
| `--canvas` | `#F7F4ED` | App background, warm paper |
| `--surface` | `#FFFFFF` | Cards, sheets, inputs |
| `--surface-soft` | `#EEE9DE` | Grouping, selected range background |
| `--line` | `#D6D0C4` | Borders/dividers |
| `--sage-700` | `#2E5B4A` | Brand primary, focus-compatible dark action |
| `--sage-600` | `#3F725D` | Hover/active primary |
| `--sage-100` | `#DCEBE1` | Positive/selected tint |
| `--terracotta-700` | `#9A432C` | Destructive action and error text |
| `--terracotta-100` | `#F8DED6` | Error/warning destructive tint |
| `--sun-700` | `#8A5A00` | Attention/warning text |
| `--sun-100` | `#F8EDC9` | Attention tint |
| `--blue-700` | `#245A74` | Informational text/link accent |
| `--blue-100` | `#DCECF3` | Informational tint |
| `--focus` | `#004E9C` | 3px visible focus ring |

Use color with a text label and icon; never communicate a state by color alone. Verify all production foreground/background combinations against WCAG 2.1 AA (normal text 4.5:1; large text 3:1).

| System status | Label / token | Example |
|---|---|---|
| Pending | `Pendiente` / sun | `Pendiente de confirmar` |
| Confirmed | `Confirmado` / sage | `Confirmado` |
| Attended | `Atendido` / sage | `Atendido` |
| Paid marker | `Cobrado` / ink + check | `Cobrado $18.000`; only individual appointments and `Suelta` attendees, never `Fija` attendees |
| Cancelled | `Cancelado` / muted | `Cancelado; lugar liberado` |
| Absent | `Ausente` / terracotta | `Ausente` |
| Scheduled | `Programado` / blue | `Clase programada` |
| Suspended | `Suspendido` / terracotta | `Clase suspendida` |
| Membership | `Vigente` / sage; `Vencido` / terracotta | `Vence el 12 jun.` |

### 2.4 Type, layout, elevation, and icons

| Element | Specification |
|---|---|
| Typeface | Humanist sans serif with strong numeral distinction (e.g., **Atkinson Hyperlegible** preferred; system fallback `Arial, sans-serif`). Never use condensed display faces. |
| Type scale | Base 18px / 1.5; labels 16px minimum; body 18px; section title 22px; page title 28px; public booking title 32px. Support browser zoom to 200%. |
| Numerals | Tabular numerals for times, money, occupancy, and duration. |
| Spacing | 4px base: 4, 8, 12, 16, 24, 32, 48. Default mobile gutter 16px; desktop content gutter 32px. |
| Touch targets | Minimum 48×48px; 56px primary buttons on public booking. Preserve 12px separation between destructive/primary controls. |
| Radius | 8px inputs/chips; 12px cards/sheets; 999px only for compact status pills. |
| Shadows | One quiet elevation: `0 2px 8px rgba(32,37,31,.12)` for floating sheets; borders do most grouping. |
| Icons | Familiar 24px rounded-outline SVG icons, 2px stroke. Always pair menu icons with labels. Use filled/solid only for selected state or status. |

### 2.5 Accessibility and responsive behavior

- All owner and public workflows are keyboard operable in logical visual order. `Esc` closes sheets; focus returns to the invoking control.
- Inputs have persistent visible labels; placeholders are examples, never labels. Errors are programmatically associated with fields and summarized after submit.
- Live regions announce reservation availability refreshes, successful saving, and blocking form errors without excessive chatter.
- High contrast mode is a Settings preference that replaces low-contrast tints with stronger token pairings; it does not rely on browser-only settings.
- Respect reduced motion; use a 150–200ms opacity/position transition only when motion is enabled.
- No horizontal scroll below 360px. Tables turn into labeled cards at small widths.

| Breakpoint | Shell behavior |
|---|---|
| `360–599px` (phone) | Bottom navigation, single-column lists, bottom sheets, sticky primary action. |
| `600–1023px` (tablet) | Bottom navigation remains; two-column content where reading order stays clear. |
| `≥1024px` (desktop) | Permanent left rail; content max-width 1280px; contextual right rail only for selected detail/summary. |

---

## 3. Information architecture

### 3.1 Owner panel

```text
Owner panel
├── Jornada (today and operational follow-through)
├── Agenda (individual appointments and dated classes)
├── Clases (weekly grid, weekly generation, dated class lists)
├── Abonos (monthly memberships)
├── Gastos
├── Insumos
├── Reportes
└── Configuración
    ├── Negocio y acceso
    ├── Servicios y precios
    ├── Trabajadoras / profesoras
    ├── Horarios y pausas
    ├── Grilla semanal (group pattern only)
    ├── Tipos de gasto
    ├── Avisos
    ├── Reserva pública
    └── Accesibilidad
```

`Jornada` is always the owner home. The menu adapts only by hiding non-applicable **group-only** sections (`Clases`, `Abonos`, and `Configuración › Grilla semanal` are rendered only when at least one service has capacity greater than 1); it never creates a separate product mode.

`Clases` (`O-07`) is the single editor of the weekly grid. `Configuración › Grilla semanal` (`O-20`) is a shortcut row that opens `O-07`; it does not duplicate the editor.

### 3.2 Public experience

```text
Public booking link
├── Business landing / choose service or class
├── Individual: choose worker → date/time → contact details → review
├── Group: choose dated class → contact details → review
├── Confirmation (opaque management link)
└── Reservation management (opaque token, or phone + one-time code)
    ├── confirm attendance
    ├── cancel (release place)
    └── reschedule individual appointment only
```

---

## 4. Navigation and menu design

### 4.1 Mobile owner shell

```text
┌──────────────────────────────────────┐
│ ☰  WorkUp                   [perfil] │
│ Jornada · martes 14 de mayo           │
├──────────────────────────────────────┤
│ Page content                          │
│                                        │
│                         [+ action]    │
├──────────────────────────────────────┤
│ ◉ Jornada  ▣ Agenda  ◫ Clases  ⋯ Más │
└──────────────────────────────────────┘
```

| Mobile location | Items (Spanish label / icon) | Behavior and rationale |
|---|---|---|
| Header | `Menú` (hamburger), `WorkUp` wordmark/page title, and `Perfil` icon | Hamburger opens the same complete navigation sheet as `Más`; wordmark returns to Jornada; profile opens the owner account sheet. Header actions are page-specific, not global clutter. |
| Bottom navigation | `Jornada` / sun; `Agenda` / calendar; `Clases` / grid (only if a group service exists); `Más` / menu | 3–4 high-frequency destinations reach with thumb. Active item uses ink text, solid icon, and 3px sage underline; not color alone. |
| More / hamburger sheet | `Jornada`, `Agenda`, `Clases` when applicable, `Abonos`, `Gastos`, `Insumos`, `Reportes`, `Configuración` | It is a labeled bottom sheet with focus trap; selecting an item closes it and moves focus to the route title. A low-stock count may badge `Insumos`; an expiring-membership count may badge `Abonos`. |
| Profile sheet | Owner username, `Configuración de cuenta`, `Cerrar sesión` | `Cerrar sesión` opens a confirmation dialog (`¿Querés cerrar sesión?`). Confirming ends the session and returns to `O-01`; Cancel returns focus to Profile. |
| Contextual action | Floating/inline primary depending on page | `+ Registrar atención` on Jornada; `+ Nuevo gasto` on Gastos; no persistent generic plus that creates ambiguity. |

### 4.2 Desktop owner shell

```text
┌───────────────┬──────────────────────────────────────────────┐
│ WorkUp         │ Jornada · martes 14 de mayo       [perfil]  │
│               ├──────────────────────────────────────────────┤
│ ● Jornada      │ Page heading / contextual actions            │
│ □ Agenda       │                                              │
│ ▦ Clases       │ Main content (max 1280px)                    │
│ ◌ Abonos       │                                              │
│ ─────────────  │                                              │
│ $ Gastos       │                                              │
│ □ Insumos  3   │                                              │
│ ◔ Reportes     │                                              │
│ ⚙ Configuración│                                              │
│               │                                              │
│ ↗ Ver reserva  │                                              │
└───────────────┴──────────────────────────────────────────────┘
```

| Desktop rail item | Icon | Badge / behavior |
|---|---|---|
| `Jornada` | sun/day | Default active home. |
| `Agenda` | calendar | Direct view of dated operation. |
| `Clases` | weekly grid | Render only when group services exist; includes generation and dated lists. |
| `Abonos` | repeat/card | Render only when group services exist. Badge for memberships expiring today/this week, not an alert count without time context. |
| `Gastos` | receipt | No badge. |
| `Insumos` | box | Badge is number below minimum. |
| `Reportes` | chart | No badge. |
| `Configuración` | gear | Collapses labels only at 1024–1119px; tooltips appear on focus/hover. |
| `Ver reserva pública` | external-link | Opens public booking in a new tab/window; clearly distinct from customer management. |

The rail remains visible at desktop widths. Its collapsed state preserves accessible labels via `aria-label` and visible tooltip on keyboard focus. The active route has a filled pale-sage background, dark text, icon change, and an `aria-current="page"` marker. The desktop `Perfil` control opens the same owner account popover as mobile; it contains `Configuración de cuenta` and `Cerrar sesión`. Logout always requires the same confirmation, then returns to `O-01`.

---

## 5. Screen inventory and shared feedback

Stable IDs are inputs for implementation, QA, and Stitch generation. `O` means owner-only, `P` public, and `S` shared system pattern.

| ID | Screen / variant | Audience | RF |
|---|---|---|---|
| `O-01` | Owner login | Owner | RF-01 |
| `O-02` | First-run onboarding | Owner | RF-01–04 |
| `O-03` | Jornada | Owner | RF-11, 12, 15 |
| `O-04` | Agenda: day/week list and filters | Owner | RF-07–12, 15 |
| `O-05` | Individual appointment detail / edit | Owner | RF-08, 09, 11, 12 |
| `O-06` | Manual walk-in / attention sheet | Owner | RF-11 |
| `O-07` | Weekly classes grid | Owner | RF-04, 06 |
| `O-08` | Generate-week preview / confirmation | Owner | RF-06 |
| `O-09` | Dated class detail and attendance list | Owner | RF-08, 10–12 |
| `O-10` | Suspend-class notice sheet | Owner | RF-10 |
| `O-11` | Membership list | Owner | RF-05 |
| `O-12` | Membership create / renew form | Owner | RF-05 |
| `O-13` | Expenses list / filter | Owner | RF-13 |
| `O-14` | Expense form | Owner | RF-13 |
| `O-15` | Inventory list and declared-stock form | Owner | RF-14 |
| `O-16` | Reports overview and metric detail | Owner | RF-16 |
| `O-17` | Settings: business, account, operation | Owner | RF-01 |
| `O-18` | Settings: services / prices / segments | Owner | RF-02 |
| `O-19` | Settings: workers, schedules, pauses | Owner | RF-03 |
| `O-20` | Settings: weekly grid | Owner | RF-04 |
| `O-21` | Settings: expense categories | Owner | RF-01, 13 |
| `O-22` | Settings: notification schedule / failures | Owner | RF-01, 17–19 |
| `O-22a` | Reminder content and notification failure detail | Owner | RF-17–19 |
| `O-23` | Settings: public booking / accessibility | Owner | RF-01, 07 |
| `P-01` | Public booking landing | Customer | RF-07 |
| `P-02` | Public individual availability | Customer | RF-07 |
| `P-03` | Public group class availability | Customer | RF-07 |
| `P-04` | Public contact and review | Customer | RF-07 |
| `P-05` | Reservation confirmation | Customer | RF-07 |
| `P-06` | Reservation management / phone verification | Customer | RF-08, 09 |
| `S-01` | Confirmation / destructive dialog | Both | RF-08–14 |
| `S-02` | Loading, empty, error, success, and conflict patterns | Both | All applicable |

### 5.1 Shared state and feedback patterns (`S-01`, `S-02`)

| State | Visual and behavior | Copy example |
|---|---|---|
| Loading | Skeleton mirrors final list/form; do not replace layout with spinner alone. Announce “Cargando…” once. | `Estamos buscando horarios disponibles…` |
| Empty | One sentence, small line illustration, one appropriate action. | `Todavía no hay gastos en este período.` / `Registrar gasto` |
| Error | Persistent inline callout with cause where known and retry. Never hide submitted input. | `No pudimos guardar el gasto. Intentá nuevamente.` |
| Reservation conflict | Keep choice data, explain the place/time is no longer available, return to availability. | `Ese horario se ocupó recién. Elegí otra opción.` |
| Manual conflict | Persistent warning names the worker/capacity conflict and requires a deliberate confirmation for an attendance already occurring; every reservation, including owner-assisted, blocks instead and offers alternatives. | `Se superpone con 1 atención de Sofía. Confirmá para registrarla igual.` |
| Success | Brief toast plus persistent state change in page. Do not make toast the only evidence. | `Atención registrada.` |
| Destructive confirmation | Explicit object, irreversible consequence, cancel and destructive action. Default focus stays on Cancel. | `¿Archivar “Coloración”? Ya no se podrá reservar, pero su historial se conserva.` |

Privacy: phone numbers are masked (`•••• 4821`) in dense list rows unless the owner opens a detail; public screens never reveal them. Health and customer notes (the customer record updated by RF-05 and RF-11) appear only behind the intentional disclosure control inside `O-05`, `O-09`, and `O-12`; there is no standalone customer-management screen because the SRS defines no such requirement. They are not shown in public lists, reports, or notification previews unless the owner specifically needs contact data.

---

## 6. Detailed view specifications

### `O-01` Owner login

- **Purpose / entry:** private entry for the single business owner; entry after session expiry or direct private URL.
- **Layout:** centered narrow card on warm canvas; wordmark, “Ingresá a WorkUp”, username field, password field, show-password control, primary `Ingresar`.
- **Secondary actions:** `¿Olvidaste tu contraseña?` is a support/recovery route only if implemented; no sign-up, worker access, or customer login link.
- **Validation/states:** required labels; non-enumerating error `Usuario o contraseña incorrectos`; rate-limit message without disclosing account existence; loading button text `Ingresando…`.
- **Accessibility/responsive:** autofill-compatible semantics, password-manager support, 18px text and full-width button on phone.

### `O-02` First-run onboarding

- **Purpose / entry:** create the business and required minimum configuration after account provision. It is resumable and uses a visible 1–4 stepper: `Negocio`, `Servicios`, `Equipo`, `Revisar`.
- **Layout hierarchy:** title, one-sentence explanation, step content, Back / Continue; mobile has sticky bottom primary action. Save draft after each valid step.
- **Fields:**
  1. business name, business contact phone, simultaneous capacity (required), walk-in toggle (default on), expense categories, owner username/password, reminder lead time (default 24h), daily-summary time (default 21:00), inventory reminder frequency (default weekly);
  2. at least one service: name, price, capacity, Application/Wait/Finishing durations;
  3. worker data and weekly working schedule/pauses; show “Add weekly grid later” only if group services exist.
- **Validation:** simultaneous capacity is validated against Restriction 9 (maximum service capacity × number of workers) only once services and workers exist: step 1 accepts the value, and step 4 `Revisar` blocks `Finalizar configuración` with an inline error linking back to step 1 when the value is below that bound; segment durations are non-negative and total duration must be greater than zero; end time follows start; pause lies inside working hours. Explain errors next to fields.
- **Completion:** review uses editable summary cards; `Finalizar configuración` leads to Jornada. Grid configuration remains an explicit next step, not an invented mandatory class.

### `O-03` Jornada

```text
┌ Jornada · Hoy, martes 14 ────────────┐
│ Cobrado hoy  $42.500   [Registrar]   │
│ Pendientes 3 · Falta cobrar 2        │
├ Próximo ─────────────────────────────┤
│ 10:00  Ana Pérez · Corte · Marina    │
│ Pendiente              [Ver detalle] │
├ Más tarde ───────────────────────────┤
│ 11:00  Hatha · 6/10 personas         │
└──────────────────────────────────────┘
```

- **Purpose / entry:** owner home and current-day operational overview. Date defaults to today; previous/next date controls allow review without turning it into the full Agenda.
- **Layout/components:** top date and `Hoy`; summary strip (`Cobrado hoy`, pending confirmations, `Falta cobrar`); chronological sections `Ahora`, `Próximo`, `Más tarde`, `Finalizado`; appointment/class rows with time, client/class, worker, status and independent paid marker only for individual appointments and `Suelta` attendees.
- **Actions:** primary `Registrar atención` opens `O-06` only when walk-ins are enabled; row tap opens `O-05` or `O-09`; a class row also has an inline `Tomar asistencia` expander listing its attendees with the same per-attendee quick actions as `O-09`, so RF-11 is invocable for classes without leaving Jornada (RF-15 note); quick owner actions: `Confirmar`, `Marcar atendido`, `Registrar cobro` only where a per-attendance charge applies, and `Marcar ausente`, subject to state/context.
- **States:** no activity explains `No hay turnos ni clases para esta jornada`; suspended classes remain visible but muted and labeled; payment total updates persistently after successful recording.
- **Responsive/accessibility:** phone rows are large stacked cards; desktop uses grouped timeline/list, not a compressed spreadsheet. Status text is read before color in screen-reader labels.
- **Cross-links:** Agenda for full day/week; individual detail, class detail, walk-in sheet.

### `O-04` Agenda: dated operation

- **Purpose / entry:** find and manage individual appointments and dated group classes outside the single-day focus.
- **Layout:** `Agenda` title, view toggle `Día` / `Semana`, date navigator, filter button, appointment/class timeline. Desktop may show a week calendar beside the selected-day detail; mobile defaults to day list.
- **Filters:** date/range, worker, service/type, state, origin (`Reservado`, `Sin reserva`), and “requires action” (pending, unpaid). Selected filters are removable chips.
- **Rows:** individual rows show active vs wait segment as a subtle segmented time bar; class rows show occupancy (`6 de 10`) and date. Never expose another customer’s data through a public route.
- **Actions:** `Nueva reserva` is owner-assisted availability flow; use `Registrar atención` for a manual walk-in; row opens detail. Do not offer group reprogramming.
- **States/validation:** filter no-result state preserves filters and offers `Limpiar filtros`; loading and recoverable errors use `S-02`.

### `O-05` Individual appointment detail and edit

- **Purpose / entry:** act on one individual capacity-1 appointment from Jornada, Agenda, or reservation lookup.
- **Layout:** header with service, date/time, worker, origin; client card (name, masked phone, owner-only notes); segment timeline; state and payment marker; activity summary.
- **Actions:** `Confirmar asistencia`, `Registrar atención`, `Registrar cobro`, `Marcar ausente`, `Reprogramar`, `Cancelar`. Show only valid actions but preserve status history/readability.
- **Reschedule:** opens a bottom sheet with same worker locked, date selector, valid time slots, and current slot indicator; successful move sets customer state to `Confirmado`. If no option works, a separate cancel path requires confirmation.
- **Customer-note form:** `Agregar observación` opens a labeled owner-only form with `Observación para la próxima atención` (optional multiline text), privacy help `Puede incluir información de salud relevante. Solo la ve la dueña en el contexto de atención; nunca se muestra en la reserva pública, reportes ni avisos.` and actions `Guardar observación` / `Cancelar`. The form displays a privacy icon, retains entered text on validation/network errors, and presents existing sensitive notes only after an intentional disclosure control.
- **Charge form:** `Registrar cobro` opens a sheet with non-editable appointment/customer/service summary, required `Monto cobrado`, currency, and `Fecha y hora de cobro` defaulting to save time; optional owner-only `Nota interna`. Amount must be greater than zero and uses the business currency. `Confirmar cobro` shows `Se registrará el cobro de $… para {cliente}. No se emite comprobante.`; confirmation sets the independent `Cobrado` marker and updates Jornada total. No invoice or receipt action exists.
- **Validation:** reprogram uses reservation-grade availability. Owner manual edit conflict does not block; it invokes a persistent warning and explicit confirmation. Cancel copy: `Al cancelar, el horario vuelve a quedar disponible.`
- **Cross-links:** client contextual notes; Agenda; prepared/public management link may be copied, but no customer account is displayed.

### `O-06` Manual walk-in / attention sheet

- **Purpose / entry:** record a real unscheduled individual attention from Jornada, if enabled in business settings.
- **Layout:** phone-first search/create customer, service, worker, attendance, amount/payment marker, optional notes. A clear `Sin reserva` origin label is non-editable. Date and time are automatically assigned at save (`Registrado ahora: {fecha} {hora}`), are not form fields, and cannot be edited.
- **Conflict behavior:** calculate worker/capacity conflict after enough fields exist; show a persistent amber warning with conflicting appointment names only to owner and require `Confirmar y registrar de todos modos`. The save is never blocked by this warning. No false “availability” language.
- **Historical time correction:** correcting a walk-in’s automatically recorded date/time is out of scope pending stakeholder validation; this screen offers no backdating or historical-edit control.
- **No-room outcome:** if the owner decides not to record because there is no capacity, offer `Volver más tarde` (no record) or `Ir a reservas`; do not create a waitlist.
- **Restrictions:** not shown where walk-ins are disabled; for group capacity, use the dated class detail to add a `Suelta` attendee instead.

### `O-07` Weekly classes grid

- **Purpose / entry:** view and edit the recurring, published weekly class grid; only relevant when services with capacity greater than one exist.
- **Layout:** week columns Monday–Sunday, time rows, grid tiles containing type, time, worker, duration, capacity. On phone, use an accessible day selector above a chronological tile list rather than a squeezed seven-column grid.
- **Actions:** `Agregar clase a la grilla`, edit tile, archive/deactivate tile. A contextual `Generar semana` opens `O-08` and names the target week.
- **Validation:** worker must have a schedule covering the class; collisions are warned/validated according to configured availability rules; a grid edit affects future generation, never alters already generated dated classes.
- **Empty:** `Todavía no hay clases en la grilla. Agregá la primera para poder generar una semana.`

### `O-08` Generate-week preview / confirmation

- **Purpose / entry:** explicit, reviewable, idempotent generation of dated classes from the recurring grid.
- **Layout:** target week selector (week starts Monday), summary (`12 clases nuevas`, `3 existentes`, `8 alumnos fijos que se agregarán`), expandable dated-class preview grouped by day. Existing classes are read-only rows tagged `Ya generada`.
- **Primary action:** `Generar clases de la semana`; secondary `Volver a la grilla`.
- **Confirmation/result:** after success, display exactly created vs unchanged records and link `Ver clases de esa semana`. Repeating same week produces no duplicates and renders `No hay clases nuevas para generar.`
- **Membership treatment:** fixed students with expired memberships are listed as `No se agrega: abono vencido`; no recovery or compensation option exists.

### `O-09` Dated class detail and attendance list

- **Purpose / entry:** operate one dated generated class.
- **Layout:** class header (type, date/time, teacher, `Programado`/`Suspendido`, occupancy), attendee list divided into `Fijas` and `Sueltas`. Each row has client, status, and masked phone. Only `Suelta` rows display an independent `Cobrado` marker; `Fija` rows display `Abono vigente`/`Abono vencido` context and never a per-class payment marker. Owner-only notes are behind a deliberate detail disclosure.
- **Actions:** class-level `Agregar reserva suelta` and, only when enabled, `Registrar llegada sin reserva`; per attendee `Confirmar`, `Marcar atendido`, `Agregar observación`, `Marcar ausente`, `Registrar aviso de falta`, `Suspender clase`. `Registrar cobro` appears **only** on a `Suelta` row and opens the same charge form pattern as `O-05`, contextualized to the class. `Fija` rows have no per-class charge action: membership payment is recorded exclusively in `O-12` at creation/renewal.
- **Class attendance notes:** `Agregar observación` is available for every attendee and reuses the `O-05` customer-note form unchanged, including the intentional disclosure of existing sensitive notes and the health-privacy notice. Saving it updates the customer record. When the owner marks class attendance, the same form can be opened in that attendance flow so an optional observation updates the customer record at the same time; it is never exposed in public booking, reminders, reports, or WhatsApp notices.
- **Rules presented in UI:** cancellation frees this week’s place; membership student loses that class and gets no recovery/refund; drop-in pays on arrival; suspend blocks new additions and does not alter the recurring grid.
- **Manual entry, availability, and conflict:** `Agregar reserva suelta` is an owner-assisted reservation onto an existing future dated class and remains available even if the business has disabled `admite atención sin turno`. It validates and **blocks** on actual class or simultaneous capacity exactly like public booking, retaining data and offering alternatives. `Registrar llegada sin reserva` is a spontaneous at-arrival class entry and is shown only when that setting is enabled. Only this already-occurred entry shows a persistent over-capacity warning and requires `Confirmar y registrar de todos modos`, but never blocks. This non-blocking spontaneous-entry rule is the sole conservative product decision pending validation of the RF-11/Rule 11 contradiction.
- **States:** suspended list stays visible, actions reduce to message detail/history; zero-attendee class says `No hay personas anotadas.`

### `O-10` Suspend-class notice sheet

- **Purpose / entry:** suspend one dated class and prepare individual WhatsApp notices.
- **Layout:** irreversible context panel (`Solo se suspende esta fecha; la grilla semanal no cambia.`), required `Motivo de suspensión` field, affected attendee count, each attendee’s name, masked phone, and editable prepared text. Default Spanish copy:

  > `Hola, {nombre}. Te avisamos que la clase de {tipo} del {fecha} a las {hora} fue suspendida por {motivo}. Esta clase no se recupera. Disculpá las molestias.`

- **Validation/actions:** `Motivo de suspensión` is mandatory; show `Indicá el motivo para preparar los avisos.` until provided. `Copiar mensaje`, `Abrir WhatsApp` per person; `Suspender clase y preparar avisos` requires confirmation. It does not send automatically.
- **Confirmation:** explain fixed memberships lose that occurrence, drop-ins are not charged, no refund/recovery is created. After confirmation status is `Suspendido` and messages remain retryable/copyable.

### `O-11` Membership list

- **Purpose / entry:** monitor monthly fixed-class memberships.
- **Layout:** date-aware filter tabs `Vigentes`, `Vencen pronto`, `Vencidos`, `Todos`; search by phone/name; list cards show student, weekly frequency, fixed classes count, payment date, expiration, amount, and status.
- **Actions:** primary `Registrar abono`; row opens form/detail; `Renovar` is prominent for expired or expiring membership.
- **States:** no membership state links to `Registrar abono`; expiration counts identify time scope. The screen never offers packs, unlimited passes, recovery, or refunds.

### `O-12` Membership create / renew form

- **Purpose / entry:** create or renew a monthly membership and record its payment.
- **Layout:** three progressive sections: `Alumno`, `Clases fijas`, `Pago y vencimiento`; review panel remains visible on desktop and follows form on mobile.
- **Fields:** phone lookup (create name, surname, health-relevant note only for new customer), frequency per week, selectable recurrent grid classes, payment date, amount; computed expiration date one month later.
- **Validation:** exactly the chosen fixed classes must be available/valid grid options; explain capacity consequences before save. Phone identifies the student. A renewal records the new payment and renewed dates; do not silently mutate past payment history.
- **Success:** `Abono vigente hasta el {fecha}. Se agregará a sus clases fijas al generar la semana.` Link to generation if the upcoming week needs it.

### `O-13` Expenses list and `O-14` expense form

- **Purpose / entry:** record and review business expenses.
- **List layout:** period selector, total for selection, type chips, searchable chronological list with date, description, type, amount; primary `Registrar gasto`.
- **Form fields:** date (default today), amount with currency prefix, expense type drawn only from business-configured types, description. Labels: `Fecha`, `Monto`, `Tipo de gasto`, `Descripción`.
- **Validation:** amount must be greater than zero; type required; date required. Saving retains field values on error. Edit is allowed; archive/delete requires a destructive confirmation that names reporting impact if applicable.
- **Cross-link:** Reports filtered to the same period, especially expenses by type.

### `O-15` Inventory list and declared-stock form

- **Purpose / entry:** declare current stock and minimum, never infer consumption.
- **Layout:** summary counts (`Por reponer`, `Al día`), filter `Todos` / `Bajo mínimo`, rows with item, declared amount, minimum, last declaration timestamp, and explicit warning. Primary `Actualizar existencias`; `Agregar insumo` in overflow/empty state.
- **Form:** name, current declared quantity, minimum quantity. Saving copy: `La existencia se actualiza con el valor que declaraste; no se descuenta sola por las atenciones.`
- **Actions/states:** update item opens numeric form; below-minimum item uses urgency text + color; archive preserves history; scheduled reminder failures appear in Notification settings, not as an invented stock-consumption event.

### `O-16` Reports overview and metric detail

- **Purpose / entry:** understand a selected period and compare it with another period without pretending unspecified formulas are settled.
- **Layout:** period selector plus optional compare-period control; top result card `Cobros de atenciones individuales + clases sueltas + abonos − gastos = resultado`; metric list/cards, then tables/charts only when data supports them. Charts must have an adjacent data table and text summary.
- **Metrics:** revenue by service; expenses by type; recurrent customers; customers who stopped attending; absence count; worker production. Every title includes an info tooltip describing included records and time period.
- **Definition warning:** use `Definición pendiente de validación de negocio` for recurrence, stopped-attending, production, and any formula SRS does not define. Do not fabricate thresholds in UI. Result arithmetic and source periods are explicit.
- **States:** no data explains what is missing; compare unavailable is not treated as zero. Export is not in scope.

### `O-17`–`O-23` Settings

Settings uses a sub-navigation list on desktop and a list of labeled rows on mobile. Every subsection has a clear Save action, success state, and “Changes affect future availability/generation” help where applicable.

| ID | Subsection | Fields and actions | Validation / preservation rule |
|---|---|---|---|
| `O-17` | `Negocio y acceso` | Business name, owner username/password, owner contact phone, simultaneous capacity, walk-in toggle. | Capacity rule blocks invalid configuration. One account only; no add-user action. |
| `O-18` | `Servicios y precios` | Service name, price, capacity, Application/Wait/Finishing durations; list active/archived. | Total duration >0; non-negative segments. Archive referenced service rather than delete. Segment help explains Wait does not occupy worker. |
| `O-19` | `Trabajadoras y horarios` | Name, surname, phone, CUIL, weekly hours, pauses. | End after start; pauses within work hours. Worker archive retains historical appointments and has no account controls. |
| `O-20` | `Grilla semanal` | Shortcut row that opens the `O-07` editor (class type, day, start, teacher; active/archived tiles). | Group-only section. No duplicate editor. Changes affect future weeks; generated classes remain dated records. |
| `O-21` | `Tipos de gasto` | Business-defined type list, add/rename/archive. | Referenced types archive rather than delete. |
| `O-22` | `Avisos` | Turn reminder lead time, daily-summary time, stock-review frequency, notification failure list/retry. | Defaults 24h, 21:00, weekly. Turn reminders target only `Pendiente` annotations and include a turn/class summary plus opaque management link; retry shows attempt result only, never open/read analytics. |
| `O-23` | `Reserva pública y accesibilidad` | Public booking-link copy/open; high-contrast preference; larger text preference; plain-language preview. | Public preview contains no customer information. Accessibility preferences apply to owner UI; public UI meets baseline AA independently. |

### `O-22a` Reminder content and notification failures

- **Purpose / entry:** owner-only detail within `Avisos`, entered from a failure row or the reminder settings preview.
- **Reminder contract in UI:** at the configured lead time, RF-17 targets **only** annotations in `Pendiente`. The preview shows the mandatory concise content: customer name, business name, service/class, date, time, worker/teacher where relevant, and an opaque reservation-management link. Example: `Hola, Ana. Te recordamos tu turno de Corte con Marina el martes 14 a las 10:00. Confirmá o avisá si no podés venir: {enlace privado}`.
- **Link behavior:** the opaque link opens only that reservation’s limited `P-06` detail. It offers confirm/cancel and, only for an individual appointment, reschedule. A group class has no reschedule affordance.
- **Failure behavior:** a failed automated reminder, daily summary, or inventory-review reminder shows notification type, intended recipient masked phone, scheduled/attempt time, error-safe reason, and `Reintentar`. Retry does not claim delivered/read/open status.

### `P-01` Public booking landing

```text
┌──────────────────────────────────────┐
│ {Nombre del negocio}                 │
│ Reservá tu turno                     │
│ Elegí cómo querés atenderte          │
│ [ Corte · 45 min ]                   │
│ [ Hatha · martes 18:00 ]             │
│ ¿Ya reservaste? Gestionar mi reserva │
└──────────────────────────────────────┘
```

- **Purpose / entry:** public, own shareable business link; starts a reservation with no account.
- **Layout:** business name, brief plain-language instruction, service/type options grouped `Turnos individuales` and `Clases`, persistent `Gestionar mi reserva` link. Without a private link, that route starts phone plus one-time-code verification and never exposes a generic customer history. Do not show prices if not configured for public display; the SRS does not mandate a pricing presentation, so generation must keep this configurable/hidden by default.
- **Accessibility:** 18px baseline, 56px choices, progress shown after selection, no motion-dependent choice, keyboard-friendly focus.

### `P-02` Public individual availability

- **Purpose / entry:** choose worker first, then a valid individual time slot.
- **Layout:** progress `1 Servicio · 2 Profesional · 3 Horario · 4 Tus datos`; worker large-select cards; date strip; available-time buttons; selected booking summary. Only after a worker is selected should slots appear.
- **Behavior:** show same-worker nearest alternative if requested date is unavailable. Availability applies worker schedule/pauses, active segments, capacity, and cancellations, but uses customer-facing language: `No hay horarios ese día. Mirá el próximo disponible con Marina.`
- **Actions:** `Continuar` only after a slot. Back preserves selection. No ability to view others’ bookings or choose a room/seat.

### `P-03` Public group class availability

- **Purpose / entry:** select a dated class with available space for a drop-in reservation.
- **Layout:** date/week grouping, class cards with type, date, time, teacher, duration when useful, and available-place wording (`Quedan 3 lugares` / `Sin lugares`). Full classes are not selectable and expose a nearby alternative list.
- **Behavior:** only `Programado` dated classes with availability appear; suspended classes do not appear as bookable. Fixed membership enrollment is not available publicly because the SRS assigns membership registration to the owner.

### `P-04` Public contact and review

- **Purpose / entry:** identify customer by phone and obtain a deliberate final confirmation.
- **Layout:** selected appointment/class summary, fields `Teléfono`, `Nombre`, `Apellido`; concise privacy notice; review details; primary `Confirmar reserva`.
- **Validation:** Argentine-friendly phone formatting but accept a sensible canonical number; required name/surname/phone; conflict refresh on submission handles a recently occupied selection. Never offer password creation or “create account.”
- **Privacy copy:** `Usamos estos datos para registrar tu reserva y contactarte sobre este turno.`

### `P-05` Reservation confirmation

- **Purpose / entry:** confirm a successful reservation and preserve safe access to next actions.
- **Layout:** success heading, date/time, service/class, worker, customer status `Pendiente de confirmar`, and action `Gestionar esta reserva`. The action carries an opaque unguessable token; copy/share link is optional but warns to keep it private.
- **Copy:** `Tu reserva quedó registrada. Te vamos a enviar un recordatorio antes del turno.`
- **Rules:** the token opens only this reservation’s limited detail and allowed actions, never a customer history or other reservations. Individual reservation management can offer rescheduling; group reservation management only confirmation/cancellation. No payment or invoice is shown.

### `P-06` Reservation management and phone verification

- **Purpose / entry:** customer self-service without an account. A private opaque-token URL opens only its matching reservation’s limited detail; the generic `Gestionar mi reserva` route has no token and starts verification.
- **No-token verification:** first collect `Teléfono`; then send and collect a one-time verification code. Only after successful verification may the route list the reservations associated with that verified phone that are eligible for management, using minimal cards (business, date/time, service/class, status); it is not a customer-history screen. This is a UX/security assumption pending a technical contract for code transport, expiry, attempt limits, and abuse controls.
- **Token detail:** token access never lists sibling reservations or personal history. It displays only service/class, date/time, worker/teacher, status, and permitted actions for that reservation.
- **Actions:** `Confirmar que voy`, `No voy a poder asistir`; for individual capacity-1 reservation only, `Cambiar horario`. Cancellation clearly says the place is released. Individual reschedule locks the worker and reuses `P-02` availability behavior; success results in `Confirmado`.
- **Error states:** invalid/expired token, phone, or code uses generic `No pudimos verificar esta reserva. Revisá el enlace, el teléfono o el código.` Do not disclose a reservation’s existence. Expired/cancelled/suspended states display their known consequence and a route back to public booking.

---

## 7. End-to-end flows

### 7.1 Owner onboarding

1. Owner signs in at `O-01` and enters `O-02`.
2. They configure business/account/operating parameters, including capacity and notification defaults.
3. They create at least one service, then workers with schedules and pauses.
4. If group services exist, the system points to grid setup but does not fabricate a grid.
5. Owner reviews, resolves validation, completes onboarding, and lands on `O-03 Jornada`.

### 7.2 Individual public booking

1. Customer opens public link `P-01`, chooses a capacity-1 service.
2. In `P-02`, they choose the worker before the date and a valid time; Wait segments do not block the worker but count toward simultaneous business capacity.
3. In `P-04`, customer enters phone/name/surname and confirms.
4. System atomically rechecks worker, service/class capacity, and simultaneous capacity. On conflict, retain data and return to slots; public booking is blocked rather than overridden. Otherwise create `Reservado`/`Programado` appointment with customer `Pendiente`.
5. `P-05` shows confirmation and opaque limited-detail management link.

### 7.3 Group public booking

1. Customer chooses a group type at `P-01`.
2. `P-03` displays only generated, programmed dated classes with space.
3. Customer provides contact data and confirms at `P-04`.
4. System rechecks class and simultaneous capacity and adds a `Suelta` attendee in `Pendiente`; full class blocks the reservation and returns alternatives.
5. Confirmation gives a limited-detail management link; it does not offer a membership or class recovery.

### 7.4 Confirmation, cancellation, and individual rescheduling

1. Customer follows a `P-06` opaque link for the matching limited detail, or uses the no-token route with phone plus one-time code to retrieve only verified associated reservations.
2. `Confirmar que voy` changes only customer status to `Confirmado`.
3. `No voy a poder asistir` warns that the place becomes available; it changes status to `Cancelado`. A fixed-class student loses the occurrence without refund or make-up.
4. For individual appointment only, `Cambiar horario` presents slots for the **same worker**; selecting one moves the appointment and leaves it `Confirmado`.
5. Group classes have no rescheduling control.

### 7.5 Weekly grid and generation

1. Owner configures recurring tiles in `O-07`/`O-20`.
2. Owner selects `Generar semana`, target week, and reviews `O-08` preview.
3. Preview distinguishes new dated classes, existing dated classes, fixed students to add, and expired memberships excluded.
4. Owner confirms. System creates only absent instances and fixed annotations; existing week records remain unchanged.
5. Owner opens each dated list via `O-09`.

### 7.6 Membership creation and renewal

1. Owner opens `O-12`, searches by phone, and creates customer details only if absent.
2. Owner selects frequency and fixed grid classes, records payment date and amount.
3. UI computes one-month expiration, presents review, and creates a `Vigente` membership plus payment.
4. At weekly generation, valid fixed classes receive the fixed annotation. On expiration without renewal, future fixed annotations are not added and the place becomes available.
5. Renewal creates the new monthly period/payment; it does not create pack credit or recovery.

### 7.7 Daily operation and walk-in

1. Owner opens `O-03` to see timeline, outstanding confirmations, unpaid attended customers, and daily collected total.
2. They tap a row to confirm, mark attended/absent, or record payment where a per-attendance charge applies; attendance and the paid marker remain independently visible for individual and `Suelta` annotations only.
3. For a walk-in, owner chooses `Registrar atención`, enters actual details, sees any conflict, explicitly confirms the persistent warning when present, and may save despite it. Date/time are assigned automatically at save and cannot be edited.
4. The created individual appointment has origin `Sin reserva`; resulting payment is tied to it. Historical timestamp correction is out of scope pending validation.
5. If owner decides there is no room, the UI records nothing and offers return-later/public-reservation guidance.
6. For a manually added class `Suelta`, the owner may make an assisted reservation on an existing future class regardless of the walk-in setting; it validates and blocks by class/simultaneous capacity like a public reservation. An at-arrival entry is available only when `admite atención sin turno` is enabled; because it has already occurred, it instead uses the persistent over-capacity warning and explicit confirmation, then saves without blocking. Only this spontaneous-entry exception is pending product validation of the RF-11/Rule 11 tension.

### 7.8 Class suspension and prepared WhatsApp notices

1. Owner opens a dated class and chooses `Suspender clase`.
2. `O-10` explains it affects only that date, requires a suspension reason, names affected attendees, and prepares per-person text using that reason.
3. Owner confirms suspension. The class becomes `Suspendido`; no new booking, recovery, or refund occurs.
4. Owner uses `Copiar mensaje`/`Abrir WhatsApp` for each attendee. This is prepared manual sending, not automatic delivery.

### 7.9 Expenses, inventory, reports, and notifications

1. Owner records expenses in `O-14` with required type/amount/date.
2. Owner declares stock in `O-15`; stock is never changed by services or classes.
3. Below-minimum stock receives a visible alert. Weekly reminder is scheduled per `O-22`; failed automatic attempts are visible and retryable. Declared quantity remains unchanged by any service/class activity.
4. Owner selects a reporting period in `O-16`: result is individual-attention charges + drop-in-class charges + membership charges − expenses; other metrics identify their scope and pending formula validation where required.
5. RF-17 reminds only `Pendiente` annotations with their turn/class summary and opaque management link. Reminder, next-day summary, and stock-review failures are shown in `O-22`; retry gives a successful/failed attempt result, not delivery analytics.

---

## 8. Requirement-to-screen traceability

| Requirement | Primary screens | Supporting behavior |
|---|---|---|
| RF-01 Configure business | `O-02`, `O-17`, `O-21`–`O-23` | Single account, capacity, walk-ins, contact and notice parameters |
| RF-02 Define service | `O-02`, `O-18` | Price, capacity, three duration segments, archive |
| RF-03 Define worker/schedule | `O-02`, `O-19` | Worker data, hours, pauses; no worker account |
| RF-04 Weekly grid class | `O-07`, `O-20` | Recurring type/day/time/teacher |
| RF-05 Membership | `O-11`, `O-12` | Phone identification, fixed classes, payment, one-month expiration |
| RF-06 Generate weekly classes | `O-08`, `O-09` | Explicit preview, idempotency, fixed annotations |
| RF-07 Reserve appointment | `P-01`–`P-05`, `O-04`, `O-09` | Public and owner-assisted future class reservations block on capacity, identify by phone, and recheck availability |
| RF-08 Attendance notice | `P-06`, `O-05`, `O-09` | Confirm/cancel and place release; opaque link or verified no-token access |
| RF-09 Reschedule appointment | `P-06`, `O-05` | Individual only; same worker, valid slot |
| RF-10 Suspend class | `O-09`, `O-10` | Dated class only, prepared WhatsApp notices |
| RF-11 Attention and charge | `O-03`, `O-05`, `O-06`, `O-09` | Independent attended/paid markers for individual/`Suelta`; private customer notes update from individual/class attendance; membership payment only at `O-12`; only already-occurred manual entries warn, require confirmation, and do not block |
| RF-12 Absence | `O-03`, `O-05`, `O-09` | Mark no-show per customer |
| RF-13 Expense | `O-13`, `O-14` | Typed dated expense |
| RF-14 Inventory | `O-15` | Declared stock, minimum alert, no consumption automation |
| RF-15 Current day | `O-03` | Timeline, action backlog, total collected |
| RF-16 Reports | `O-16` | Individual charges + drop-in charges + membership charges − expenses; comparison and pending metric definitions |
| RF-17 Appointment reminder | `O-22`, `O-22a`, `P-06`, `S-02` | Only `Pendiente` annotations; summary plus opaque management link; configured lead time and failure/retry visibility |
| RF-18 Daily agenda summary | `O-22`, `S-02` | Configured time/contact, failure/retry visibility |
| RF-19 Stock-review notice | `O-22`, `O-15`, `S-02` | Configured periodicity, failure/retry visibility |

---

## 9. Stitch preparation

### 9.1 Reusable global visual prompt

> Design a mobile-first responsive web app named **WorkUp**, a calm operational tool for a Spanish-speaking Argentine small service business. Use a warm paper background `#F7F4ED`, white surfaces, dark ink `#20251F`, sage primary `#2E5B4A`, terracotta destructive `#9A432C`, and visible blue `#004E9C` focus rings. Typography is Atkinson Hyperlegible or a humanist sans, 18px body minimum, large readable time and money numerals, 48px touch targets, 8/12px radius, quiet borders and minimal shadows. Avoid generic cold SaaS dashboards, gradients, tiny dense tables, excessive charts, or decorative clutter. Owner UI uses the persistent WorkUp shell (mobile header + bottom navigation, desktop left rail); public booking is a separate minimal, no-login shell. All UI copy is clear Argentine Spanish. Pair every status color with label and icon; show accessibility-conscious focus, empty, loading, error, success, and conflict states where relevant. Never use icon fonts or ligature names such as `warning`, `error`, `event_busy`, or `sync_problem`; render real inline SVG icons or omit the icon when the text label is sufficient.

### 9.2 Ordered generation batches

Use the global prompt unchanged as the first paragraph of each generation request. Generate in order to lock shared components before detailed operation.

| Batch | Screen family / IDs | Concise Stitch prompt |
|---|---|---|
| 1 | Shell and feedback: `O-01`, owner mobile/desktop shell, `S-01`, `S-02` | Create WorkUp owner login and responsive owner shell with Jornada active, explicit hamburger/More navigation, profile/account popover, logout confirmation, bottom navigation on mobile and left rail on desktop; include accessible confirmation dialog, skeleton, empty, recoverable error, success, and persistent manual-conflict warning/confirmation patterns. Do not create synchronization, offline, or stale-data states. Use real inline SVG icons or no icon; never render icon-font ligature names as text. |
| 2 | Onboarding/settings: `O-02`, `O-17`–`O-23`, `O-22a` | Create progressive owner onboarding and settings families with large labeled forms, Spanish helper text, archive-not-delete confirmations, service segment editor, worker schedule/pauses, recurring grid, `O-22a` reminder preview for only Pendiente reservations plus notification failures/retry, public-link and accessibility preferences. |
| 3 | Daily operation: `O-03`–`O-06` | Create Jornada, Agenda, individual appointment detail, and walk-in sheet showing timeline readability, independent attendance/payment markers only where per-attendance payment applies, privacy-protected customer-note and charge forms, segment timeline, filters, and a persistent conflict warning requiring confirmation but never blocking manual save; walk-in time is automatic and read-only. |
| 4 | Group operation: `O-07`–`O-10` | Create weekly classes grid, idempotent generate-week preview, dated class attendance detail with reusable private `O-05` observation form, and suspension sheet with required reason plus editable prepared WhatsApp messages; show assisted future class reservations blocking by capacity like public booking, setting-gated at-arrival entries with non-blocking warning/confirmation, Cobrado/actions only for Suelta, membership payment only at creation/renewal, and no recovery/refund. |
| 5 | Business records: `O-11`–`O-16` | Create memberships list/create-renew form, expenses list/form, declared inventory list/form, and reports overview whose result is individual charges + drop-in charges + membership charges − expenses; use accessible data tables and tooltip-marked pending formulas for other metrics. |
| 6 | Public booking: `P-01`–`P-06` | Create a separate, extremely simple public mobile booking flow: availability that blocks public over-capacity reservations, choose worker then individual slot or dated group class, contact/review, confirmation with private opaque limited-detail link, and no-token management using phone plus one-time verification code that returns only verified associated reservations; no accounts, payments, or other customer data. |

---

## 10. Coverage checklist

- [x] Mobile-first owner and public responsive shells specified.
- [x] One owner account; workers are records, not users.
- [x] Individual availability preserves wait-segment behavior and worker choice.
- [x] Group grid, explicit weekly generation, dated classes, capacity, fixed memberships, drop-ins, and suspension covered.
- [x] Attendance status and payment marker are independently visible where per-attendance payment applies; `Fija` payment remains on the membership only, and class attendance can update private customer notes through the reused `O-05` form.
- [x] Public and owner-assisted future class reservations block capacity conflicts. Only already-occurred manual records warn persistently, require confirmation, and do not block; assisted class reservations are independent of the walk-in setting, while at-arrival entries require it.
- [x] Walk-in time is automatic and immutable; historical timestamp correction is explicitly out of scope pending validation.
- [x] Public booking has no account, protects customer data, and supports opaque-link or phone-plus-one-time-code management.
- [x] Cancellation, individual-only rescheduling, absence, and daily operation covered.
- [x] Expenses, declared inventory with no consumption automation, alerts, reports, settings, and notification failure/retry covered.
- [x] WCAG 2.1 AA baseline, keyboard, reader, high-contrast, large-text, and responsive requirements included.
- [x] Explicit exclusions prevent unsupported CRM, payments, invoices, accounts, inventory automation, rooms/seats, packs, recovery, and refunds.
- [x] RF-01 through RF-19 trace to concrete screens.
- [x] Stitch prompts describe generation inputs only; no generation is claimed.

## 11. Decisions still requiring stakeholder validation

### Blocks implementation (functional/domain decision)

| Decision needed | Why it blocks |
|---|---|
| Exact authentication recovery process for the one owner account | Login recovery must be secure without inventing an unsupported account workflow. |
| Exact notification transport/technical contract and retry limits | RF-17–19 require sending; UI can expose failures but cannot define the delivery integration. |
| Phone one-time-code transport, expiry, rate limits, and abuse protections | No-token `Gestionar mi reserva` needs this contract to be secure without becoming a customer account. |
| Resolution of spontaneous at-arrival `Suelta` over-capacity behavior | Owner-assisted future class reservations block just like public reservations. This design alone permits a setting-gated spontaneous at-arrival `Suelta` entry with persistent warning and confirmation, conservatively extending the manual-record rule; RF-11/Rule 11 needs explicit product confirmation. |
| Historical correction policy for automatic walk-in timestamps | The design intentionally excludes backdating/editing to avoid reintroducing D-12. |
| Formal formulas/thresholds for “recurrent customer,” “stopped attending,” and “worker production” | RF-16 names the indicators but does not define their calculations. |
| Whether public prices are displayed | SRS defines a tariff but does not state public price visibility; this affects booking copy and screen content. |

### Does not block visual generation

| Decision to confirm | Conservative visual default in this specification |
|---|---|
| Opaque reservation-token lifetime | Token opens only the matching limited detail; exact lifetime follows the security contract. |
| Exact labels/business terminology by vertical | Use SRS glossary and generic `trabajadora / profesora` contextual labels. |
| Empty-state illustration style | Sparse warm line illustration; operational content remains primary. |
| Default public pricing display | Hide price by default pending decision; keep layout able to reveal it. |
