namespace WorkUp.ViewModels.Catalog;

public sealed record ServiceRowViewModel(int Id, string Name, decimal Price, int DurationMinutes, int MaxCapacity, bool IsActive);
