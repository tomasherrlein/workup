namespace WorkUp.Domain.Enums;

// Numeric values are persisted as TINYINT columns. Never renumber an existing member.

/// <summary>Whether an appointment is an individual turn (max capacity 1) or a dated weekly class.</summary>
public enum AppointmentKind : byte
{
    Individual = 1,
    ClassSession = 2
}

/// <summary>Origen del turno: <c>Reservado</c> or <c>Sin reserva</c>. Only individual appointments have an origin.</summary>
public enum AppointmentOrigin : byte
{
    Reserved = 1,
    WalkIn = 2
}

/// <summary>Estado del turno: <c>Programado</c> or <c>Suspendido</c>.</summary>
public enum AppointmentStatus : byte
{
    Scheduled = 1,
    Suspended = 2
}

/// <summary>Modalidad de la anotación en una clase: <c>Fija</c> or <c>Suelta</c>. Individual bookings have none.</summary>
public enum BookingModality : byte
{
    Fixed = 1,
    DropIn = 2
}

/// <summary>
/// Estado del cliente en el turno. <c>Cobrado</c> is not a status: it is derived from a non-annulled payment,
/// because attendance and payment are independent (D-20).
/// </summary>
public enum BookingStatus : byte
{
    Pending = 1,
    Confirmed = 2,
    Attended = 3,
    Absent = 4,
    Cancelled = 5
}

/// <summary>Who reported an absence: the owner, or the client through the private link (RF-12, RF-24).</summary>
public enum AbsenceSource : byte
{
    Owner = 1,
    ClientLink = 2
}

/// <summary>Who cancelled a booking (RF-22).</summary>
public enum CancelledBy : byte
{
    Client = 1,
    Owner = 2
}

/// <summary>Estado de la inscripción a clases fijas (RF-05, RF-20, RF-29).</summary>
public enum EnrollmentStatus : byte
{
    /// <summary>Inscripción pendiente de pago: retains its fixed places (D-21).</summary>
    PendingPayment = 1,
    /// <summary>At least one membership period was paid.</summary>
    Active = 2,
    /// <summary>Pending enrollment annulled by the owner; places released.</summary>
    Annulled = 3,
    /// <summary>Baja: the owner ended the membership; places released, history kept.</summary>
    Terminated = 4
}

/// <summary>What a payment pays for; every payment references its concrete origin (D-20).</summary>
public enum PaymentConcept : byte
{
    IndividualAppointment = 1,
    WalkIn = 2,
    DropInClass = 3,
    Membership = 4
}

/// <summary>Purpose of a single-use client account token.</summary>
public enum AccountTokenPurpose : byte
{
    EmailVerification = 1,
    PasswordReset = 2
}

/// <summary>Every email the system can generate. Used for templates and deduplication keys.</summary>
public enum NotificationType : byte
{
    BookingCreated = 1,
    AppointmentReminder = 2,
    DropInReminder = 3,
    DropInBooked = 4,
    ClassSuspended = 5,
    Rescheduled = 6,
    MembershipExpiring = 7,
    MembershipExpired = 8,
    DailySummary = 9,
    StockReview = 10,
    EmailVerification = 11,
    PasswordReset = 12,
    AccountInvitation = 13
}

/// <summary>
/// Lifecycle of a queued notice. The dispatcher (T23) moves a row from <c>Pending</c> to <c>Dispatching</c> in the same
/// transaction that commits the claim, and only then calls the sender.
/// </summary>
public enum NotificationStatus : byte
{
    Pending = 1,
    Sent = 2,
    /// <summary>Terminal: a definite failure, or the retry budget exhausted. Never claimed again.</summary>
    Failed = 3,
    /// <summary>Claimed and being sent right now. Never reclaimed automatically: only a human resolves it.</summary>
    Dispatching = 4,
    /// <summary>Send outcome unknown; needs a manual decision. Never retried or reclaimed automatically.</summary>
    ReviewRequired = 5
}

/// <summary>Membership state on a given date: <c>Vigente</c> (Current) or <c>Vencido</c> (Expired).</summary>
public enum MembershipState : byte
{
    None = 0,
    Current = 1,
    Expired = 2
}
