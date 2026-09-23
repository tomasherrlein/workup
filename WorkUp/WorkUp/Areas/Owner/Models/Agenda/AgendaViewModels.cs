using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Areas.Owner.Models.Agenda;

public sealed record AgendaIndexViewModel(
    OwnerAgendaResult Agenda,
    OwnerAgendaPeriod Period,
    int? WorkerId,
    BookingStatus? BookingStatus,
    AppointmentOrigin? Origin);

public sealed record AgendaBookingDetailViewModel(OwnerAgendaBookingDetail Booking);
