using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

// T25 daily owner agenda summary core (RF-18). At the configured business time the owner receives the agenda of the
// next day: tomorrow's individual turns and class sessions with their still-active bookings. The recipient is the
// business notice email and nothing else, the key is once per business per target day, and the guarded enqueue
// rechecks the live rows so an edited recipient, an edited time or a repeated tick never produces a second or a stale
// email.
public class DailySummaryServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 12);
    private static readonly DateTime Start = new(2026, 3, 12, 21, 0, 0);

    private readonly FakeDailySummaryRepository _repository = new();
    private readonly DailySummaryService _service;

    public DailySummaryServiceTests() => _service = new DailySummaryService(_repository);

    private static DateTime Tomorrow(int hour, int minute = 0) => new(2026, 3, 13, hour, minute, 0);

    // ---- The configured hour: before is too early, equal and after both fire once ----

    [Fact]
    public async Task Before_the_configured_hour_nothing_is_enqueued()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(0, await _service.RunAsync(Start.AddMinutes(-1)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Exactly_at_the_configured_hour_the_summary_is_enqueued_once()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.DailySummary, notice.Type);
        Assert.Equal("summary:business:1:20260313", notice.DedupKey);
        Assert.Equal(Start, notice.CreatedAt);
        Assert.Equal(1, notice.BusinessId);
        Assert.Equal("duena@example.com", notice.Recipient);
        Assert.Contains(DailySummaryService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task After_the_configured_hour_the_summary_is_enqueued_once()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(1, await _service.RunAsync(Start.AddMinutes(30)));
        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal("summary:business:1:20260313", notice.DedupKey);
    }

    // ---- A missed minute is still served the same day, but midnight ends the opportunity ----

    [Fact]
    public async Task A_missed_minute_still_enqueues_later_the_same_day_for_the_same_target()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 3, 12, 21, 5, 0)));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal("summary:business:1:20260313", notice.DedupKey);
        Assert.Equal(new DateTime(2026, 3, 12, 21, 5, 0), notice.CreatedAt);
    }

    [Fact]
    public async Task A_repeated_tick_on_the_same_day_never_enqueues_a_second_summary()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(1, await _service.RunAsync(Start));
        Assert.Equal(0, await _service.RunAsync(Start.AddMinutes(1)));
        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 3, 12, 23, 59, 0)));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task Midnight_resets_the_opportunity_and_yesterday_is_never_backfilled()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(1, await _service.RunAsync(Start));
        // 00:30 of March 13 is before the 21:00 configured hour: the March 13 agenda is not summarized now, and the
        // missed March 12 opportunity is not resurrected. The window only reopens for the March 14 agenda at 21:00.
        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 3, 13, 0, 30, 0)));
        Assert.Single(_repository.Notifications);

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 3, 13, 21, 0, 0)));
        var keys = _repository.Notifications.Select(notice => notice.DedupKey).OrderBy(key => key).ToArray();
        Assert.Equal(["summary:business:1:20260313", "summary:business:1:20260314"], keys);
    }

    // ---- Agenda content: tomorrow only, individual and class sessions, active bookings only ----

    [Fact]
    public async Task An_empty_agenda_states_it_explicitly_instead_of_sending_an_empty_body()
    {
        _repository.AddBusiness(name: "Estudio");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Estudio", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mixed_individual_and_class_agenda_lists_both_with_their_day_names()
    {
        var business = _repository.AddBusiness(name: "Estudio Verde");
        _repository.AddIndividual(
            business, 1, Tomorrow(9, 30), serviceName: "Corte", workerName: "Sol Diaz", clientName: "Ana Perez");
        var yoga = _repository.AddClassSession(
            business, 2, Tomorrow(18), serviceName: "Yoga", workerName: "Mar Acosta");
        yoga.WithBooking(21, "Luz Gomez");
        yoga.WithBooking(22, "Eva Ruiz", BookingStatus.Confirmed);

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("09:30", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Corte", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Sol Diaz", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Ana Perez", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("18:00", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Yoga", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Mar Acosta", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Luz Gomez", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Eva Ruiz", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_next_day_is_reported_and_other_days_are_ignored()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, new DateTime(2026, 3, 12, 10, 0, 0), clientName: "Hoy");
        _repository.AddIndividual(business, 2, Tomorrow(9), clientName: "Manana");
        _repository.AddIndividual(business, 3, new DateTime(2026, 3, 14, 9, 0, 0), clientName: "Pasado");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Manana", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Hoy", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Pasado", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_bookings_are_omitted_from_the_summary()
    {
        var business = _repository.AddBusiness();
        var yoga = _repository.AddClassSession(business, 1, Tomorrow(18));
        yoga.WithBooking(11, "Ana Activa", BookingStatus.Pending);
        yoga.WithBooking(12, "Beto Cancelado", BookingStatus.Cancelled);

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Ana Activa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Cancelado", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attended_and_absent_bookings_are_not_part_of_tomorrow()
    {
        var business = _repository.AddBusiness();
        var yoga = _repository.AddClassSession(business, 1, Tomorrow(18));
        yoga.WithBooking(11, "Ana Activa", BookingStatus.Confirmed);
        yoga.WithBooking(12, "Beto Ausente", BookingStatus.Absent);
        yoga.WithBooking(13, "Carla Atendida", BookingStatus.Attended);

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Ana Activa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Ausente", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Carla Atendida", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suspended_appointment_is_omitted()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9), serviceName: "Corte", clientName: "Ana");
        _repository.AddClassSession(
            business, 2, Tomorrow(18), serviceName: "Yoga Suspendida", status: AppointmentStatus.Suspended);

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Corte", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Ana", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Yoga Suspendida", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_individual_turn_whose_only_booking_is_cancelled_is_dropped_from_the_agenda()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(
            business, 1, Tomorrow(9), serviceName: "Corte Individual", clientName: "Beto Cancelado",
            bookingStatus: BookingStatus.Cancelled);

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Corte Individual", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Cancelado", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scheduled_class_with_no_bookings_remains_a_planned_class_of_the_agenda()
    {
        var business = _repository.AddBusiness();
        _repository.AddClassSession(business, 1, Tomorrow(18), serviceName: "Yoga", workerName: "Mar Acosta");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Yoga", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Mar Acosta", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("sin anotaciones", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_individual_is_dropped_while_a_class_without_bookings_survives()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(
            business, 1, Tomorrow(9), serviceName: "Corte Individual", clientName: "Beto Cancelado",
            bookingStatus: BookingStatus.Cancelled);
        _repository.AddClassSession(business, 2, Tomorrow(18), serviceName: "Yoga");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Yoga", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Corte Individual", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Cancelado", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scan_itself_drops_an_individual_turn_with_no_active_booking()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(
            business, 1, Tomorrow(9), serviceName: "Corte Individual", clientName: "Beto Cancelado",
            bookingStatus: BookingStatus.Cancelled);
        _repository.AddClassSession(business, 2, Tomorrow(18), serviceName: "Yoga");

        var candidates = await _repository.ListCandidatesAsync(Start, 20);

        var candidate = Assert.Single(candidates);
        var appointment = Assert.Single(candidate.Appointments);
        Assert.Equal("Yoga", appointment.ServiceName);
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task Each_business_receives_only_its_own_agenda_and_recipient()
    {
        var first = _repository.AddBusiness(1, "Primero", "primero@example.com");
        _repository.AddIndividual(first, 1, Tomorrow(9), clientName: "Ana del Primero");
        var second = _repository.AddBusiness(2, "Segundo", "segundo@example.com");
        _repository.AddIndividual(second, 2, Tomorrow(10), clientName: "Beto del Segundo");

        Assert.Equal(2, await _service.RunAsync(Start));

        var firstNotice = Assert.Single(_repository.Notifications, n => n.BusinessId == 1);
        Assert.Equal("primero@example.com", firstNotice.Recipient);
        Assert.Equal("summary:business:1:20260313", firstNotice.DedupKey);
        Assert.Contains("Ana del Primero", firstNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto del Segundo", firstNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Primero", firstNotice.BodyHtml, StringComparison.Ordinal);

        var secondNotice = Assert.Single(_repository.Notifications, n => n.BusinessId == 2);
        Assert.Equal("segundo@example.com", secondNotice.Recipient);
        Assert.Equal("summary:business:2:20260313", secondNotice.DedupKey);
        Assert.Contains("Beto del Segundo", secondNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana del Primero", secondNotice.BodyHtml, StringComparison.Ordinal);
    }

    // ---- The guarded enqueue: an edit between the scan and the insert invalidates the notice ----

    [Fact]
    public async Task A_recipient_changed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = _repository.AddBusiness(recipient: "vieja@example.com");
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.AfterList = () => business.Recipient = "nueva@example.com";

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.EnqueueAttempts);
    }

    [Fact]
    public async Task A_configured_time_changed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.AfterList = () => business.ConfiguredTime = new TimeOnly(23, 0);

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.EnqueueAttempts);
    }

    [Fact]
    public async Task A_recipient_cleared_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.AfterList = () => business.Recipient = "";

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_concurrent_summary_committed_after_the_scan_is_not_duplicated()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        Notification? competing = null;
        _repository.AfterList = () => competing = _repository.SeedSummary(1, Today.AddDays(1), Start);

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Same(competing, Assert.Single(_repository.Notifications));
    }

    [Fact]
    public async Task A_business_removed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.AfterList = () => _repository.RemoveBusiness(1);

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    // ---- The notice email is the only destination; the phone is never a fallback ----

    [Fact]
    public async Task A_business_without_a_notice_email_is_skipped_and_never_falls_back_to_the_phone()
    {
        var business = _repository.AddBusiness(recipient: null, contactPhone: "+54 11 4444-1111");
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_malformed_notice_email_is_skipped_instead_of_being_used()
    {
        var business = _repository.AddBusiness(recipient: "no-es-un-email");
        _repository.AddIndividual(business, 1, Tomorrow(9));

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    // ---- HTML safety and the absence of private or financial payload ----

    [Fact]
    public async Task Business_worker_service_and_client_names_are_html_encoded()
    {
        var business = _repository.AddBusiness(name: "<i>Estudio</i>");
        var yoga = _repository.AddClassSession(
            business, 1, Tomorrow(18), serviceName: "Masaje & Spa", workerName: "<script>alert(1)</script>");
        yoga.WithBooking(11, "<b>Ana</b>");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("&lt;i&gt;Estudio&lt;/i&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Masaje &amp; Spa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ana</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>Estudio</i>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task The_summary_never_carries_private_links_payment_details_or_notes()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9), clientName: "Ana");

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("nota", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Bounded batch and a permissive reader that cannot force an ineligible write ----

    [Fact]
    public async Task The_batch_is_bounded_to_twenty_businesses_per_tick()
    {
        for (var index = 1; index <= 25; index++)
            _repository.AddBusiness(index, $"Negocio {index}", $"duena{index}@example.com");
        _repository.IgnoreBatchCap = true;

        Assert.Equal(DailySummaryService.BatchSize, await _service.RunAsync(Start));

        Assert.Equal(25, _repository.LastScanCandidateCount);
        Assert.Equal(DailySummaryService.BatchSize, _repository.Notifications.Count);
        Assert.Equal(DailySummaryService.BatchSize, _repository.LastBatchSize);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_summary_before_the_configured_hour()
    {
        var business = _repository.AddBusiness(configuredTime: new TimeOnly(23, 0));
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_summary_without_a_usable_recipient()
    {
        var business = _repository.AddBusiness(recipient: null);
        _repository.AddIndividual(business, 1, Tomorrow(9));
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    // ---- The service never reads a clock of its own ----

    [Fact]
    public async Task The_service_uses_the_supplied_instant_and_never_reads_a_clock()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        var supplied = new DateTime(2026, 3, 12, 22, 15, 0);

        Assert.Equal(1, await _service.RunAsync(supplied));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(supplied, notice.CreatedAt);
        Assert.Equal(DailySummaryService.DedupKey(1, Today.AddDays(1)), notice.DedupKey);
    }

    [Fact]
    public async Task The_service_defends_against_a_permissive_reader_that_over_returns_rows()
    {
        var business = _repository.AddBusiness();
        // A valid individual turn on the target day.
        _repository.AddIndividual(business, 1, Tomorrow(9), serviceName: "Turno Valido", clientName: "Ana Valida");
        // An individual turn whose only booking is cancelled: not part of the agenda.
        _repository.AddIndividual(
            business, 2, Tomorrow(10), serviceName: "Turno Cancelado", clientName: "Beto Cancelado",
            bookingStatus: BookingStatus.Cancelled);
        // A turn of today, not of the target day.
        _repository.AddIndividual(
            business, 3, new DateTime(2026, 3, 12, 10, 0, 0), serviceName: "Turno De Hoy", clientName: "Hoy");
        // A suspended class session.
        var suspended = _repository.AddClassSession(
            business, 4, Tomorrow(11), serviceName: "Clase Suspendida", status: AppointmentStatus.Suspended);
        suspended.WithBooking(41, "Suspension Activa");
        // A class session with one active and one cancelled booking.
        var mixed = _repository.AddClassSession(business, 5, Tomorrow(12), serviceName: "Clase Mixta");
        mixed.WithBooking(51, "Inscripta Activa", BookingStatus.Confirmed);
        mixed.WithBooking(52, "Inscripta Inactiva", BookingStatus.Cancelled);
        // A class session with no bookings at all.
        _repository.AddClassSession(business, 6, Tomorrow(13), serviceName: "Clase Vacia");
        _repository.OverReturnRows = true;

        Assert.Equal(1, await _service.RunAsync(Start));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Turno Valido", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Ana Valida", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Clase Mixta", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Inscripta Activa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Clase Vacia", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Turno Cancelado", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Cancelado", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Turno De Hoy", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Clase Suspendida", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Inscripta Inactiva", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);
    }

    // ---- The last representable calendar day has no next day ----

    [Fact]
    public async Task The_last_representable_calendar_day_has_no_summary_and_is_not_scanned()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, DateTime.MaxValue.Date, serviceName: "Corte", clientName: "Ana");

        Assert.Equal(0, await _service.RunAsync(DateTime.MaxValue));

        Assert.Equal(0, _repository.ListCount);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public void The_target_date_saturates_instead_of_overflowing_on_the_last_day()
    {
        Assert.Equal(DateOnly.MaxValue, DailySummaryService.TargetDate(DateTime.MaxValue));
        Assert.Equal(new DateOnly(2026, 3, 13), DailySummaryService.TargetDate(Start));
    }

    // ---- Cancellation propagates and never leaves a partial notice ----

    [Fact]
    public async Task Cancellation_before_the_run_propagates_and_enqueues_nothing()
    {
        _repository.AddBusiness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(Start, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Cancellation_after_the_scan_propagates_without_enqueuing()
    {
        var business = _repository.AddBusiness();
        _repository.AddIndividual(business, 1, Tomorrow(9));
        using var cancellation = new CancellationTokenSource();
        _repository.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(Start, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }
}
