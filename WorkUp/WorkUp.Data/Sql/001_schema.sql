-- WorkUp schema, SRS v3.0. Executed once by DatabaseMigrator; batches are separated by GO.
-- Conventions: English PascalCase names, INT identity keys, TINYINT enums (see WorkUp.Domain.Enums),
-- DATETIME2(0) business-local times (Argentina), DECIMAL(12,2) money. Every tenant table carries BusinessId.

CREATE TABLE Businesses (
    Id                   INT IDENTITY PRIMARY KEY,
    Name                 NVARCHAR(120) NOT NULL,
    Slug                 VARCHAR(60)   NOT NULL CONSTRAINT UQ_Businesses_Slug UNIQUE,
    ContactPhone         VARCHAR(30)   NOT NULL,
    NotificationEmail    NVARCHAR(254) NULL,
    AllowsWalkIns        BIT           NOT NULL CONSTRAINT DF_Businesses_AllowsWalkIns DEFAULT 1,
    ReminderLeadHours    INT           NOT NULL CONSTRAINT DF_Businesses_ReminderLeadHours DEFAULT 24,
    DailySummaryTime     TIME(0)       NOT NULL CONSTRAINT DF_Businesses_DailySummaryTime DEFAULT '21:00',
    StockReviewEveryDays INT           NOT NULL CONSTRAINT DF_Businesses_StockReviewEveryDays DEFAULT 7,
    CreatedAt            DATETIME2(0)  NOT NULL,
    CONSTRAINT CK_Businesses_ReminderLeadHours CHECK (ReminderLeadHours BETWEEN 1 AND 168),
    CONSTRAINT CK_Businesses_StockReviewEveryDays CHECK (StockReviewEveryDays BETWEEN 1 AND 90)
);

CREATE TABLE OwnerAccounts (
    Id           INT IDENTITY PRIMARY KEY,
    BusinessId   INT           NOT NULL CONSTRAINT FK_OwnerAccounts_Businesses REFERENCES Businesses(Id),
    Username     NVARCHAR(60)  NOT NULL CONSTRAINT UQ_OwnerAccounts_Username UNIQUE,
    PasswordHash NVARCHAR(400) NOT NULL,
    CreatedAt    DATETIME2(0)  NOT NULL,
    -- One account per business (D-06, Restricción 5).
    CONSTRAINT UQ_OwnerAccounts_Business UNIQUE (BusinessId)
);

CREATE TABLE ExpenseTypes (
    Id         INT IDENTITY PRIMARY KEY,
    BusinessId INT          NOT NULL CONSTRAINT FK_ExpenseTypes_Businesses REFERENCES Businesses(Id),
    Name       NVARCHAR(80) NOT NULL,
    IsActive   BIT          NOT NULL CONSTRAINT DF_ExpenseTypes_IsActive DEFAULT 1,
    CONSTRAINT UQ_ExpenseTypes_Name UNIQUE (BusinessId, Name)
);

CREATE TABLE Services (
    Id              INT IDENTITY PRIMARY KEY,
    BusinessId      INT            NOT NULL CONSTRAINT FK_Services_Businesses REFERENCES Businesses(Id),
    Name            NVARCHAR(100)  NOT NULL,
    Price           DECIMAL(12, 2) NOT NULL,
    DurationMinutes INT            NOT NULL,
    MaxCapacity     INT            NOT NULL,
    IsActive        BIT            NOT NULL CONSTRAINT DF_Services_IsActive DEFAULT 1,
    CONSTRAINT CK_Services_Price CHECK (Price > 0),
    CONSTRAINT CK_Services_Duration CHECK (DurationMinutes > 0),
    CONSTRAINT CK_Services_MaxCapacity CHECK (MaxCapacity >= 1)
);

CREATE TABLE MembershipPrices (
    Id              INT IDENTITY PRIMARY KEY,
    BusinessId      INT            NOT NULL CONSTRAINT FK_MembershipPrices_Businesses REFERENCES Businesses(Id),
    WeeklyFrequency INT            NOT NULL,
    Price           DECIMAL(12, 2) NOT NULL,
    CONSTRAINT UQ_MembershipPrices_Frequency UNIQUE (BusinessId, WeeklyFrequency),
    CONSTRAINT CK_MembershipPrices_Frequency CHECK (WeeklyFrequency BETWEEN 1 AND 7),
    CONSTRAINT CK_MembershipPrices_Price CHECK (Price > 0)
);

CREATE TABLE Workers (
    Id         INT IDENTITY PRIMARY KEY,
    BusinessId INT           NOT NULL CONSTRAINT FK_Workers_Businesses REFERENCES Businesses(Id),
    FirstName  NVARCHAR(60)  NOT NULL,
    LastName   NVARCHAR(60)  NOT NULL,
    Phone      VARCHAR(30)   NOT NULL,
    Cuil       VARCHAR(13)   NOT NULL,
    IsActive   BIT           NOT NULL CONSTRAINT DF_Workers_IsActive DEFAULT 1,
    CONSTRAINT UQ_Workers_Cuil UNIQUE (BusinessId, Cuil)
);

-- DayOfWeek follows System.DayOfWeek: 0 = Sunday ... 6 = Saturday.
CREATE TABLE WorkerSchedules (
    Id        INT IDENTITY PRIMARY KEY,
    WorkerId  INT     NOT NULL CONSTRAINT FK_WorkerSchedules_Workers REFERENCES Workers(Id) ON DELETE CASCADE,
    DayOfWeek TINYINT NOT NULL,
    StartTime TIME(0) NOT NULL,
    EndTime   TIME(0) NOT NULL,
    CONSTRAINT CK_WorkerSchedules_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
    CONSTRAINT CK_WorkerSchedules_Range CHECK (EndTime > StartTime)
);

CREATE TABLE WorkerBreaks (
    Id        INT IDENTITY PRIMARY KEY,
    WorkerId  INT     NOT NULL CONSTRAINT FK_WorkerBreaks_Workers REFERENCES Workers(Id) ON DELETE CASCADE,
    DayOfWeek TINYINT NOT NULL,
    StartTime TIME(0) NOT NULL,
    EndTime   TIME(0) NOT NULL,
    CONSTRAINT CK_WorkerBreaks_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
    CONSTRAINT CK_WorkerBreaks_Range CHECK (EndTime > StartTime)
);

CREATE TABLE GridClasses (
    Id         INT IDENTITY PRIMARY KEY,
    BusinessId INT     NOT NULL CONSTRAINT FK_GridClasses_Businesses REFERENCES Businesses(Id),
    ServiceId  INT     NOT NULL CONSTRAINT FK_GridClasses_Services REFERENCES Services(Id),
    WorkerId   INT     NOT NULL CONSTRAINT FK_GridClasses_Workers REFERENCES Workers(Id),
    DayOfWeek  TINYINT NOT NULL,
    StartTime  TIME(0) NOT NULL,
    IsActive   BIT     NOT NULL CONSTRAINT DF_GridClasses_IsActive DEFAULT 1,
    CONSTRAINT CK_GridClasses_Day CHECK (DayOfWeek BETWEEN 0 AND 6)
);

CREATE TABLE Clients (
    Id          INT IDENTITY PRIMARY KEY,
    BusinessId  INT            NOT NULL CONSTRAINT FK_Clients_Businesses REFERENCES Businesses(Id),
    FirstName   NVARCHAR(60)   NOT NULL,
    LastName    NVARCHAR(60)   NOT NULL,
    Email       NVARCHAR(254)  NOT NULL,
    Phone       VARCHAR(30)    NULL,
    HealthNotes NVARCHAR(1000) NULL,
    CreatedAt   DATETIME2(0)   NOT NULL,
    -- The client is identified by email inside a business (Regla 7). Emails are stored lower-case.
    CONSTRAINT UQ_Clients_Email UNIQUE (BusinessId, Email)
);

CREATE TABLE ClientAccounts (
    Id              INT IDENTITY PRIMARY KEY,
    BusinessId      INT           NOT NULL CONSTRAINT FK_ClientAccounts_Businesses REFERENCES Businesses(Id),
    ClientId        INT           NULL CONSTRAINT FK_ClientAccounts_Clients REFERENCES Clients(Id),
    Email           NVARCHAR(254) NOT NULL,
    PasswordHash    NVARCHAR(400) NOT NULL,
    EmailVerifiedAt DATETIME2(0)  NULL,
    CreatedAt       DATETIME2(0)  NOT NULL,
    CONSTRAINT UQ_ClientAccounts_Email UNIQUE (BusinessId, Email)
);

-- A client record is linked to at most one account (RF-26: no automatic merges).
CREATE UNIQUE INDEX UX_ClientAccounts_Client ON ClientAccounts (ClientId) WHERE ClientId IS NOT NULL;

CREATE TABLE AccountTokens (
    Id              INT IDENTITY PRIMARY KEY,
    ClientAccountId INT          NOT NULL CONSTRAINT FK_AccountTokens_ClientAccounts REFERENCES ClientAccounts(Id),
    Purpose         TINYINT      NOT NULL,
    TokenHash       BINARY(32)   NOT NULL CONSTRAINT UQ_AccountTokens_TokenHash UNIQUE,
    ExpiresAt       DATETIME2(0) NOT NULL,
    UsedAt          DATETIME2(0) NULL,
    CreatedAt       DATETIME2(0) NOT NULL
);

CREATE TABLE AccountInvitations (
    Id         INT IDENTITY PRIMARY KEY,
    BusinessId INT           NOT NULL CONSTRAINT FK_AccountInvitations_Businesses REFERENCES Businesses(Id),
    ClientId   INT           NOT NULL CONSTRAINT FK_AccountInvitations_Clients REFERENCES Clients(Id),
    Email      NVARCHAR(254) NOT NULL,
    TokenHash  BINARY(32)    NOT NULL CONSTRAINT UQ_AccountInvitations_TokenHash UNIQUE,
    ExpiresAt  DATETIME2(0)  NOT NULL,
    UsedAt     DATETIME2(0)  NULL,
    RevokedAt  DATETIME2(0)  NULL,
    CreatedAt  DATETIME2(0)  NOT NULL
);

-- Turno: individual appointment, walk-in attention, or dated weekly class.
CREATE TABLE Appointments (
    Id               INT IDENTITY PRIMARY KEY,
    BusinessId       INT           NOT NULL CONSTRAINT FK_Appointments_Businesses REFERENCES Businesses(Id),
    Kind             TINYINT       NOT NULL,
    ServiceId        INT           NOT NULL CONSTRAINT FK_Appointments_Services REFERENCES Services(Id),
    WorkerId         INT           NOT NULL CONSTRAINT FK_Appointments_Workers REFERENCES Workers(Id),
    GridClassId      INT           NULL CONSTRAINT FK_Appointments_GridClasses REFERENCES GridClasses(Id),
    StartsAt         DATETIME2(0)  NOT NULL,
    EndsAt           DATETIME2(0)  NOT NULL,
    MaxCapacity      INT           NOT NULL,
    Origin           TINYINT       NULL,
    Status           TINYINT       NOT NULL CONSTRAINT DF_Appointments_Status DEFAULT 1,
    SuspensionReason NVARCHAR(300) NULL,
    SuspendedAt      DATETIME2(0)  NULL,
    CreatedAt        DATETIME2(0)  NOT NULL,
    CONSTRAINT CK_Appointments_Range CHECK (EndsAt > StartsAt),
    CONSTRAINT CK_Appointments_Capacity CHECK (MaxCapacity >= 1),
    -- Individual appointments (reserved or walk-in) have an origin and capacity 1; class sessions have no origin.
    CONSTRAINT CK_Appointments_KindShape CHECK (
        (Kind = 1 AND Origin IS NOT NULL AND MaxCapacity = 1 AND GridClassId IS NULL)
        OR (Kind = 2 AND Origin IS NULL)
    ),
    -- Only class sessions can be suspended (RF-10).
    CONSTRAINT CK_Appointments_Suspension CHECK (Status = 1 OR Kind = 2)
);

CREATE INDEX IX_Appointments_WorkerDay ON Appointments (WorkerId, StartsAt) INCLUDE (EndsAt, Status);
CREATE INDEX IX_Appointments_BusinessDay ON Appointments (BusinessId, StartsAt);

-- Week generation is idempotent: one session per grid class and start (RF-06).
CREATE UNIQUE INDEX UX_Appointments_GridSession ON Appointments (GridClassId, StartsAt) WHERE GridClassId IS NOT NULL;

-- Observación para la próxima atención (RF-11). Owner-only.
CREATE TABLE ClientNotes (
    Id            INT IDENTITY PRIMARY KEY,
    ClientId      INT            NOT NULL CONSTRAINT FK_ClientNotes_Clients REFERENCES Clients(Id),
    AppointmentId INT            NULL CONSTRAINT FK_ClientNotes_Appointments REFERENCES Appointments(Id),
    Text          NVARCHAR(1000) NOT NULL,
    CreatedAt     DATETIME2(0)   NOT NULL
);

CREATE INDEX IX_ClientNotes_Client ON ClientNotes (ClientId);

CREATE TABLE Enrollments (
    Id              INT IDENTITY PRIMARY KEY,
    BusinessId      INT            NOT NULL CONSTRAINT FK_Enrollments_Businesses REFERENCES Businesses(Id),
    ClientId        INT            NOT NULL CONSTRAINT FK_Enrollments_Clients REFERENCES Clients(Id),
    WeeklyFrequency INT            NOT NULL,
    Status          TINYINT        NOT NULL CONSTRAINT DF_Enrollments_Status DEFAULT 1,
    AgreedPrice     DECIMAL(12, 2) NOT NULL,
    PriceReason     NVARCHAR(200)  NULL,
    ReferenceDay    TINYINT        NULL,
    CreatedAt       DATETIME2(0)   NOT NULL,
    ActivatedAt     DATETIME2(0)   NULL,
    AnnulledAt      DATETIME2(0)   NULL,
    TerminatedAt    DATETIME2(0)   NULL,
    CONSTRAINT CK_Enrollments_Frequency CHECK (WeeklyFrequency BETWEEN 1 AND 7),
    CONSTRAINT CK_Enrollments_Price CHECK (AgreedPrice > 0),
    CONSTRAINT CK_Enrollments_ReferenceDay CHECK (ReferenceDay IS NULL OR ReferenceDay BETWEEN 1 AND 31)
);

CREATE TABLE EnrollmentFixedClasses (
    Id           INT IDENTITY PRIMARY KEY,
    EnrollmentId INT  NOT NULL CONSTRAINT FK_EnrollmentFixedClasses_Enrollments REFERENCES Enrollments(Id),
    GridClassId  INT  NOT NULL CONSTRAINT FK_EnrollmentFixedClasses_GridClasses REFERENCES GridClasses(Id),
    ValidFrom    DATE NOT NULL,
    ValidTo      DATE NULL,
    CONSTRAINT CK_EnrollmentFixedClasses_Range CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
);

CREATE INDEX IX_EnrollmentFixedClasses_Grid ON EnrollmentFixedClasses (GridClassId, ValidFrom) INCLUDE (ValidTo, EnrollmentId);

-- Anotación de un cliente en un turno.
CREATE TABLE Bookings (
    Id                   INT IDENTITY PRIMARY KEY,
    BusinessId           INT            NOT NULL CONSTRAINT FK_Bookings_Businesses REFERENCES Businesses(Id),
    AppointmentId        INT            NOT NULL CONSTRAINT FK_Bookings_Appointments REFERENCES Appointments(Id),
    ClientId             INT            NOT NULL CONSTRAINT FK_Bookings_Clients REFERENCES Clients(Id),
    EnrollmentId         INT            NULL CONSTRAINT FK_Bookings_Enrollments REFERENCES Enrollments(Id),
    Modality             TINYINT        NULL,
    Status               TINYINT        NOT NULL CONSTRAINT DF_Bookings_Status DEFAULT 1,
    AbsenceSource        TINYINT        NULL,
    CancelledBy          TINYINT        NULL,
    AgreedPrice          DECIMAL(12, 2) NULL,
    PriceReason          NVARCHAR(200)  NULL,
    CreatedAt            DATETIME2(0)   NOT NULL,
    ConfirmedAt          DATETIME2(0)   NULL,
    CancelledAt          DATETIME2(0)   NULL,
    AttendanceRecordedAt DATETIME2(0)   NULL,
    CONSTRAINT CK_Bookings_FixedHasEnrollment CHECK (Modality IS NULL OR Modality <> 1 OR EnrollmentId IS NOT NULL),
    CONSTRAINT CK_Bookings_Cancelled CHECK ((Status = 5 AND CancelledAt IS NOT NULL) OR (Status <> 5 AND CancelledAt IS NULL))
);

CREATE INDEX IX_Bookings_Appointment ON Bookings (AppointmentId) INCLUDE (Status, ClientId);
CREATE INDEX IX_Bookings_Client ON Bookings (ClientId);

-- Regenerating a week never duplicates a fixed booking (RF-06).
CREATE UNIQUE INDEX UX_Bookings_FixedPerSession ON Bookings (AppointmentId, EnrollmentId) WHERE EnrollmentId IS NOT NULL;

-- A client holds at most one active booking per appointment.
CREATE UNIQUE INDEX UX_Bookings_ActiveClient ON Bookings (AppointmentId, ClientId) WHERE Status <> 5;

CREATE TABLE AppointmentLinks (
    Id             INT IDENTITY PRIMARY KEY,
    BookingId      INT           NOT NULL CONSTRAINT FK_AppointmentLinks_Bookings REFERENCES Bookings(Id),
    TokenHash      BINARY(32)    NOT NULL CONSTRAINT UQ_AppointmentLinks_TokenHash UNIQUE,
    ProtectedToken NVARCHAR(600) NOT NULL,
    ExpiresAt      DATETIME2(0)  NOT NULL,
    RevokedAt      DATETIME2(0)  NULL,
    CreatedAt      DATETIME2(0)  NOT NULL
);

CREATE INDEX IX_AppointmentLinks_Booking ON AppointmentLinks (BookingId);

CREATE TABLE Payments (
    Id           INT IDENTITY PRIMARY KEY,
    BusinessId   INT            NOT NULL CONSTRAINT FK_Payments_Businesses REFERENCES Businesses(Id),
    ClientId     INT            NULL CONSTRAINT FK_Payments_Clients REFERENCES Clients(Id),
    Concept      TINYINT        NOT NULL,
    BookingId    INT            NULL CONSTRAINT FK_Payments_Bookings REFERENCES Bookings(Id),
    EnrollmentId INT            NULL CONSTRAINT FK_Payments_Enrollments REFERENCES Enrollments(Id),
    Amount       DECIMAL(12, 2) NOT NULL,
    ListPrice    DECIMAL(12, 2) NULL,
    PriceReason  NVARCHAR(200)  NULL,
    PaidAt       DATETIME2(0)   NOT NULL,
    RecordedAt   DATETIME2(0)   NOT NULL,
    CONSTRAINT CK_Payments_Amount CHECK (Amount > 0),
    -- Every payment identifies its concrete origin (D-20).
    CONSTRAINT CK_Payments_Reference CHECK (
        (Concept = 4 AND EnrollmentId IS NOT NULL AND BookingId IS NULL)
        OR (Concept <> 4 AND BookingId IS NOT NULL AND EnrollmentId IS NULL)
    )
);

CREATE INDEX IX_Payments_BusinessPaidAt ON Payments (BusinessId, PaidAt);
CREATE INDEX IX_Payments_Booking ON Payments (BookingId) WHERE BookingId IS NOT NULL;

CREATE TABLE PaymentAnnulments (
    Id         INT IDENTITY PRIMARY KEY,
    PaymentId  INT           NOT NULL CONSTRAINT FK_PaymentAnnulments_Payments REFERENCES Payments(Id),
    Reason     NVARCHAR(300) NOT NULL,
    AnnulledAt DATETIME2(0)  NOT NULL,
    AnnulledBy NVARCHAR(60)  NOT NULL,
    CONSTRAINT UQ_PaymentAnnulments_Payment UNIQUE (PaymentId)
);

CREATE TABLE Refunds (
    Id         INT IDENTITY PRIMARY KEY,
    PaymentId  INT            NOT NULL CONSTRAINT FK_Refunds_Payments REFERENCES Payments(Id),
    Amount     DECIMAL(12, 2) NOT NULL,
    RefundedOn DATE           NOT NULL,
    Reason     NVARCHAR(300)  NOT NULL,
    RecordedAt DATETIME2(0)   NOT NULL,
    CONSTRAINT CK_Refunds_Amount CHECK (Amount > 0)
);

CREATE TABLE MembershipPeriods (
    Id            INT IDENTITY PRIMARY KEY,
    EnrollmentId  INT          NOT NULL CONSTRAINT FK_MembershipPeriods_Enrollments REFERENCES Enrollments(Id),
    CoverageStart DATE         NOT NULL,
    ExpiresOn     DATE         NOT NULL,
    PaymentId     INT          NOT NULL CONSTRAINT FK_MembershipPeriods_Payments REFERENCES Payments(Id),
    CreatedAt     DATETIME2(0) NOT NULL,
    CONSTRAINT CK_MembershipPeriods_Range CHECK (ExpiresOn > CoverageStart),
    CONSTRAINT UQ_MembershipPeriods_Payment UNIQUE (PaymentId)
);

CREATE INDEX IX_MembershipPeriods_Enrollment ON MembershipPeriods (EnrollmentId, ExpiresOn);

CREATE TABLE Expenses (
    Id            INT IDENTITY PRIMARY KEY,
    BusinessId    INT            NOT NULL CONSTRAINT FK_Expenses_Businesses REFERENCES Businesses(Id),
    ExpenseTypeId INT            NOT NULL CONSTRAINT FK_Expenses_ExpenseTypes REFERENCES ExpenseTypes(Id),
    SpentOn       DATE           NOT NULL,
    Amount        DECIMAL(12, 2) NOT NULL,
    Description   NVARCHAR(300)  NOT NULL,
    CreatedAt     DATETIME2(0)   NOT NULL,
    CONSTRAINT CK_Expenses_Amount CHECK (Amount > 0)
);

CREATE INDEX IX_Expenses_BusinessDay ON Expenses (BusinessId, SpentOn);

CREATE TABLE Supplies (
    Id             INT IDENTITY PRIMARY KEY,
    BusinessId     INT            NOT NULL CONSTRAINT FK_Supplies_Businesses REFERENCES Businesses(Id),
    Name           NVARCHAR(100)  NOT NULL,
    CurrentStock   DECIMAL(10, 2) NOT NULL,
    MinimumStock   DECIMAL(10, 2) NOT NULL,
    LastDeclaredAt DATETIME2(0)   NOT NULL,
    IsActive       BIT            NOT NULL CONSTRAINT DF_Supplies_IsActive DEFAULT 1,
    CONSTRAINT UQ_Supplies_Name UNIQUE (BusinessId, Name),
    CONSTRAINT CK_Supplies_Stock CHECK (CurrentStock >= 0 AND MinimumStock >= 0)
);

-- Email outbox. DedupKey makes every notice idempotent across retries.
CREATE TABLE Notifications (
    Id         INT IDENTITY PRIMARY KEY,
    BusinessId INT           NULL CONSTRAINT FK_Notifications_Businesses REFERENCES Businesses(Id),
    Type       TINYINT       NOT NULL,
    Recipient  NVARCHAR(254) NOT NULL,
    Subject    NVARCHAR(200) NOT NULL,
    BodyHtml   NVARCHAR(MAX) NOT NULL,
    DedupKey   NVARCHAR(150) NOT NULL CONSTRAINT UQ_Notifications_DedupKey UNIQUE,
    Status     TINYINT       NOT NULL CONSTRAINT DF_Notifications_Status DEFAULT 1,
    Attempts   INT           NOT NULL CONSTRAINT DF_Notifications_Attempts DEFAULT 0,
    LastError  NVARCHAR(500) NULL,
    CreatedAt  DATETIME2(0)  NOT NULL,
    SentAt     DATETIME2(0)  NULL
);

CREATE INDEX IX_Notifications_Pending ON Notifications (Status, CreatedAt);

-- Key/value settings for the whole installation, such as the simulated clock offset (plan assumption 11).
CREATE TABLE SystemSettings (
    [Key]   VARCHAR(60)    NOT NULL PRIMARY KEY,
    [Value] NVARCHAR(400)  NOT NULL
);
