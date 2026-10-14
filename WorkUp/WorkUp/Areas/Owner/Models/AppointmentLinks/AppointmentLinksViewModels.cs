using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Areas.Owner.Models.AppointmentLinks;

public sealed record AppointmentLinksIndexViewModel(
    IReadOnlyList<OwnerPrivateAppointmentSummary> Bookings);

public sealed record AppointmentLinkCopyViewModel(
    string Url,
    DateTime ExpiresAt);
