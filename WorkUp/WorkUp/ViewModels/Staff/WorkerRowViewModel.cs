namespace WorkUp.ViewModels.Staff;

public sealed record WorkerRowViewModel(int Id, string FullName, string MaskedPhone, string ScheduleSummary, bool IsActive);
