# Workup landing variants and selected homepage

## Objective and decisions
The user selected the Mostrador editorial composition with La ficha sage/forest palette and authorized a complete real homepage. Parent owns design and implementation. Ignore DESIGN.md. Spanish copy follows the product. Preserve prototypes and unrelated work. Do not commit without an explicit user request.

## Scope
- Existing reference: WorkUp/WorkUp/wwwroot/landing-variants.html.
- Selected homepage: WorkUp/WorkUp/Views/Home/Index.cshtml only. A self-contained document isolates page-specific typography and colors from owner/account/booking screens. Preserve gated development banner and demo information using their existing conditions and copy helpers.
- No new product behavior, authentication changes, dependencies, network fonts, stock images, invented metrics, pricing or testimonials.

## Tasks
- [ ] T1 (parked): Four prototypes written; browser verification not completed. Remain available as design reference.
- [ ] T2 (parked): Prototype structural verification previously blocked by unavailable Python. Superseded as active work by selected homepage; do not claim verified.
- [x] T3 (complete; verified, no commit requested): Implement complete selected homepage, responsive and keyboard accessible. Route: parent inline, a single source view with local styles and already-explored layout boundaries; no multi-file source changes or delegated creative decisions.
- [x] T4 (complete with limitations recorded below): Verify Razor compilation, applicable existing checks, and browser presentation if available. Route: bounded read-only verifier for executable checks, per harness requirements; parent retains design and all source editing. Background task mv038jkb-3-i5uw.

## Acceptance
Mostrador split hero and paper agenda; palette sage #e5e9e0, forest #225743, ivory #fffef9. Content: navigation, hero, labeled illustrative agenda, appointments/classes, financial records, getting started, honest FAQ, final registration CTA and footer. Existing login/register destinations. No fake controls or sample customer data presented as real. Native details FAQ, no JavaScript dependency. Mobile stacking, readable contrast, focus indication and skip link.

## Checks and safety
This is presentational Razor/HTML/CSS with native disclosure, not a new domain behavior with meaningful deterministic RED/GREEN. Compile Razor and run applicable existing checks; use rendered browser checks when tools permit. Do not claim visual verification from source inspection. Application startup migrates databases by default: do not start with migrations enabled, seed data, install dependencies, or run opt-in DB/HTTP integrations. RDD off. No commits requested.

## Existing evidence
Explorer confirmed HomeController.Index returns this view; existing account routes are /panel/Account/Login and /panel/Account/Register. _Layout carries an email-mode-aware development banner under DevToolsGate.IsEnabled. Homepage has a separately gated demo section. Parent spot-checked _Layout.cshtml. Git branch feat/workup-demo-data; source initially clean, existing untracked prototypes/task file and unrelated documents preserved.

## Implementation evidence
Parent replaced only Views/Home/Index.cshtml with a self-contained Razor document, local inline CSS, sage/forest/ivory palette, split hero and illustrative salon agenda, separate appointment/class examples, financial record illustration, three setup steps, native details FAQ and registration closing. No JS or external assets. Both DevToolsGate conditions and existing email-mode copy resolution are preserved. Navigation and CTAs use existing account paths. Browser behavior is not yet verified.

## Verification progress
Verifier mv038jkb-3-i5uw reported build passed with 0 warnings/errors and 2,506 tests passed; integration opt-in environment flags were absent. Browser verification is ongoing using installed Chromium and Node built-in WebSocket/CDP (no added packages). Authorized only OS-temp isolated browser profile, isolated supported DataProtection key directory, logs/screenshots and loopback app process with migrations and demo seeding explicitly disabled. Await exact final command/results and screenshots before acceptance. Before launching, verifier discovered unconditional ScheduledJobsHostedService immediate SQL polling. A process-local connection string to an intentionally unreachable checked-unused loopback SQL endpoint with dummy catalog/credentials and one-second timeout was additionally authorized; no actual/shared DB access permitted. Expected background SQL errors are a preview-environment limitation.

## Rendered verification progress
Verifier reported HTTP 200 at widths 1440/768/390/320, no horizontal overflow or offscreen elements, correct account hrefs, skip-link Tab+Enter reaching main. Parent inspected actual screenshots homepage-1440.png and homepage-390.png at C:/Users/mono/AppData/Local/Temp/workup-home-verify-aR7yaJ/; selected composition/palette and mobile stacking look correct. Parent git diff --check passed (only Git LF-to-CRLF normalization warnings); only tracked source modified is Index.cshtml, 235 additions/33 deletions. App/browser processes stopped and temporary keys removed after this run. Dummy-SQL background tick errors were expected from isolation.

## Final verification
Verifier mv038jkb-3-i5uw finished without concrete homepage defects. Commands: `dotnet build WorkUp/WorkUp/WorkUp.csproj --no-restore` passed with 0 warnings/errors; `dotnet test WorkUp/WorkUp.Tests/WorkUp.Tests.csproj --no-restore --verbosity quiet` reported 2,506 passed, 0 failed. Integration opt-ins were absent: N/A integration cases counted as passed are NOT evidence of executed integration coverage.

Actual Chrome/CDP runs returned HTTP 200 at 1440/768/390/320 px without overflow. Skip-link keyboard navigation worked. All four FAQ disclosures opened with Enter and closed with Space at every width once complete CDP keyboard events were supplied; the initial failure was automation-only, no source correction needed. Parent inspected desktop/mobile screenshots. Correct login/register hrefs were confirmed. All three owned app/browser process trees stopped and isolated keys removed; screenshots/logs remain only in OS temp. Source and unrelated files remained unchanged during verification.

Limitations: account destination requests and gate-closed rendering were not exercised. Opt-in DB/HTTP integration coverage was not run. Expected dummy-SQL scheduler failures and unavailable HTTPS-port warning occurred in the isolated preview. No real/shared database was used. No commits, installations, or RDD run (switch off).

## Next step
User can view the selected landing at `/` in their running application. No remaining required homepage corrections were identified. Original four-prototype verification debt stays parked, outside the accepted selected-homepage scope.
