namespace WorkUp.ViewModels.Classes;

public sealed record GridClassRowViewModel(int Id, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, string ServiceName, string TeacherName, int Capacity, bool IsActive);
