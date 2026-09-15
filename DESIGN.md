---
name: WorkUp
description: The counter notebook of an appointment-based service business, rebuilt as a fast trade tool.
colors:
  night-grape: "#2A1752"
  grape-ink-deep: "#3A1F70"
  grape-ink: "#4B2A8C"
  lilac-mid: "#7457B5"
  lilac-light: "#D9CFEE"
  grape-wash: "#EEE9F7"
  lilac-mist: "#F6F3FB"
  counter-ink: "#1C1A22"
  counter-ink-muted: "#55505F"
  paper: "#F7F6F9"
  sheet: "#FDFDFE"
  rule: "#DCDAE1"
  control-edge: "#85808F"
  state-pendiente-text: "#7A4A00"
  state-pendiente-bg: "#FBF1DC"
  state-confirmado-text: "#1F4E79"
  state-confirmado-bg: "#E4EEF7"
  state-atendido-text: "#1E6B3A"
  state-atendido-bg: "#E3F2E7"
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
    backgroundColor: "{colors.grape-ink}"
    textColor: "{colors.sheet}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    height: "52px"
    padding: "0 24px"
  button-primary-hover:
    backgroundColor: "{colors.grape-ink-deep}"
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
    backgroundColor: "{colors.grape-wash}"
    textColor: "{colors.grape-ink}"
    typography: "{typography.label}"
---

<!-- Pre-implementation: tokens are decided and contrast-checked, but no code exists yet. Re-run $impeccable document once there is code, to capture real components and write the DESIGN.json sidecar. -->

# Design System: WorkUp

## 1. Overview

**Creative North Star: "The Trade Ledger"**

WorkUp looks like a working ledger kept by someone who is good at their job: ruled lines, hard columns, bold times, and marks that mean something. It is a tool you use standing at a counter with one hand, in good daylight, with a customer waiting. Every screen is a list you scan top to bottom; structure comes from rules and alignment, not from boxes. The ledger is printed in a family of violet inks: the darkest violet frames the app and the rules, mid violets draw the working marks (segment bars, selection), and the saturated grape ink is the owner's pen for actions.

The system is dense on purpose and calm by restraint. Time sits in a fixed left column in bold tabular numerals, the person and service read next to it, and the state sits right-aligned as a hard-edged tag. Sections are separated by a 2px ink rule and a plain sentence-case heading, like the ruled headers of an accounts book.

It explicitly rejects the anti-references in PRODUCT.md: the **"warm AI" look** (cream backgrounds, sage green, rounded pills everywhere, soft stacked cards, uppercase tracked micro-labels), **wellness / spa apps** (pastels, candles and leaves, thin "zen" type), and **legacy management systems** (dense grey tables, Windows-style menus, endless forms).

**Key Characteristics:**
- Full-width rows divided by 1px rules; cards are the exception, never the layout.
- Near-square geometry: 4px controls, 2px tags, 8px only on bottom sheets.
- A violet tonal family: Night Grape frames (app bar, section rules, time column), Grape Ink acts (buttons, links, focus), lilac tints mark (segment bars, selection, summary band).
- State is always a word from the glossary, in a rectangular tag with an icon.
- Flat at rest; the only shadow belongs to sheets that float over content.

## 2. Colors

A cool, near-white paper printed in a family of violet inks; state colors stay functional and are never replaced by violet.

### Primary (the violet family, dark to light)
- **Night Grape** (#2A1752): the frame. Mobile app bar and desktop rail background (Sheet text on it: 15.4:1), public booking header band, 2px section rules, section heading text, and row times.
- **Grape Ink Deep** (#3A1F70): pressed and hover state of Grape Ink.
- **Grape Ink** (#4B2A8C): the owner's pen. Primary buttons, row actions and links, active nav item, selected date or slot border, and the 3px focus ring. White text on it: 10.3:1.
- **Lilac Mid** (#7457B5): working marks, non-text only. `Terminación` segment, progress and occupancy fill, selected-slot inner mark. 5.5:1 against Sheet.
- **Lilac Light** (#D9CFEE): the `Espera` segment (hatched), inactive text on Night Grape (10.5:1), and dividers inside the app bar.
- **Grape Wash** (#EEE9F7): active navigation item and selected row or slot background.
- **Lilac Mist** (#F6F3FB): the day summary band and sticky action bar background. Never the whole app background.

### Neutral
- **Counter Ink** (#1C1A22): all primary text, section rules (2px), icons, and the solid `Cobrado` tag.
- **Counter Ink Muted** (#55505F): secondary lines (service, worker, masked phone). About 7:1 on Paper.
- **Paper** (#F7F6F9): app background. Cool and faintly violet; it must never drift warm or cream.
- **Sheet** (#FDFDFE): rows, inputs, sheets, and navigation bars.
- **Rule** (#DCDAE1): 1px dividers between rows. Decorative only; never the sole boundary of an interactive control.
- **Control Edge** (#85808F): input, checkbox, and secondary-control borders at rest. Meets the 3:1 non-text contrast minimum on Sheet and Paper.

### State colors (functional roles)
- **Pendiente**: amber text (#7A4A00) on amber wash (#FBF1DC), clock icon.
- **Confirmado**: blue text (#1F4E79) on blue wash (#E4EEF7), check icon.
- **Atendido / Vigente**: green text (#1E6B3A) on green wash (#E3F2E7), double-check icon.
- **Cobrado**: Sheet text on solid Counter Ink, coin icon. The strongest tag, because money in hand is a fact.
- **Ausente / Vencido / Suspendido**: red text (#9B2C2C) on red wash (#FBE7E7), cross icon.
- **Programado**: Grape Ink text with a 1px Grape Ink border, no fill, calendar icon.
- **Cancelado**: Counter Ink Muted text with line-through on the row's time, no fill.

### Named Rules
**The Violet Family Rule.** Each violet has one job: Night Grape frames, Grape Ink acts, lilacs mark. Only one solid Grape Ink button per screen; if two are visible, one of them is wrong.

**The States-Stay-Honest Rule.** Violet never replaces a client state color. `Pendiente` stays amber, `Confirmado` blue, `Atendido` green, `Ausente` red, `Cobrado` ink. Only the turn state `Programado` uses a Grape Ink outline.

**The Adjacent-Violets Rule.** Grape Ink and Lilac Mid are only 1.9:1 apart; never place them touching without a 2px gap or a Lilac Light segment between, and always label segments with text.

**The No-Cream Rule.** Backgrounds stay cool. Any neutral with a visible warm or yellow cast is prohibited.

**The Word-First Rule.** No state is ever communicated by color alone; the glossary word and an icon are mandatory.

## 3. Typography

**Body Font:** Atkinson Hyperlegible Next (with Arial, sans-serif)

**Character:** One hyperlegible family for everything. Character comes from weight and scale contrast, not from a second typeface: bold tabular numerals for time and money, regular weight for names and services.

### Hierarchy
- **Display** (700, 32px, 1.15, tabular numerals): the day's collected total and the period result. Only one per screen.
- **Headline** (700, 24px, 1.25): page title (`Jornada`, `Agenda`, `Abonos`).
- **Title** (700, 20px, 1.3): section headings in sentence case (`Ahora`, `Más tarde`, `Fijas`) and sheet titles, sitting on a 2px Counter Ink rule.
- **Body** (400, 18px, 1.5): names, services, form values, explanations. Max 65ch in explanatory text.
- **Label** (700, 16px, 1.3): buttons, tags, field labels, row times. 16px is the floor; nothing renders smaller.

### Named Rules
**The Sentence-Case Rule.** No uppercase tracked micro-labels. Section headings are sentence case in Title style.

**The Numbers-Stand-Up Rule.** Times, amounts, and capacity (`6 de 10`) always use bold tabular numerals, so columns align like a ledger.

## 4. Elevation

Flat by default. Depth comes from rules and from the contrast between Paper and Sheet, not from shadows. The only elevated surfaces are bottom sheets and the desktop profile popover, which float over content and need to separate from it.

### Shadow Vocabulary
- **Sheet lift** (`box-shadow: 0 -2px 16px rgba(28, 26, 34, 0.16)`): bottom sheets and popovers only, paired with a 40% Counter Ink scrim behind them.

### Named Rules
**The Flat-At-Rest Rule.** Rows, lists, forms, and navigation never cast shadows. If a row looks lifted, remove the shadow and add a rule.

## 5. Components

### Buttons
- **Shape:** near-square corners (4px), 52px tall; 56px on public booking.
- **Primary:** Grape Ink fill, Sheet text, Label type, one per screen, usually sticky at the bottom on mobile.
- **Hover / Focus:** fill shifts to Grape Ink Deep; focus adds a 3px Grape Ink ring with 2px offset.
- **Secondary:** Sheet fill, 2px Counter Ink border, Counter Ink text.
- **Destructive:** Sheet fill, 2px red (#9B2C2C) border and red text; never a solid red fill.
- **Row action:** text button in Grape Ink with a 20px leading icon, 48px hit area, no border.

### Status Tags
- **Style:** rectangular (2px radius), Label type in sentence case, 16px leading icon, fill and text from the state roles in Colors.
- **Placement:** right-aligned on the row's first line. `Atendido` and `Cobrado` sit side by side as independent tags.

### Agenda Row (signature component)
- **Structure:** full-width Sheet row, 1px Rule divider below. A fixed 64px left column holds the start time (Label, tabular) with the end time muted underneath; the middle holds the person or class (Body, bold name) and the service and worker (Counter Ink Muted); the right holds the status tags.
- **Time column:** start time in Night Grape, bold tabular.
- **Individual appointments:** a 6px segmented bar under the text with 2px gaps: Aplicación (Grape Ink), Espera (Lilac Light, hatched), Terminación (Lilac Mid), each labeled below in muted text.
- **Classes:** occupancy `6 de 10` in bold tabular numerals plus a 6px occupancy bar (Lilac Mid fill on Grape Wash track).
- **Actions:** one to three row actions under the text; overflow goes to a `Más acciones` sheet.

### Section Header
- **Style:** Title type in Night Grape, sentence case, on a 2px Night Grape rule, with an optional right-aligned muted count (`3 turnos`). No background, no card.

### Summary Band
- **Style:** full-width Lilac Mist band under the app bar, no border radius. Label in Counter Ink Muted, the figure in Display type Night Grape, follow-up links in Grape Ink.

### Inputs / Fields
- **Style:** Sheet fill, 1px Control Edge border, 4px radius, 52px tall, persistent Label above; placeholder text is an example only.
- **Focus:** border becomes 2px Grape Ink plus the 3px focus ring.
- **Error:** 2px red border, red helper text with a cross icon under the field; entered value is kept.

### Navigation
- **Mobile app bar:** Night Grape background, Sheet title and icons, square corners, no shadow.
- **Mobile bottom bar:** Sheet background with a 1px Rule on top. Four items with icon and Label text; the active item shows Grape Ink text, a filled icon, and a 3px Grape Ink bar on its top edge.
- **Desktop:** permanent Night Grape left rail. Items in Lilac Light text; the active item has a Grape Wash fill with Night Grape text and square corners; badges are small Sheet tags with Night Grape count.
- **Public booking:** no app navigation; a Night Grape header band with the business name in Sheet Title type.

### Bottom Sheets
- **Shape:** 8px top corners, Sheet fill, Sheet lift shadow, 40% scrim, drag handle.
- **Content:** title on a 2px rule, form or list, sticky action row at the bottom.

## 6. Do's and Don'ts

### Do:
- **Do** build every list as full-width rows with 1px Rule (#DCDAE1) dividers and a fixed 64px time column.
- **Do** give each violet one job: Night Grape (#2A1752) frames, Grape Ink (#4B2A8C) acts, Lilac Mid (#7457B5) and Lilac Light (#D9CFEE) mark, Grape Wash (#EEE9F7) and Lilac Mist (#F6F3FB) tint selection and the summary band.
- **Do** write every state as its glossary word in a rectangular tag with an icon.
- **Do** use bold tabular numerals for every time, amount, and capacity figure.
- **Do** separate sections with a 2px Night Grape (#2A1752) rule and a sentence-case heading.
- **Do** keep body text at 18px and nothing under 16px; 48px minimum hit areas, 56px on public booking.

### Don't:
- **Don't** use the **"warm AI" look**: cream backgrounds, sage green, rounded pills everywhere, soft stacked cards, uppercase tracked micro-labels.
- **Don't** drift toward **wellness / spa apps**: pastels, candles and leaves imagery, thin "zen" typefaces.
- **Don't** fall back to **legacy management systems**: dense grey tables, Windows-style menus, endless forms.
- **Don't** use violet gradients, glows, or a dark "AI startup" theme; every violet is flat ink.
- **Don't** tint the whole app background lilac or lavender; that drifts into wellness / spa apps.
- **Don't** use violet for client states (`Pendiente`, `Confirmado`, `Atendido`, `Cobrado`, `Cancelado`, `Ausente`); the only violet tag is the `Programado` outline for a scheduled turn.
- **Don't** wrap rows in cards or nest cards; if something looks like a card grid, turn it into rows.
- **Don't** use pill-shaped (999px) tags or buttons.
- **Don't** add shadows to rows, lists, or navigation.
- **Don't** use `border-left` or `border-right` wider than 1px as a colored accent.
- **Don't** render icon-font ligature names (`add`, `event_busy`) as text; use inline SVG icons.
- **Don't** add a literal "+" next to a plus icon.
