using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Areas.Owner.Models.Day;

public sealed record DayIndexViewModel(OwnerDayResult Day, DateTime Now);
