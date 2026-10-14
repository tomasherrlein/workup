/* ============================================================================
   WorkUp - LocalDemoEnrichment.sql
   ----------------------------------------------------------------------------
   Operational demo enrichment for the two fictional T07 businesses only.
   This file is prepared for a later, separate execution step; it does not run
   by itself and it never touches application code, migrations or the T07 seed.

   Contract (see odd/tasks/workup-demo-data.md, D01):
     * Dedicated LocalDB catalog only: the server must be LocalDB and the catalog
       a WorkUpDemo* / WorkUpT07Demo* name. Both checks run inside the outer TRY
       and fail closed, before any write.
     * Insert-only. No UPDATE, no DELETE, no MERGE, no DROP, no TRUNCATE.
     * One explicit SERIALIZABLE transaction with XACT_ABORT: either the whole
       batch commits or nothing is written.
     * Idempotent by a reserved marker: the `demo.client.m%@demo.invalid` and
       `demo.client.y%@demo.invalid` client emails. A second run inserts zero
       rows and never rewrites a date or a value.
     * A partial marker set (1..5 clients for a business) is a hard error, not a
       silent top-up and not a duplicate insert.
     * Every tenant column is bound to the business resolved from its seeded
       slug. Ids are looked up by natural key, never assumed, and only active
       workers / services / grid classes are accepted.
     * Yoga class sessions are generated only for the four seeded grid classes
       and only in the previous, current and next week. Fixed bookings are
       attached only to the enrollments this batch inserts. No booking is ever
       materialized for a pre-existing user enrollment.
     * Capacity is checked, never bypassed: every proposed booking plus the live
       bookings already on the session must fit Appointments.MaxCapacity, and the
       permanent seats of each grid class (pre-existing plus new) must fit the
       service capacity. An incompatible collision aborts the whole batch.
     * Synthetic instants are backdated so that an appointment precedes its
       booking, a booking precedes its attendance or cancellation, and a
       membership activation is not later than its coverage start.
     * Tokens, invitations and private appointment links are intentionally
       excluded: they would be synthetic and unusable. Pending notices are
       intentionally excluded too, so the demo host never dispatches them.

   Calendar: Argentina-local dates computed from the Windows time zone
   `Argentina Standard Time` (Buenos Aires, fixed UTC-3), not from the server
   clock, so a machine in another time zone still seeds the same local week.

   Run (separate execution step, fresh seeded catalog), exact runner:
     sqlcmd -I -b -S "(localdb)\MSSQLLocalDB" -E -f 65001 ^
            -d <fresh seeded DB> -i WorkUp/scripts/LocalDemoEnrichment.sql
   The input file is UTF-8, so -f 65001 keeps accented demo text intact.

   Output: only non-sensitive per-business counts, the applied state and the
   demo client account emails. No password and no password hash is read out.

   Source-only limitation: this preparation was not executed. Guards, assertion
   semantics, CK/UQ/FK shapes and idempotency were verified against the schema,
   enums, rules and hasher source. Real SQL proof is the next, independent step.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

/* ---------------------------------------------------------------------------
   1. Argentina-local calendar. SysDayOfWeek: 0 = Sunday .. 6 = Saturday.
      MondayIndex: 0 = Monday .. 6 = Sunday. Pure computation, no I/O.
   --------------------------------------------------------------------------- */
DECLARE @localNow DATETIME2(0) =
    CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'Argentina Standard Time' AS DATETIME2(0));
DECLARE @today DATE = CAST(@localNow AS DATE);
DECLARE @mondayIndex INT = DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @today) % 7;  -- 0 = Monday
DECLARE @weekMonday DATE = DATEADD(DAY, -@mondayIndex, @today);                        -- Monday of the current week

DECLARE @weekMondayDT DATETIME2(0) = CAST(@weekMonday AS DATETIME2(0));
DECLARE @prevMon DATETIME2(0) = DATEADD(DAY, -7, @weekMondayDT);
DECLARE @prevTue DATETIME2(0) = DATEADD(DAY, 1, @prevMon);
DECLARE @prevWed DATETIME2(0) = DATEADD(DAY, 2, @prevMon);
DECLARE @prevThu DATETIME2(0) = DATEADD(DAY, 3, @prevMon);
DECLARE @prevFri DATETIME2(0) = DATEADD(DAY, 4, @prevMon);

-- Nearest weekday (Mon-Fri) strictly after today, and the one after that. Both are always real future weekdays.
DECLARE @dCurrent DATE = DATEADD(DAY, CASE WHEN @mondayIndex >= 4 THEN 7 - @mondayIndex ELSE 1 END, @today);
DECLARE @currentIndex INT = DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @dCurrent) % 7;
DECLARE @dNext DATE = DATEADD(DAY, CASE WHEN @currentIndex >= 4 THEN 7 - @currentIndex ELSE 1 END, @dCurrent);
DECLARE @currentDayDT DATETIME2(0) = CAST(@dCurrent AS DATETIME2(0));
DECLARE @nextDayDT DATETIME2(0) = CAST(@dNext AS DATETIME2(0));

-- Month anchors for expenses. SpentOn is clamped to today so "this month" never runs into the future.
DECLARE @thisMonthDay1 DATE = DATEFROMPARTS(YEAR(@today), MONTH(@today), 1);
DECLARE @prevMonthDay1 DATE = DATEADD(MONTH, -1, @thisMonthDay1);

/* ---------------------------------------------------------------------------
   2. Declarations. Every guard below is evaluated inside the outer TRY, before
      the first write, and raises with THROW so it always terminates the batch.
   --------------------------------------------------------------------------- */
DECLARE @isLocalDb INT = CONVERT(INT, SERVERPROPERTY('IsLocalDB'));

DECLARE @marinaId INT, @yogaId INT;
DECLARE @marinaHash NVARCHAR(400), @yogaHash NVARCHAR(400);
DECLARE @ana INT, @clara INT, @martina INT, @valeria INT;
DECLARE @corte INT, @color INT, @brushing INT, @hatha INT, @vinyasa INT;
DECLARE @gMon INT, @gTue INT, @gWed INT, @gThu INT;
DECLARE @marinaMarkerCount INT, @yogaMarkerCount INT;
DECLARE @marinaInserted BIT = 0, @yogaInserted BIT = 0;

/* Client ids reused across the marina block. */
DECLARE @m1 INT, @m2 INT, @m3 INT, @m4 INT, @m5 INT, @m6 INT;
/* Client ids reused across the yoga block. */
DECLARE @y1 INT, @y2 INT, @y3 INT, @y4 INT, @y5 INT, @y6 INT;

BEGIN TRY

    /* -----------------------------------------------------------------------
       3. Fail-closed target guards.
       ----------------------------------------------------------------------- */
    IF @isLocalDb IS NULL OR @isLocalDb <> 1
    BEGIN
        ;THROW 50000, N'LocalDemoEnrichment requires a LocalDB server; no LocalDB instance was detected.', 1;
    END

    IF DB_NAME() NOT LIKE 'WorkUpDemo%' AND DB_NAME() NOT LIKE 'WorkUpT07Demo%'
    BEGIN
        ;THROW 50000, N'LocalDemoEnrichment refuses to run outside a dedicated WorkUpDemo* or WorkUpT07Demo* LocalDB catalog.', 1;
    END

    /* -----------------------------------------------------------------------
       4. Resolve the two seeded businesses and their owners by slug / username.
          Ids and the owner hash are looked up, never assumed.
       ----------------------------------------------------------------------- */
    SET @marinaId = (SELECT Id FROM Businesses WHERE Slug = 'peluqueria-marina-demo');
    SET @yogaId = (SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo');
    IF @marinaId IS NULL OR @yogaId IS NULL
    BEGIN
        ;THROW 50000, N'The two fictional T07 businesses were not found; run the demo seed first.', 1;
    END

    SET @marinaHash = (SELECT oa.PasswordHash FROM OwnerAccounts oa
                        WHERE oa.BusinessId = @marinaId AND oa.Username = N'marina.demo');
    SET @yogaHash = (SELECT oa.PasswordHash FROM OwnerAccounts oa
                      WHERE oa.BusinessId = @yogaId AND oa.Username = N'lucia.demo');
    IF @marinaHash IS NULL OR @yogaHash IS NULL
    BEGIN
        ;THROW 50000, N'The seeded demo owners were not found; run the demo seed first.', 1;
    END

    -- Verify the maintained ASP.NET Core Identity PBKDF2 shape without reading the secret out.
    -- Format marker 0x01 (V3) base64-encodes to the prefix AQAAAA.
    IF @marinaHash NOT LIKE 'AQAAAA%' OR @yogaHash NOT LIKE 'AQAAAA%'
    BEGIN
        ;THROW 50000, N'Unexpected owner PasswordHasher format; refusing to copy it into a demo client account.', 1;
    END

    /* -----------------------------------------------------------------------
       5. Resolve active staff, services and yoga grid classes. IsActive = 1
          makes the batch fail closed if the user baseline was modified.
       ----------------------------------------------------------------------- */
    SET @ana = (SELECT Id FROM Workers WHERE BusinessId = @marinaId AND IsActive = 1 AND Cuil = '00000000001');
    SET @clara = (SELECT Id FROM Workers WHERE BusinessId = @marinaId AND IsActive = 1 AND Cuil = '00000000002');
    SET @martina = (SELECT Id FROM Workers WHERE BusinessId = @yogaId AND IsActive = 1 AND Cuil = '00000000003');
    SET @valeria = (SELECT Id FROM Workers WHERE BusinessId = @yogaId AND IsActive = 1 AND Cuil = '00000000004');
    IF @ana IS NULL OR @clara IS NULL OR @martina IS NULL OR @valeria IS NULL
    BEGIN
        ;THROW 50000, N'Active seeded demo workers were not found.', 1;
    END

    SET @corte = (SELECT Id FROM Services WHERE BusinessId = @marinaId AND IsActive = 1 AND Name = N'Corte');
    -- Name LIKE 'Coloraci_n' keeps the lookup valid even if the client code page misreads the accent.
    SET @color = (SELECT Id FROM Services WHERE BusinessId = @marinaId AND IsActive = 1 AND Name LIKE N'Coloraci_n');
    SET @brushing = (SELECT Id FROM Services WHERE BusinessId = @marinaId AND IsActive = 1 AND Name = N'Brushing');
    SET @hatha = (SELECT Id FROM Services WHERE BusinessId = @yogaId AND IsActive = 1 AND Name = N'Hatha yoga');
    SET @vinyasa = (SELECT Id FROM Services WHERE BusinessId = @yogaId AND IsActive = 1 AND Name = N'Vinyasa yoga');
    IF @corte IS NULL OR @color IS NULL OR @brushing IS NULL OR @hatha IS NULL OR @vinyasa IS NULL
    BEGIN
        ;THROW 50000, N'Active seeded demo services were not found.', 1;
    END

    -- Yoga grid: Mon hatha/Martina, Tue hatha/Valeria, Wed vinyasa/Martina, Thu vinyasa/Valeria (all 18:00).
    SET @gMon = (SELECT Id FROM GridClasses WHERE BusinessId = @yogaId AND IsActive = 1 AND ServiceId = @hatha   AND WorkerId = @martina AND DayOfWeek = 1 AND StartTime = '18:00');
    SET @gTue = (SELECT Id FROM GridClasses WHERE BusinessId = @yogaId AND IsActive = 1 AND ServiceId = @hatha   AND WorkerId = @valeria AND DayOfWeek = 2 AND StartTime = '18:00');
    SET @gWed = (SELECT Id FROM GridClasses WHERE BusinessId = @yogaId AND IsActive = 1 AND ServiceId = @vinyasa AND WorkerId = @martina AND DayOfWeek = 3 AND StartTime = '18:00');
    SET @gThu = (SELECT Id FROM GridClasses WHERE BusinessId = @yogaId AND IsActive = 1 AND ServiceId = @vinyasa AND WorkerId = @valeria AND DayOfWeek = 4 AND StartTime = '18:00');
    IF @gMon IS NULL OR @gTue IS NULL OR @gWed IS NULL OR @gThu IS NULL
    BEGIN
        ;THROW 50000, N'The active seeded yoga grid classes were not found.', 1;
    END

    /* -----------------------------------------------------------------------
       6. One serializable transaction. Marker detection runs inside it, under
          held range locks, so two concurrent runs cannot both see "new".
       ----------------------------------------------------------------------- */
    BEGIN TRANSACTION;

    SET @marinaMarkerCount = (SELECT COUNT(*) FROM Clients WITH (UPDLOCK, HOLDLOCK)
                               WHERE BusinessId = @marinaId AND Email LIKE 'demo.client.m%@demo.invalid');
    SET @yogaMarkerCount = (SELECT COUNT(*) FROM Clients WITH (UPDLOCK, HOLDLOCK)
                             WHERE BusinessId = @yogaId AND Email LIKE 'demo.client.y%@demo.invalid');

    IF @marinaMarkerCount NOT IN (0, 6) OR @yogaMarkerCount NOT IN (0, 6)
    BEGIN
        ;THROW 50000, N'A partial demo enrichment was detected (a marker business has between 1 and 5 demo clients); resolve it manually instead of re-running.', 1;
    END

    /* =======================================================================
       MARINA - individual appointments, expenses and supplies.
       ======================================================================= */
    IF @marinaMarkerCount = 6
    BEGIN
        -- Already applied: assert the exact expected accounts and reservations
        -- instead of inserting, and never silently accept a partial dataset.
        IF (SELECT COUNT(*) FROM ClientAccounts ca
             WHERE ca.BusinessId = @marinaId
               AND ca.Email IN (N'demo.client.m1@demo.invalid', N'demo.client.m4@demo.invalid')
               AND ca.ClientId IS NOT NULL AND ca.EmailVerifiedAt IS NOT NULL) <> 2
        BEGIN
            ;THROW 50000, N'Existing marina enrichment is incomplete: the two expected demo accounts are not linked and verified.', 1;
        END

        IF EXISTS (
            SELECT 1 FROM ClientAccounts ca
             WHERE ca.BusinessId = @marinaId
               AND ca.Email IN (N'demo.client.m1@demo.invalid', N'demo.client.m4@demo.invalid')
               AND NOT EXISTS (SELECT 1 FROM Bookings b
                                WHERE b.BusinessId = @marinaId AND b.ClientId = ca.ClientId))
        BEGIN
            ;THROW 50000, N'Existing marina enrichment is incomplete: a demo account has no own booking.', 1;
        END

        IF (SELECT COUNT(*) FROM Expenses x WHERE x.BusinessId = @marinaId) = 0
            OR (SELECT COUNT(*) FROM Supplies s WHERE s.BusinessId = @marinaId) = 0
        BEGIN
            ;THROW 50000, N'Existing marina enrichment is incomplete: expenses or supplies are missing.', 1;
        END
    END
    ELSE
    BEGIN
        SET @marinaInserted = 1;

        /* 6.1 Six synthetic clients (lower-case .invalid emails). */
        INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
        VALUES
            (@marinaId, N'Sofía',   N'Demo', N'demo.client.m1@demo.invalid', '0000001001', NULL, @localNow),
            (@marinaId, N'Camila',  N'Demo', N'demo.client.m2@demo.invalid', '0000001002', NULL, @localNow),
            (@marinaId, N'Valentina', N'Demo', N'demo.client.m3@demo.invalid', '0000001003', NULL, @localNow),
            (@marinaId, N'Julieta', N'Demo', N'demo.client.m4@demo.invalid', '0000001004', NULL, @localNow),
            (@marinaId, N'Agustina', N'Demo', N'demo.client.m5@demo.invalid', '0000001005', NULL, @localNow),
            (@marinaId, N'Micaela', N'Demo', N'demo.client.m6@demo.invalid', '0000001006', NULL, @localNow);

        SET @m1 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m1@demo.invalid');
        SET @m2 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m2@demo.invalid');
        SET @m3 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m3@demo.invalid');
        SET @m4 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m4@demo.invalid');
        SET @m5 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m5@demo.invalid');
        SET @m6 = (SELECT Id FROM Clients WHERE BusinessId = @marinaId AND Email = N'demo.client.m6@demo.invalid');

        /* 6.2 Client login accounts, linked by ClientId and verified. The owner
               hash is copied verbatim so the demo client shares the known demo
               password without the password or its hash ever being printed. */
        INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt, PendingFirstName, PendingLastName)
        VALUES
            (@marinaId, @m1, N'demo.client.m1@demo.invalid', @marinaHash, @localNow, @localNow, NULL, NULL),
            (@marinaId, @m4, N'demo.client.m4@demo.invalid', @marinaHash, @localNow, @localNow, NULL, NULL);

        /* 6.3 Expense types (reuse if present with the same active shape, reject an incompatible collision). */
        DECLARE @marinaExpenseTypes TABLE (Name NVARCHAR(80));
        INSERT INTO @marinaExpenseTypes (Name) VALUES
            (N'Productos de salón (demo)'), (N'Alquiler (demo)'), (N'Servicios e impuestos (demo)');

        INSERT INTO ExpenseTypes (BusinessId, Name, IsActive)
        SELECT @marinaId, t.Name, 1
          FROM @marinaExpenseTypes t
         WHERE NOT EXISTS (SELECT 1 FROM ExpenseTypes e WHERE e.BusinessId = @marinaId AND e.Name = t.Name);

        IF EXISTS (SELECT 1 FROM @marinaExpenseTypes t
                    JOIN ExpenseTypes e ON e.BusinessId = @marinaId AND e.Name = t.Name AND e.IsActive = 0)
        BEGIN
            ;THROW 50000, N'An incompatible marina expense type collision was found.', 1;
        END

        /* 6.4 Expenses this month (clamped to today) and the previous month. */
        INSERT INTO Expenses (BusinessId, ExpenseTypeId, SpentOn, Amount, Description, CreatedAt)
        SELECT @marinaId, et.Id, t.SpentOn, t.Amount, t.Description, t.SpentOn
          FROM (VALUES
                    (N'Productos de salón (demo)',    CASE WHEN DATEADD(DAY, 2, @thisMonthDay1) > @today THEN @today ELSE DATEADD(DAY, 2, @thisMonthDay1) END, 45000.00,  N'Reposición de tinturas'),
                    (N'Servicios e impuestos (demo)', CASE WHEN DATEADD(DAY, 5, @thisMonthDay1) > @today THEN @today ELSE DATEADD(DAY, 5, @thisMonthDay1) END, 12000.00,  N'Internet y servicios'),
                    (N'Alquiler (demo)',              DATEADD(DAY, 4, @prevMonthDay1), 120000.00, N'Alquiler del salón'),
                    (N'Productos de salón (demo)',    DATEADD(DAY, 16, @prevMonthDay1), 32000.00, N'Reposición de shampoos')
               ) AS t(TypeName, SpentOn, Amount, Description)
          JOIN ExpenseTypes et ON et.BusinessId = @marinaId AND et.Name = t.TypeName;

        /* 6.5 Supplies, some below the minimum. */
        DECLARE @marinaSupplies TABLE (Name NVARCHAR(100), CurrentStock DECIMAL(10,2), MinimumStock DECIMAL(10,2));
        INSERT INTO @marinaSupplies (Name, CurrentStock, MinimumStock) VALUES
            (N'Shampoo profesional (demo)', 3.00,  8.00),
            (N'Tinte color (demo)',         20.00, 5.00),
            (N'Capas descartables (demo)',  40.00, 30.00);

        IF EXISTS (SELECT 1 FROM @marinaSupplies t
                    JOIN Supplies s ON s.BusinessId = @marinaId AND s.Name = t.Name)
        BEGIN
            ;THROW 50000, N'An incompatible marina supply name collision was found.', 1;
        END

        INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
        SELECT @marinaId, t.Name, t.CurrentStock, t.MinimumStock, @localNow, 1 FROM @marinaSupplies t;

        /* 6.6 Individual appointments: past attended/absent/cancelled plus
               current and next weekday pending/confirmed, including one future
               eligible appointment for each demo account (m1 and m4). Every slot
               is validated against the worker schedule, the service duration,
               the quarter-hour grid and against overlapping appointments. */
        DECLARE @marinaSlots TABLE (
            SlotName      VARCHAR(8)   NOT NULL PRIMARY KEY,
            ClientId      INT          NOT NULL,
            ServiceId     INT          NOT NULL,
            WorkerId      INT          NOT NULL,
            StartsAt      DATETIME2(0) NOT NULL,
            EndsAt        DATETIME2(0) NOT NULL,
            Origin        TINYINT      NOT NULL,
            BookingStatus TINYINT      NOT NULL,
            Paid          BIT          NOT NULL);

        INSERT INTO @marinaSlots (SlotName, ClientId, ServiceId, WorkerId, StartsAt, EndsAt, Origin, BookingStatus, Paid)
        VALUES
            ('m1a', @m1, @corte,    @ana,   DATEADD(MINUTE, 600, @prevMon),      DATEADD(MINUTE, 645, @prevMon),      1, 3, 1),
            ('m2a', @m2, @brushing, @clara, DATEADD(MINUTE, 660, @prevMon),      DATEADD(MINUTE, 705, @prevMon),      1, 3, 1),
            ('m3a', @m3, @corte,    @ana,   DATEADD(MINUTE, 540, @prevTue),      DATEADD(MINUTE, 585, @prevTue),      1, 4, 0),
            ('m4a', @m4, @color,    @ana,   DATEADD(MINUTE, 840, @prevTue),      DATEADD(MINUTE, 960, @prevTue),      1, 3, 1),
            ('m5a', @m5, @corte,    @clara, DATEADD(MINUTE, 600, @prevWed),      DATEADD(MINUTE, 645, @prevWed),      1, 5, 0),
            ('m4b', @m4, @corte,    @clara, DATEADD(MINUTE, 960, @prevWed),      DATEADD(MINUTE, 1005, @prevWed),     2, 3, 1),
            ('m6a', @m6, @brushing, @ana,   DATEADD(MINUTE, 900, @prevThu),      DATEADD(MINUTE, 945, @prevThu),      1, 3, 1),
            ('m1b', @m1, @corte,    @ana,   DATEADD(MINUTE, 600, @prevFri),      DATEADD(MINUTE, 645, @prevFri),      1, 4, 0),
            ('m2b', @m2, @corte,    @ana,   DATEADD(MINUTE, 600, @currentDayDT), DATEADD(MINUTE, 645, @currentDayDT), 1, 1, 0),
            ('m3b', @m3, @brushing, @clara, DATEADD(MINUTE, 840, @currentDayDT), DATEADD(MINUTE, 885, @currentDayDT), 1, 2, 1),
            ('m1c', @m1, @corte,    @ana,   DATEADD(MINUTE, 720, @currentDayDT), DATEADD(MINUTE, 765, @currentDayDT), 1, 2, 0),
            ('m5b', @m5, @corte,    @ana,   DATEADD(MINUTE, 660, @nextDayDT),    DATEADD(MINUTE, 705, @nextDayDT),    1, 1, 0),
            ('m6b', @m6, @corte,    @clara, DATEADD(MINUTE, 900, @nextDayDT),    DATEADD(MINUTE, 945, @nextDayDT),    1, 2, 0),
            ('m4c', @m4, @corte,    @clara, DATEADD(MINUTE, 960, @nextDayDT),    DATEADD(MINUTE, 1005, @nextDayDT),   1, 1, 0);

        -- a) quarter-hour boundaries
        IF EXISTS (SELECT 1 FROM @marinaSlots s
                    WHERE DATEDIFF(MINUTE, CAST(s.StartsAt AS DATE), s.StartsAt) % 15 <> 0)
        BEGIN
            ;THROW 50000, N'A marina demo slot is not on a quarter-hour boundary.', 1;
        END

        -- b) slot fully inside a working range of the target worker, on the matching weekday
        IF EXISTS (
            SELECT 1 FROM @marinaSlots s
             WHERE NOT EXISTS (
                SELECT 1 FROM WorkerSchedules ws
                 WHERE ws.WorkerId = s.WorkerId
                   AND ws.DayOfWeek = ((DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), CAST(s.StartsAt AS DATE)) % 7) + 1) % 7
                   AND ws.StartTime <= CAST(s.StartsAt AS TIME(0))
                   AND ws.EndTime   >= CAST(s.EndsAt   AS TIME(0))))
        BEGIN
            ;THROW 50000, N'A marina demo slot falls outside its worker schedule.', 1;
        END

        -- c) occupied range equals the service duration
        IF EXISTS (
            SELECT 1 FROM @marinaSlots s
             JOIN Services svc ON svc.Id = s.ServiceId
             WHERE svc.BusinessId <> @marinaId
                OR DATEDIFF(MINUTE, s.StartsAt, s.EndsAt) <> svc.DurationMinutes)
        BEGIN
            ;THROW 50000, N'A marina demo slot duration does not match its service.', 1;
        END

        -- d) no two of our own slots overlap on the same worker
        IF EXISTS (
            SELECT 1 FROM @marinaSlots a
             JOIN @marinaSlots b ON b.WorkerId = a.WorkerId AND b.SlotName > a.SlotName
             WHERE a.StartsAt < b.EndsAt AND b.StartsAt < a.EndsAt)
        BEGIN
            ;THROW 50000, N'Two marina demo slots overlap on the same worker.', 1;
        END

        -- e) no collision with a pre-existing scheduled individual appointment
        IF EXISTS (
            SELECT 1 FROM @marinaSlots s
             JOIN Appointments a ON a.BusinessId = @marinaId AND a.WorkerId = s.WorkerId AND a.Kind = 1 AND a.Status = 1
             WHERE s.StartsAt < a.EndsAt AND a.StartsAt < s.EndsAt)
        BEGIN
            ;THROW 50000, N'A marina demo slot overlaps a pre-existing appointment; no compatible place is available.', 1;
        END

        -- Appointment CreatedAt is backdated for past slots so it precedes its booking.
        INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
        SELECT @marinaId, 1, s.ServiceId, s.WorkerId, NULL, s.StartsAt, s.EndsAt, 1, s.Origin, 1,
               CASE WHEN s.StartsAt > @localNow THEN @localNow ELSE DATEADD(DAY, -10, s.StartsAt) END
          FROM @marinaSlots s;

        /* 6.7 One booking per slot, with CreatedAt before start and before any cancellation. */
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy,
                              AgreedPrice, PriceReason, CreatedAt, ConfirmedAt, CancelledAt, AttendanceRecordedAt)
        SELECT @marinaId, a.Id, s.ClientId, NULL, NULL, s.BookingStatus,
               CASE WHEN s.BookingStatus = 4 THEN 1 ELSE NULL END,
               CASE WHEN s.BookingStatus = 5 THEN 1 ELSE NULL END,
               NULL, NULL,
               CASE WHEN s.StartsAt > @localNow THEN @localNow ELSE DATEADD(DAY, -3, s.StartsAt) END,
               CASE WHEN s.BookingStatus = 2 THEN @localNow ELSE NULL END,
               CASE WHEN s.BookingStatus = 5 THEN DATEADD(MINUTE, -30, s.StartsAt) ELSE NULL END,
               CASE WHEN s.BookingStatus IN (3, 4) THEN s.EndsAt ELSE NULL END
          FROM @marinaSlots s
          JOIN Appointments a
            ON a.BusinessId = @marinaId AND a.Kind = 1 AND a.WorkerId = s.WorkerId AND a.StartsAt = s.StartsAt;

        /* 6.8 Payments for the paid slots: individual (1) or walk-in (2). */
        INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
        SELECT @marinaId, s.ClientId, CASE WHEN s.Origin = 2 THEN 2 ELSE 1 END, b.Id, NULL,
               svc.Price, NULL, NULL,
               CASE WHEN s.StartsAt > @localNow THEN @localNow ELSE s.StartsAt END,
               @localNow
          FROM @marinaSlots s
          JOIN Appointments a ON a.BusinessId = @marinaId AND a.Kind = 1 AND a.WorkerId = s.WorkerId AND a.StartsAt = s.StartsAt
          JOIN Bookings b ON b.AppointmentId = a.Id AND b.ClientId = s.ClientId
          JOIN Services svc ON svc.Id = s.ServiceId
         WHERE s.Paid = 1;
    END

    /* =======================================================================
       YOGA - grid-aligned class sessions, enrollments, periods, drop-ins,
       memberships, expenses and supplies.
       ======================================================================= */
    IF @yogaMarkerCount = 6
    BEGIN
        IF (SELECT COUNT(*) FROM ClientAccounts ca
             WHERE ca.BusinessId = @yogaId
               AND ca.Email IN (N'demo.client.y1@demo.invalid', N'demo.client.y3@demo.invalid')
               AND ca.ClientId IS NOT NULL AND ca.EmailVerifiedAt IS NOT NULL) <> 2
        BEGIN
            ;THROW 50000, N'Existing yoga enrichment is incomplete: the two expected demo accounts are not linked and verified.', 1;
        END

        IF EXISTS (
            SELECT 1 FROM ClientAccounts ca
             WHERE ca.BusinessId = @yogaId
               AND ca.Email IN (N'demo.client.y1@demo.invalid', N'demo.client.y3@demo.invalid')
               AND NOT EXISTS (SELECT 1 FROM Bookings b
                                WHERE b.BusinessId = @yogaId AND b.ClientId = ca.ClientId))
        BEGIN
            ;THROW 50000, N'Existing yoga enrichment is incomplete: a demo account has no own booking.', 1;
        END

        IF (SELECT COUNT(*) FROM Enrollments e WHERE e.BusinessId = @yogaId) = 0
            OR (SELECT COUNT(*) FROM MembershipPeriods mp JOIN Enrollments e ON e.Id = mp.EnrollmentId WHERE e.BusinessId = @yogaId) = 0
            OR (SELECT COUNT(*) FROM Expenses x WHERE x.BusinessId = @yogaId) = 0
            OR (SELECT COUNT(*) FROM Supplies s WHERE s.BusinessId = @yogaId) = 0
        BEGIN
            ;THROW 50000, N'Existing yoga enrichment is incomplete: enrollments, periods, expenses or supplies are missing.', 1;
        END
    END
    ELSE
    BEGIN
        SET @yogaInserted = 1;

        /* 6.9 Six synthetic clients. */
        INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
        VALUES
            (@yogaId, N'Lucía',   N'Demo', N'demo.client.y1@demo.invalid', '0000002001', NULL, @localNow),
            (@yogaId, N'Martina', N'Demo', N'demo.client.y2@demo.invalid', '0000002002', NULL, @localNow),
            (@yogaId, N'Renata',  N'Demo', N'demo.client.y3@demo.invalid', '0000002003', NULL, @localNow),
            (@yogaId, N'Paula',   N'Demo', N'demo.client.y4@demo.invalid', '0000002004', NULL, @localNow),
            (@yogaId, N'Florencia', N'Demo', N'demo.client.y5@demo.invalid', '0000002005', NULL, @localNow),
            (@yogaId, N'Delfina', N'Demo', N'demo.client.y6@demo.invalid', '0000002006', NULL, @localNow);

        SET @y1 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y1@demo.invalid');
        SET @y2 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y2@demo.invalid');
        SET @y3 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y3@demo.invalid');
        SET @y4 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y4@demo.invalid');
        SET @y5 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y5@demo.invalid');
        SET @y6 = (SELECT Id FROM Clients WHERE BusinessId = @yogaId AND Email = N'demo.client.y6@demo.invalid');

        /* 6.10 Client login accounts linked by ClientId (owner hash copied verbatim). */
        INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt, PendingFirstName, PendingLastName)
        VALUES
            (@yogaId, @y1, N'demo.client.y1@demo.invalid', @yogaHash, @localNow, @localNow, NULL, NULL),
            (@yogaId, @y3, N'demo.client.y3@demo.invalid', @yogaHash, @localNow, @localNow, NULL, NULL);

        /* 6.11 Membership prices (unique per business and weekly frequency). */
        IF NOT EXISTS (SELECT 1 FROM MembershipPrices WHERE BusinessId = @yogaId AND WeeklyFrequency = 2)
            INSERT INTO MembershipPrices (BusinessId, WeeklyFrequency, Price) VALUES (@yogaId, 2, 16000.00);
        ELSE IF EXISTS (SELECT 1 FROM MembershipPrices WHERE BusinessId = @yogaId AND WeeklyFrequency = 2 AND Price <> 16000.00)
        BEGIN
            ;THROW 50000, N'An incompatible yoga membership price collision was found (frequency 2).', 1;
        END

        IF NOT EXISTS (SELECT 1 FROM MembershipPrices WHERE BusinessId = @yogaId AND WeeklyFrequency = 3)
            INSERT INTO MembershipPrices (BusinessId, WeeklyFrequency, Price) VALUES (@yogaId, 3, 22000.00);
        ELSE IF EXISTS (SELECT 1 FROM MembershipPrices WHERE BusinessId = @yogaId AND WeeklyFrequency = 3 AND Price <> 22000.00)
        BEGIN
            ;THROW 50000, N'An incompatible yoga membership price collision was found (frequency 3).', 1;
        END

        /* 6.12 Expense types, expenses (this month clamped to today) and supplies. */
        DECLARE @yogaExpenseTypes TABLE (Name NVARCHAR(80));
        INSERT INTO @yogaExpenseTypes (Name) VALUES
            (N'Alquiler de salón (demo)'), (N'Mantenimiento (demo)'), (N'Insumos (demo)');

        INSERT INTO ExpenseTypes (BusinessId, Name, IsActive)
        SELECT @yogaId, t.Name, 1
          FROM @yogaExpenseTypes t
         WHERE NOT EXISTS (SELECT 1 FROM ExpenseTypes e WHERE e.BusinessId = @yogaId AND e.Name = t.Name);

        IF EXISTS (SELECT 1 FROM @yogaExpenseTypes t
                    JOIN ExpenseTypes e ON e.BusinessId = @yogaId AND e.Name = t.Name AND e.IsActive = 0)
        BEGIN
            ;THROW 50000, N'An incompatible yoga expense type collision was found.', 1;
        END

        INSERT INTO Expenses (BusinessId, ExpenseTypeId, SpentOn, Amount, Description, CreatedAt)
        SELECT @yogaId, et.Id, t.SpentOn, t.Amount, t.Description, t.SpentOn
          FROM (VALUES
                    (N'Alquiler de salón (demo)',   CASE WHEN DATEADD(DAY, 1, @thisMonthDay1) > @today THEN @today ELSE DATEADD(DAY, 1, @thisMonthDay1) END, 95000.00, N'Alquiler mensual'),
                    (N'Insumos (demo)',             CASE WHEN DATEADD(DAY, 7, @thisMonthDay1) > @today THEN @today ELSE DATEADD(DAY, 7, @thisMonthDay1) END, 8000.00,  N'Elementos de limpieza'),
                    (N'Mantenimiento (demo)',       DATEADD(DAY, 6, @prevMonthDay1), 22000.00, N'Service de equipos'),
                    (N'Alquiler de salón (demo)',   DATEADD(DAY, 3, @prevMonthDay1), 95000.00, N'Alquiler mensual')
               ) AS t(TypeName, SpentOn, Amount, Description)
          JOIN ExpenseTypes et ON et.BusinessId = @yogaId AND et.Name = t.TypeName;

        DECLARE @yogaSupplies TABLE (Name NVARCHAR(100), CurrentStock DECIMAL(10,2), MinimumStock DECIMAL(10,2));
        INSERT INTO @yogaSupplies (Name, CurrentStock, MinimumStock) VALUES
            (N'Mat de yoga (demo)',        5.00,  10.00),
            (N'Bloques de yoga (demo)',    24.00, 8.00),
            (N'Sales de limpieza (demo)',  2.00,  6.00);

        IF EXISTS (SELECT 1 FROM @yogaSupplies t
                    JOIN Supplies s ON s.BusinessId = @yogaId AND s.Name = t.Name)
        BEGIN
            ;THROW 50000, N'An incompatible yoga supply name collision was found.', 1;
        END

        INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
        SELECT @yogaId, t.Name, t.CurrentStock, t.MinimumStock, @localNow, 1 FROM @yogaSupplies t;

        /* 6.13 Membership enrollments: three active (two expiring soon) and one
               pending payment. Coverage dates are the shared anchors used by the
               enrollment, its fixed classes and its membership period. */
        DECLARE @y1Paid DATE = DATEADD(DAY, -18, @today);  -- active, expires in ~12 days
        DECLARE @y2Paid DATE = DATEADD(DAY, -8, @today);   -- active, expires in ~22 days
        DECLARE @y3Paid DATE = DATEADD(DAY, -25, @today);  -- expiring in ~5 days
        DECLARE @y4Paid DATE = DATEADD(DAY, -27, @today);  -- expiring in ~3 days

        DECLARE @enrY1 INT, @enrY2 INT, @enrY3 INT, @enrY4 INT, @enrY5 INT;

        -- CreatedAt precedes activation, and activation equals the coverage start.
        INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        VALUES (@yogaId, @y1, 2, 2, 16000.00, NULL, DAY(@y1Paid), DATEADD(DAY, -2, @y1Paid), @y1Paid, NULL, NULL);
        SET @enrY1 = SCOPE_IDENTITY();

        INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        VALUES (@yogaId, @y2, 2, 2, 16000.00, NULL, DAY(@y2Paid), DATEADD(DAY, -2, @y2Paid), @y2Paid, NULL, NULL);
        SET @enrY2 = SCOPE_IDENTITY();

        INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        VALUES (@yogaId, @y3, 3, 2, 22000.00, NULL, DAY(@y3Paid), DATEADD(DAY, -2, @y3Paid), @y3Paid, NULL, NULL);
        SET @enrY3 = SCOPE_IDENTITY();

        INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        VALUES (@yogaId, @y4, 2, 2, 16000.00, NULL, DAY(@y4Paid), DATEADD(DAY, -2, @y4Paid), @y4Paid, NULL, NULL);
        SET @enrY4 = SCOPE_IDENTITY();

        INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        VALUES (@yogaId, @y5, 2, 1, 16000.00, NULL, NULL, @today, NULL, NULL, NULL);
        SET @enrY5 = SCOPE_IDENTITY();

        /* 6.14 Fixed grid assignments: the demonstration uses the frequency the
               enrollment claims (2 or 3). ValidFrom is the coverage start; the
               pending enrollment holds its seats from registration. */
        INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo)
        VALUES
            (@enrY1, @gMon, @y1Paid, NULL),
            (@enrY1, @gWed, @y1Paid, NULL),
            (@enrY2, @gTue, @y2Paid, NULL),
            (@enrY2, @gThu, @y2Paid, NULL),
            (@enrY3, @gMon, @y3Paid, NULL),
            (@enrY3, @gTue, @y3Paid, NULL),
            (@enrY3, @gWed, @y3Paid, NULL),
            (@enrY4, @gWed, @y4Paid, NULL),
            (@enrY4, @gThu, @y4Paid, NULL),
            (@enrY5, @gMon, @today, NULL),
            (@enrY5, @gTue, @today, NULL);

        -- The number of fixed assignments must equal the claimed weekly frequency.
        IF EXISTS (
            SELECT 1 FROM Enrollments e
             WHERE e.BusinessId = @yogaId AND e.Id IN (@enrY1, @enrY2, @enrY3, @enrY4, @enrY5)
               AND e.WeeklyFrequency <> (SELECT COUNT(*) FROM EnrollmentFixedClasses efc WHERE efc.EnrollmentId = e.Id))
        BEGIN
            ;THROW 50000, N'A demo enrollment does not have exactly its weekly frequency in fixed grid assignments.', 1;
        END

        /* 6.15 Permanent seat limit per grid class, counting pre-existing
               enrollments and the new ones. Never bypassed. */
        IF EXISTS (
            SELECT 1 FROM GridClasses g
             JOIN Services svc ON svc.Id = g.ServiceId
             WHERE g.BusinessId = @yogaId AND g.Id IN (@gMon, @gTue, @gWed, @gThu)
               AND (SELECT COUNT(DISTINCT efc.EnrollmentId)
                      FROM EnrollmentFixedClasses efc
                      JOIN Enrollments e ON e.Id = efc.EnrollmentId
                     WHERE efc.GridClassId = g.Id AND e.Status IN (1, 2)
                       AND (efc.ValidTo IS NULL OR efc.ValidTo >= @today))
                   > svc.MaxCapacity)
        BEGIN
            ;THROW 50000, N'A demo enrollment would exceed the permanent seats of a grid class (including pre-existing seats).', 1;
        END

        /* 6.16 Class sessions only for the four exact grid classes, only in the
               previous, current and next week. NOT EXISTS keeps a pre-generated
               week from being duplicated. */
        INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
        SELECT @yogaId, 2, g.ServiceId, g.WorkerId, g.Id,
               ss.SessionStart,
               DATEADD(MINUTE, svc.DurationMinutes, ss.SessionStart),
               svc.MaxCapacity, NULL, 1,
               CASE WHEN ss.SessionStart > @localNow THEN @localNow ELSE DATEADD(DAY, -14, ss.SessionStart) END
          FROM GridClasses g
          JOIN Services svc ON svc.Id = g.ServiceId AND svc.BusinessId = @yogaId
          CROSS JOIN (VALUES (-7), (0), (7)) AS wk(WeekOffset)
          CROSS APPLY (SELECT SessionDate = DATEADD(DAY, wk.WeekOffset + g.DayOfWeek - 1, @weekMonday)) AS d
          CROSS APPLY (SELECT SessionStart = DATEADD(MINUTE,
                        DATEDIFF(MINUTE, CAST('00:00:00' AS TIME(0)), CAST(g.StartTime AS TIME(0))),
                        CAST(d.SessionDate AS DATETIME2(0)))) AS ss
         WHERE g.BusinessId = @yogaId AND g.Id IN (@gMon, @gTue, @gWed, @gThu)
           AND NOT EXISTS (SELECT 1 FROM Appointments a WHERE a.GridClassId = g.Id AND CAST(a.StartsAt AS DATE) = d.SessionDate);

        /* 6.17 The exact sessions in scope: our four grid classes, scheduled,
               previous Monday through next Sunday. Only these are touched. */
        DECLARE @yogaSessions TABLE (
            AppointmentId INT          NOT NULL PRIMARY KEY,
            GridClassId   INT          NOT NULL,
            ServiceId     INT          NOT NULL,
            MaxCapacity   INT          NOT NULL,
            StartsAt      DATETIME2(0) NOT NULL);

        INSERT INTO @yogaSessions (AppointmentId, GridClassId, ServiceId, MaxCapacity, StartsAt)
        SELECT a.Id, a.GridClassId, a.ServiceId, a.MaxCapacity, a.StartsAt
          FROM Appointments a
         WHERE a.BusinessId = @yogaId AND a.Kind = 2 AND a.Status = 1
           AND a.GridClassId IN (@gMon, @gTue, @gWed, @gThu)
           AND CAST(a.StartsAt AS DATE) BETWEEN DATEADD(DAY, -7, @weekMonday) AND DATEADD(DAY, 13, @weekMonday);

        /* 6.18 Planned fixed bookings: only the enrollments this batch inserts,
               only in the bounded session range, only within each assignment's
               validity window, and only when the pair is not already booked. */
        DECLARE @plannedFixed TABLE (
            AppointmentId   INT          NOT NULL,
            EnrollmentId    INT          NOT NULL,
            ClientId        INT          NOT NULL,
            StartsAt        DATETIME2(0) NOT NULL,
            DurationMinutes INT          NOT NULL);

        INSERT INTO @plannedFixed (AppointmentId, EnrollmentId, ClientId, StartsAt, DurationMinutes)
        SELECT s.AppointmentId, e.Id, e.ClientId, s.StartsAt, svc.DurationMinutes
          FROM @yogaSessions s
          JOIN Services svc ON svc.Id = s.ServiceId
          JOIN EnrollmentFixedClasses efc ON efc.GridClassId = s.GridClassId
          JOIN Enrollments e ON e.Id = efc.EnrollmentId
         WHERE e.Id IN (@enrY1, @enrY2, @enrY3, @enrY4)
           AND e.Status = 2
           AND CAST(s.StartsAt AS DATE) >= efc.ValidFrom
           AND (efc.ValidTo IS NULL OR CAST(s.StartsAt AS DATE) <= efc.ValidTo)
           AND NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.AppointmentId = s.AppointmentId AND b.EnrollmentId = e.Id);

        /* 6.19 Planned drop-ins on the same bounded sessions. */
        DECLARE @sMonPrev INT = (SELECT AppointmentId FROM @yogaSessions WHERE GridClassId = @gMon AND CAST(StartsAt AS DATE) = DATEADD(DAY, -7, @weekMonday));
        DECLARE @sWedPrev INT = (SELECT AppointmentId FROM @yogaSessions WHERE GridClassId = @gWed AND CAST(StartsAt AS DATE) = DATEADD(DAY, -5, @weekMonday));
        DECLARE @sMonNext INT = (SELECT AppointmentId FROM @yogaSessions WHERE GridClassId = @gMon AND CAST(StartsAt AS DATE) = DATEADD(DAY, 7, @weekMonday));
        DECLARE @sWedNext INT = (SELECT AppointmentId FROM @yogaSessions WHERE GridClassId = @gWed AND CAST(StartsAt AS DATE) = DATEADD(DAY, 9, @weekMonday));

        DECLARE @plannedDropins TABLE (
            AppointmentId   INT          NOT NULL,
            ClientId        INT          NOT NULL,
            StartsAt        DATETIME2(0) NOT NULL,
            DurationMinutes INT          NOT NULL,
            BookingStatus   TINYINT      NOT NULL);

        INSERT INTO @plannedDropins (AppointmentId, ClientId, StartsAt, DurationMinutes, BookingStatus)
        SELECT s.AppointmentId, d.ClientId, s.StartsAt, svc.DurationMinutes, d.BookingStatus
          FROM (VALUES
                    (@y6, @sMonPrev, 3),
                    (@y6, @sMonNext, 1),
                    (@y2, @sWedNext, 2),
                    (@y5, @sWedPrev, 5)
               ) AS d(ClientId, AppointmentId, BookingStatus)
          JOIN @yogaSessions s ON s.AppointmentId = d.AppointmentId
          JOIN Services svc ON svc.Id = s.ServiceId
         WHERE d.AppointmentId IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.AppointmentId = d.AppointmentId AND b.ClientId = d.ClientId);

        /* 6.20 Session capacity: live bookings already on the session plus every
               proposed booking must fit MaxCapacity. Refuse, never skip. */
        IF EXISTS (
            SELECT 1 FROM @yogaSessions s
             WHERE (SELECT COUNT(*) FROM Bookings b WHERE b.AppointmentId = s.AppointmentId AND b.Status <> 5)
                 + (SELECT COUNT(*) FROM @plannedFixed p WHERE p.AppointmentId = s.AppointmentId)
                 + (SELECT COUNT(*) FROM @plannedDropins d WHERE d.AppointmentId = s.AppointmentId AND d.BookingStatus <> 5)
                 > s.MaxCapacity)
        BEGIN
            ;THROW 50000, N'A proposed yoga booking would exceed the session capacity.', 1;
        END

        /* 6.21 Insert the planned fixed bookings. Past sessions are attended,
               future sessions are confirmed, and every instant is backdated
               before its start. */
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy,
                              AgreedPrice, PriceReason, CreatedAt, ConfirmedAt, CancelledAt, AttendanceRecordedAt)
        SELECT @yogaId, p.AppointmentId, p.ClientId, p.EnrollmentId, 1,
               CASE WHEN p.StartsAt <= @localNow THEN 3 ELSE 2 END,
               NULL, NULL, NULL, NULL,
               CASE WHEN p.StartsAt > @localNow THEN @localNow ELSE DATEADD(DAY, -7, p.StartsAt) END,
               CASE WHEN p.StartsAt > @localNow THEN @localNow ELSE NULL END,
               NULL,
               CASE WHEN p.StartsAt <= @localNow THEN DATEADD(MINUTE, p.DurationMinutes, p.StartsAt) ELSE NULL END
          FROM @plannedFixed p
         WHERE NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.AppointmentId = p.AppointmentId AND b.EnrollmentId = p.EnrollmentId);

        /* 6.22 Insert the planned drop-ins. */
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy,
                              AgreedPrice, PriceReason, CreatedAt, ConfirmedAt, CancelledAt, AttendanceRecordedAt)
        SELECT @yogaId, d.AppointmentId, d.ClientId, NULL, 2, d.BookingStatus, NULL,
               CASE WHEN d.BookingStatus = 5 THEN 1 ELSE NULL END,
               NULL, NULL,
               CASE WHEN d.StartsAt > @localNow THEN @localNow ELSE DATEADD(DAY, -7, d.StartsAt) END,
               CASE WHEN d.BookingStatus = 2 THEN @localNow ELSE NULL END,
               CASE WHEN d.BookingStatus = 5 THEN @localNow ELSE NULL END,
               CASE WHEN d.BookingStatus = 3 THEN DATEADD(MINUTE, d.DurationMinutes, d.StartsAt) ELSE NULL END
          FROM @plannedDropins d
         WHERE NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.AppointmentId = d.AppointmentId AND b.ClientId = d.ClientId AND b.Status <> 5);

        /* 6.23 Membership payments and their periods. ExpiresOn is the first day
               without coverage and always falls after the coverage start. */
        INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
        SELECT @yogaId, e.ClientId, 4, NULL, e.Id, e.AgreedPrice, NULL, NULL, pd.PaidAt, @localNow
          FROM Enrollments e
          JOIN Clients c ON c.Id = e.ClientId
          JOIN (VALUES
                    ('demo.client.y1@demo.invalid', @y1Paid),
                    ('demo.client.y2@demo.invalid', @y2Paid),
                    ('demo.client.y3@demo.invalid', @y3Paid),
                    ('demo.client.y4@demo.invalid', @y4Paid)
               ) AS pd(Email, PaidAt) ON pd.Email = c.Email
         WHERE e.BusinessId = @yogaId AND e.Status = 2;

        INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt)
        SELECT p.EnrollmentId, CAST(p.PaidAt AS DATE), DATEADD(MONTH, 1, CAST(p.PaidAt AS DATE)), p.Id, CAST(p.PaidAt AS DATE)
          FROM Payments p
         WHERE p.BusinessId = @yogaId AND p.Concept = 4
           AND p.EnrollmentId IN (@enrY1, @enrY2, @enrY3, @enrY4)
           AND NOT EXISTS (SELECT 1 FROM MembershipPeriods mp WHERE mp.PaymentId = p.Id);

        /* 6.24 Drop-in payments for the attended drop-ins (paid in advance of no
               coverage; recorded now). */
        INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
        SELECT @yogaId, b.ClientId, 3, b.Id, NULL, svc.Price, NULL, NULL, @localNow, @localNow
          FROM Bookings b
          JOIN Appointments a ON a.Id = b.AppointmentId
          JOIN Services svc ON svc.Id = a.ServiceId
         WHERE b.BusinessId = @yogaId AND b.Modality = 2 AND b.Status = 3
           AND b.ClientId IN (@y2, @y5, @y6)
           AND NOT EXISTS (SELECT 1 FROM Payments p WHERE p.BookingId = b.Id);
    END

    /* -----------------------------------------------------------------------
       7. Integrity assertions before the commit: a violated invariant rolls
          the whole batch back instead of reporting after the fact.
       ----------------------------------------------------------------------- */
    IF EXISTS (
        SELECT 1 FROM ClientAccounts ca
         WHERE ca.Email LIKE 'demo.client.%@demo.invalid'
           AND (ca.ClientId IS NULL OR ca.EmailVerifiedAt IS NULL))
    BEGIN
        ;THROW 50000, N'A demo client account is not linked and verified.', 1;
    END

    IF (SELECT COUNT(*) FROM ClientAccounts ca
         WHERE ca.BusinessId = @marinaId
           AND ca.Email IN (N'demo.client.m1@demo.invalid', N'demo.client.m4@demo.invalid')
           AND ca.ClientId IS NOT NULL AND ca.EmailVerifiedAt IS NOT NULL) <> 2
        OR (SELECT COUNT(*) FROM ClientAccounts ca
             WHERE ca.BusinessId = @yogaId
               AND ca.Email IN (N'demo.client.y1@demo.invalid', N'demo.client.y3@demo.invalid')
               AND ca.ClientId IS NOT NULL AND ca.EmailVerifiedAt IS NOT NULL) <> 2
    BEGIN
        ;THROW 50000, N'The expected demo client accounts are not linked and verified.', 1;
    END

    -- Every demo account must own at least one booking. Time-independent, so an
    -- already-applied dataset never starts failing as the seeded dates age.
    IF EXISTS (
        SELECT 1 FROM ClientAccounts ca
         WHERE ca.Email LIKE 'demo.client.%@demo.invalid'
           AND ca.EmailVerifiedAt IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.ClientId = ca.ClientId))
    BEGIN
        ;THROW 50000, N'A demo client account has no own booking.', 1;
    END

    -- Future eligible own reservations are required only for a business inserted
    -- in this invocation. An already-applied dataset is never judged against the
    -- current clock again.
    IF @marinaInserted = 1 AND EXISTS (
        SELECT 1 FROM ClientAccounts ca
         WHERE ca.BusinessId = @marinaId
           AND ca.Email IN (N'demo.client.m1@demo.invalid', N'demo.client.m4@demo.invalid')
           AND ca.EmailVerifiedAt IS NOT NULL
           AND NOT EXISTS (
                SELECT 1 FROM Bookings b
                  JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
                 WHERE b.ClientId = ca.ClientId AND b.Status IN (1, 2)
                   AND a.Kind = 1 AND a.Status = 1 AND a.StartsAt > @localNow))
    BEGIN
        ;THROW 50000, N'A freshly inserted marina demo account has no future eligible own reservation.', 1;
    END

    IF @yogaInserted = 1 AND EXISTS (
        SELECT 1 FROM ClientAccounts ca
         WHERE ca.BusinessId = @yogaId
           AND ca.Email IN (N'demo.client.y1@demo.invalid', N'demo.client.y3@demo.invalid')
           AND ca.EmailVerifiedAt IS NOT NULL
           AND NOT EXISTS (
                SELECT 1 FROM Bookings b
                  JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
                 WHERE b.ClientId = ca.ClientId AND b.Status IN (1, 2)
                   AND a.Status = 1 AND a.StartsAt > @localNow))
    BEGIN
        ;THROW 50000, N'A freshly inserted yoga demo account has no future eligible own reservation.', 1;
    END

    -- A demo booking may never be created after its start, nor before its cancellation.
    IF EXISTS (
        SELECT 1 FROM Bookings b
          JOIN Clients c ON c.Id = b.ClientId
         WHERE c.Email LIKE 'demo.client.%@demo.invalid'
           AND (b.CreatedAt >= b.CancelledAt
                OR b.CreatedAt >= (SELECT a.StartsAt FROM Appointments a WHERE a.Id = b.AppointmentId)))
    BEGIN
        ;THROW 50000, N'A demo booking has an incoherent created / start / cancellation ordering.', 1;
    END

    -- A membership activation must not be later than the start of its coverage.
    IF EXISTS (
        SELECT 1 FROM MembershipPeriods mp
          JOIN Enrollments e ON e.Id = mp.EnrollmentId
          JOIN Clients c ON c.Id = e.ClientId
         WHERE c.Email LIKE 'demo.client.%@demo.invalid'
           AND (e.ActivatedAt IS NULL OR e.ActivatedAt > CAST(mp.CoverageStart AS DATETIME2(0))))
    BEGIN
        ;THROW 50000, N'A demo enrollment is activated after its coverage start.', 1;
    END

    IF EXISTS (
        SELECT 1 FROM Payments p
          JOIN Bookings b ON b.Id = p.BookingId AND b.BusinessId = p.BusinessId
          JOIN Clients c ON c.Id = b.ClientId
         WHERE c.Email LIKE 'demo.client.%@demo.invalid' AND b.ClientId <> p.ClientId)
    BEGIN
        ;THROW 50000, N'A demo payment is bound to a booking of another client.', 1;
    END

    IF EXISTS (
        SELECT 1 FROM Expenses x JOIN ExpenseTypes et ON et.Id = x.ExpenseTypeId
         WHERE x.BusinessId IN (@marinaId, @yogaId) AND et.BusinessId <> x.BusinessId)
    BEGIN
        ;THROW 50000, N'A demo expense references an expense type of another business.', 1;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH

/* ---------------------------------------------------------------------------
   8. Non-sensitive report. Counts per business, the applied state and the
      demo client account emails. No hash and no password is printed. "0
      inserts" is claimed only for a business whose insert branch was skipped.
   --------------------------------------------------------------------------- */
SELECT b.Slug,
       b.Name,
       (SELECT COUNT(*) FROM Clients c WHERE c.BusinessId = b.Id AND c.Email LIKE 'demo.client.%@demo.invalid') AS DemoClients,
       (SELECT COUNT(*) FROM ClientAccounts ca WHERE ca.BusinessId = b.Id AND ca.Email LIKE 'demo.client.%@demo.invalid') AS ClientLogins,
       (SELECT COUNT(*) FROM Appointments a WHERE a.BusinessId = b.Id AND a.Kind = 1) AS IndividualAppointments,
       (SELECT COUNT(*) FROM Appointments a WHERE a.BusinessId = b.Id AND a.Kind = 2) AS ClassSessions,
       (SELECT COUNT(*) FROM Bookings bk WHERE bk.BusinessId = b.Id) AS Bookings,
       (SELECT COUNT(*) FROM Payments p WHERE p.BusinessId = b.Id) AS Payments,
       (SELECT COUNT(*) FROM Enrollments e WHERE e.BusinessId = b.Id) AS Enrollments,
       (SELECT COUNT(*) FROM MembershipPeriods mp JOIN Enrollments e ON e.Id = mp.EnrollmentId WHERE e.BusinessId = b.Id) AS MembershipPeriods,
       (SELECT COUNT(*) FROM Expenses x WHERE x.BusinessId = b.Id) AS Expenses,
       (SELECT COUNT(*) FROM Supplies s WHERE s.BusinessId = b.Id) AS Supplies,
       (SELECT COUNT(*) FROM Supplies s WHERE s.BusinessId = b.Id AND s.CurrentStock < s.MinimumStock) AS LowSupplies
  FROM Businesses b
 WHERE b.Id IN (@marinaId, @yogaId)
 ORDER BY b.Slug;

SELECT CASE WHEN @marinaInserted = 1 THEN 'applied' ELSE 'already applied (0 inserts for this business)' END AS MarinaBatch,
       CASE WHEN @yogaInserted   = 1 THEN 'applied' ELSE 'already applied (0 inserts for this business)' END AS YogaBatch;

SELECT b.Slug AS BusinessSlug, ca.Email AS DemoClientLogin
  FROM ClientAccounts ca
  JOIN Businesses b ON b.Id = ca.BusinessId
 WHERE ca.Email LIKE 'demo.client.%@demo.invalid'
 ORDER BY b.Slug, ca.Email;
