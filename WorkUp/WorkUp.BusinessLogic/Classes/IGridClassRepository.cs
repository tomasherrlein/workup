using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Classes;

public interface IGridClassRepository
{
    /// <summary>All grid classes of the business, active and archived, ordered by weekday and start time.</summary>
    Task<List<GridClass>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    /// <summary>A grid class, or null when it does not exist or belongs to another business.</summary>
    Task<GridClass?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>
    /// Active grid classes of one worker on one weekday, used for the same-teacher overlap rule. Pass the class's
    /// own id when editing it so it does not overlap-check against itself.
    /// </summary>
    Task<List<GridClass>> ListActiveByWorkerAndDayAsync(int businessId, int workerId, DayOfWeek dayOfWeek, int? excludingId = null, CancellationToken ct = default);

    Task<int> InsertAsync(GridClass gridClass, CancellationToken ct = default);

    Task UpdateAsync(GridClass gridClass, CancellationToken ct = default);

    /// <summary>
    /// Whether the grid class has a fixed-class assignment valid today or later (EnrollmentFixedClasses.ValidTo IS
    /// NULL OR ValidTo &gt;= today). Memberships (T15, T17) own that table; queried directly here for now, the same
    /// way SqlServiceRepository.HasBookingHistoryAsync (T02) reached into Appointments and GridClasses before their
    /// own modules existed.
    /// </summary>
    Task<bool> HasActiveFixedAssignmentAsync(int businessId, int id, DateOnly today, CancellationToken ct = default);
}
