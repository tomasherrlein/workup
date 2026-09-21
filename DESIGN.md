---
name: WorkUp
description: The counter notebook of an appointment-based service business, rebuilt as a fast trade tool.
colors:
  mat-night: "#002F28"
  pine-deep: "#00493D"
  pine: "#006251"
  mat-mid: "#1B9579"
  mat-light: "#B4DFD0"
  pine-wash: "#E1F4ED"
  pine-mist: "#F0F9F6"
  grid-yellow: "#F5D240"
  counter-ink: "#151C1B"
  counter-ink-muted: "#4D5855"
  paper: "#F4F8F6"
  sheet: "#FCFEFD"
  rule: "#D2D9D7"
  control-edge: "#798380"
  state-pendiente-text: "#7A4A00"
  state-pendiente-bg: "#FBF1DC"
  state-confirmado-text: "#1F4E79"
  state-confirmado-bg: "#E4EEF7"
  state-atendido-text: "#296824"
  state-atendido-bg: "#E2F6D8"
  state-alert-text: "#9B2C2C"
  state-alert-bg: "#FBE7E7"
typography:
  display:
    fontFamily: "Atkinson Hyperlegible Next, Arial, sans-serif"
    fontSize: "32px"
    fontWeight: 700
    lineHeight: 1.15
    fontFeature: "tnum"
  headline:
    fontFamily: "Atkinson Hyperlegible Next, Arial, sans-serif"
    fontSize: "24px"
    fontWeight: 700
    lineHeight: 1.25
  title:
    fontFamily: "Atkinson Hyperlegible Next, Arial, sans-serif"
    fontSize: "20px"
    fontWeight: 700
    lineHeight: 1.3
  body:
    fontFamily: "Atkinson Hyperlegible Next, Arial, sans-serif"
    fontSize: "18px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Atkinson Hyperlegible Next, Arial, sans-serif"
    fontSize: "16px"
    fontWeight: 700
    lineHeight: 1.3
rounded:
  tag: "2px"
  control: "4px"
  sheet: "8px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "40px"
components:
  button-primary:
    backgroundColor: "{colors.pine}"
    textColor: "{colors.sheet}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    height: "52px"
    padding: "0 24px"
  button-primary-hover:
    backgroundColor: "{colors.pine-deep}"
  button-secondary:
    backgroundColor: "{colors.sheet}"
    textColor: "{colors.counter-ink}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    height: "52px"
  status-tag-pendiente:
    backgroundColor: "{colors.state-pendiente-bg}"
    textColor: "{colors.state-pendiente-text}"
    typography: "{typography.label}"
    rounded: "{rounded.tag}"
    padding: "2px 8px"
  status-tag-cobrado:
    backgroundColor: "{colors.counter-ink}"
    textColor: "{colors.sheet}"
    typography: "{typography.label}"
    rounded: "{rounded.tag}"
    padding: "2px 8px"
  agenda-row:
    backgroundColor: "{colors.sheet}"
    textColor: "{colors.counter-ink}"
    typography: "{typography.body}"
    padding: "12px 16px"
  nav-item-active:
    backgroundColor: "{colors.pine-wash}"
    textColor: "{colors.pine}"
    typography: "{typography.label}"
---

<!-- Pre-implementation: tokens are decided and contrast-checked, but no code exists yet. Re-run $impeccable document once there is code, to capture real components and write the DESIGN.json sidecar. -->

# Design System: WorkUp

## 1. Overview

**Creative North Star: "The Cutting Mat"**

WorkUp looks like a working ledger laid on a workshop cutting mat: ruled lines, hard columns, bold times, and marks that mean something. It is a tool you use standing at a counter with one hand, in good daylight, with a customer waiting. Every screen is a list you scan top to bottom; structure comes from rules and alignment, not from boxes. The palette comes from the self-healing cutting mat: a deep pine green frames the app, mid greens draw the working marks (occupancy bars, selection), the saturated pine is the owner's pen for actions, and a single grid yellow marks position on the dark frame, like the grid printed on the mat.

The system is dense on purpose and calm by restraint. Time sits in a fixed left column in bold tabular numerals, the person and service read next to it, and the state sits right-aligned as a hard-edged tag. Sections are separated by a 2px ink rule and a plain sentence-case heading, like the ruled headers of an accounts book.

It explicitly rejects the anti-references in PRODUCT.md: the **"warm AI" look** (cream backgrounds, sage green, rounded pills everywhere, soft stacked cards, uppercase tracked micro-labels), **wellness / spa apps** (pastels, candles and leaves, thin "zen" type), and **legacy management systems** (dense grey tables, Windows-style menus, endless forms). The greens here are saturated workshop greens on cool white, never desaturated sage on cream.

**Key Characteristics:**
- Full-width rows divided by 1px rules; cards are the exception, never the layout.
- Near-square geometry: 4px controls, 2px tags, 8px only on bottom sheets.
- A pine tonal family: Mat Night frames (app bar, rail, section rules, time column), Pine acts (buttons, links, focus), mint tints mark (occupancy bars, selection, summary band).
- Grid Yellow is the only accent and lives only on the dark frame.
- State is always a word from the glossary, in a rectangular tag with an icon.
- Flat at rest; the only shadow belongs to sheets that float over content.

## 2. Colors

A cool white paper with a barely perceptible green cast, framed by cutting-mat greens and one grid yellow; state colors stay functional and are never replaced by the brand green.

Values were defined in OKLCH and are stored as hex. Contrast ratios below are WCAG 2.1.

### Primary (the pine family, dark to light)
- **Mat Night** (#002F28, oklch 0.27 0.055 182): the frame. Mobile app bar and desktop rail background (Sheet text on it: 14.4:1), public booking header band, 2px section rules, section heading text, and row times.
- **Pine Deep** (#00493D): pressed and hover state of Pine.
- **Pine** (#006251, oklch 0.44 0.09 176): the owner's pen. Primary buttons, row actions and links, selected date or slot border, and the 3px focus ring. Sheet text on it: 7.2:1; on Paper as text: 6.8:1.
- **Mat Mid** (#1B9579): working marks, non-text only. Occupancy and progress fill, selected-slot inner mark. 3.7:1 against Sheet.
- **Mat Light** (#B4DFD0): inactive text on Mat Night (10.1:1) and dividers inside the app bar.
- **Pine Wash** (#E1F4ED): active navigation item and selected row or slot background.
- **Pine Mist** (#F0F9F6): the day summary band and sticky action bar background. Never the whole app background.

### Accent
- **Grid Yellow** (#F5D240, oklch 0.87 0.16 95): position marks on the dark frame only. Active item indicator and icon on the desktop rail, the 3px active indicator in the mobile app bar, and the "today" mark in date headers drawn on Mat Night. 9.9:1 on Mat Night; 4.9:1 on Pine. Never used on light surfaces (1.5:1 on Sheet), never as a tag fill, never as text on Paper or Sheet.

### Neutral
- **Counter Ink** (#151C1B): all primary text (16.1:1 on Paper), icons, and the solid `Cobrado` tag.
- **Counter Ink Muted** (#4D5855): secondary lines (service, worker, masked phone). 6.9:1 on Paper.
- **Paper** (#F4F8F6): app background. Cool with a faint green cast; it must never drift warm or cream.
- **Sheet** (#FCFEFD): rows, inputs, sheets, and light navigation bars.
- **Rule** (#D2D9D7): 1px dividers between rows. Decorative only; never the sole boundary of an interactive control.
- **Control Edge** (#798380): input, checkbox, and secondary-control borders at rest. 3.9:1 on Sheet, 3.7:1 on Paper.

### State colors (functional roles)
- **Pendiente**: amber text (#7A4A00) on amber wash (#FBF1DC), clock icon.
- **Confirmado**: blue text (#1F4E79) on blue wash (#E4EEF7), check icon.
- **Atendido / Vigente**: leaf green text (#296824, oklch hue 142) on leaf wash (#E2F6D8), double-check icon. 5.9:1. Deliberately yellower than the brand pine (hue 176) so it never reads as a selection or brand mark.
- **Cobrado**: Sheet text on solid Counter Ink, coin icon. The strongest tag, because money in hand is a fact.
- **Ausente / Vencido / Suspendido**: red text (#9B2C2C) on red wash (#FBE7E7), cross icon.
- **Programado**: Pine text with a 1px Pine border, no fill, calendar icon.
- **Cancelado**: Counter Ink Muted text with line-through on the row's time, no fill.

### Named Rules
**The Pine Family Rule.** Each green has one job: Mat Night frames, Pine acts, mints mark. Only one solid Pine button per screen; if two are visible, one of them is wrong.

**The Grid-On-The-Mat Rule.** Grid Yellow appears only on Mat Night or Pine surfaces, as a thin indicator or icon. It never fills a tag, a button, or a light background, and never carries a state.

**The States-Stay-Honest Rule.** The brand green never replaces a client state color. `Pendiente` stays amber, `Confirmado` blue, `Atendido` leaf green, `Ausente` red, `Cobrado` ink. Only the turn state `Programado` uses a Pine outline.

**The Two-Greens Rule.** `Atendido` leaf green and brand Pine must stay visibly apart: never show an `Atendido` tag on a Pine Wash selected row without its word and double-check icon, and never tint a selection with leaf green.

**The Adjacent-Greens Rule.** Pine and Mat Mid are only 2.0:1 apart; never place them touching without a 2px gap, and always state the value of a bar in text.

**The No-Cream Rule.** Backgrounds stay cool. Any neutral with a visible warm or yellow cast is prohibited; the only yellow in the system is Grid Yellow on the frame.

**The Word-First Rule.** No state is ever communicated by color alone; the glossary word and an icon are mandatory.

## 3. Typography

**Body Font:** Atkinson Hyperlegible Next (with Arial, sans-serif)

**Character:** One hyperlegible family for everything. Character comes from weight and scale contrast, not from a second typeface: bold tabular numerals for time and money, regular weight for names and services.

### Hierarchy
- **Display** (700, 32px, 1.15, tabular numerals): the day's collected total and the period result. Only one per screen.
- **Headline** (700, 24px, 1.25): page title (`Jornada`, `Agenda`, `Abonos`).
- **Title** (700, 20px, 1.3): section headings in sentence case (`Ahora`, `Más tarde`, `Fijas`) and sheet titles, sitting on a 2px Mat Night rule.
- **Body** (400, 18px, 1.5): names, services, form values, explanations. Max 65ch in explanatory text.
- **Label** (700, 16px, 1.3): buttons, tags, field labels, row times. 16px is the floor; nothing renders smaller.

### Named Rules
**The Sentence-Case Rule.** No uppercase tracked micro-labels. Section headings are sentence case in Title style.

**The Numbers-Stand-Up Rule.** Times, amounts, and capacity (`6 de 10`) always use bold tabular numerals, so columns align like a ledger.

## 4. Elevation

Flat by default. Depth comes from rules and from the contrast between Paper and Sheet, not from shadows. The only elevated surfaces are bottom sheets and the desktop profile popover, which float over content and need to separate from it.

### Shadow Vocabulary
- **Sheet lift** (`box-shadow: 0 -2px 16px rgba(21, 28, 27, 0.16)`): bottom sheets and popovers only, paired with a 40% Counter Ink scrim behind them.

### Named Rules
**The Flat-At-Rest Rule.** Rows, lists, forms, and navigation never cast shadows. If a row looks lifted, remove the shadow and add a rule.

## 5. Components

### Buttons
- **Shape:** near-square corners (4px), 52px tall; 56px on public booking.
- **Primary:** Pine fill, Sheet text, Label type, one per screen, usually sticky at the bottom on mobile.
- **Hover / Focus:** fill shifts to Pine Deep; focus adds a 3px Pine ring with 2px offset. On Mat Night surfaces the focus ring is Grid Yellow.
- **Secondary:** Sheet fill, 2px Counter Ink border, Counter Ink text.
- **Destructive:** Sheet fill, 2px red (#9B2C2C) border and red text; never a solid red fill.
- **Row action:** text button in Pine with a 20px leading icon, 48px hit area, no border.

### Status Tags
- **Style:** rectangular (2px radius), Label type in sentence case, 16px leading icon, fill and text from the state roles in Colors.
- **Placement:** right-aligned on the row's first line. `Atendido` and `Cobrado` sit side by side as independent tags.

### Agenda Row (signature component)
- **Structure:** full-width Sheet row, 1px Rule divider below. A fixed 64px left column holds the start time (Label, tabular) with the end time muted underneath; the middle holds the person or class (Body, bold name) and the service and worker (Counter Ink Muted); the right holds the status tags.
- **Time column:** start time in Mat Night, bold tabular.
- **Individual appointments:** no bar. The service occupies its full duration (`docs/srs.md`, D-25), shown as start and end time in the time column.
- **Classes:** occupancy `6 de 10` in bold tabular numerals plus a 6px occupancy bar (Mat Mid fill on Pine Wash track).
- **Actions:** one to three row actions under the text; overflow goes to a `Más acciones` sheet.

### Section Header
- **Style:** Title type in Mat Night, sentence case, on a 2px Mat Night rule, with an optional right-aligned muted count (`3 turnos`). No background, no card.

### Summary Band
- **Style:** full-width Pine Mist band under the app bar, no border radius. Label in Counter Ink Muted, the figure in Display type Mat Night, follow-up links in Pine.

### Inputs / Fields
- **Style:** Sheet fill, 1px Control Edge border, 4px radius, 52px tall, persistent Label above; placeholder text is an example only.
- **Focus:** border becomes 2px Pine plus the 3px focus ring.
- **Error:** 2px red border, red helper text with a cross icon under the field; entered value is kept.

### Navigation
- **Mobile app bar:** Mat Night background, Sheet title and icons, square corners, no shadow. When the bar holds date or view tabs, the active one carries a 3px Grid Yellow bar on its bottom edge.
- **Mobile bottom bar:** Sheet background with a 1px Rule on top. Four items with icon and Label text; the active item shows Pine text, a filled icon, and a 3px Pine bar on its top edge.
- **Desktop:** permanent Mat Night left rail. Items in Mat Light text; the active item has a Pine Deep fill with Sheet text, a Grid Yellow icon, and square corners; badges are small Sheet tags with Mat Night count.
- **Public booking:** no app navigation; a Mat Night header band with the business name in Sheet Title type.

### Bottom Sheets
- **Shape:** 8px top corners, Sheet fill, Sheet lift shadow, 40% scrim, drag handle.
- **Content:** title on a 2px rule, form or list, sticky action row at the bottom.

## 6. Do's and Don'ts

### Do:
- **Do** build every list as full-width rows with 1px Rule (#D2D9D7) dividers and a fixed 64px time column.
- **Do** give each green one job: Mat Night (#002F28) frames, Pine (#006251) acts, Mat Mid (#1B9579) and Mat Light (#B4DFD0) mark, Pine Wash (#E1F4ED) and Pine Mist (#F0F9F6) tint selection and the summary band.
- **Do** keep Grid Yellow (#F5D240) on the dark frame, as a thin indicator or icon.
- **Do** write every state as its glossary word in a rectangular tag with an icon.
- **Do** use bold tabular numerals for every time, amount, and capacity figure.
- **Do** separate sections with a 2px Mat Night (#002F28) rule and a sentence-case heading.
- **Do** keep body text at 18px and nothing under 16px; 48px minimum hit areas, 56px on public booking.

### Don't:
- **Don't** use the **"warm AI" look**: cream backgrounds, sage green, rounded pills everywhere, soft stacked cards, uppercase tracked micro-labels.
- **Don't** drift toward **wellness / spa apps**: pastels, candles and leaves imagery, thin "zen" typefaces.
- **Don't** fall back to **legacy management systems**: dense grey tables, Windows-style menus, endless forms.
- **Don't** desaturate the greens toward sage or olive, and never pair them with cream, beige, or warm off-white.
- **Don't** use green gradients, glows, or a dark "AI startup" theme; every green is flat ink.
- **Don't** tint the whole app background mint; that drifts into wellness / spa apps.
- **Don't** put Grid Yellow on Paper or Sheet, in a tag, or in text on a light surface.
- **Don't** use the brand green for client states (`Pendiente`, `Confirmado`, `Atendido`, `Cobrado`, `Cancelado`, `Ausente`); the only brand-green tag is the `Programado` outline for a scheduled turn.
- **Don't** wrap rows in cards or nest cards; if something looks like a card grid, turn it into rows.
- **Don't** use pill-shaped (999px) tags or buttons.
- **Don't** add shadows to rows, lists, or navigation.
- **Don't** use `border-left` or `border-right` wider than 1px as a colored accent.
- **Don't** render icon-font ligature names (`add`, `event_busy`) as text; use inline SVG icons.
- **Don't** add a literal "+" next to a plus icon.
