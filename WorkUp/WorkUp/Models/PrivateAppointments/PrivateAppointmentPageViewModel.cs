using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Models.PrivateAppointments;

/// <summary>Public page projection containing only the exact linked appointment and available actions.</summary>
public sealed record PrivateAppointmentPageViewModel(PrivateAppointmentView? Appointment);
