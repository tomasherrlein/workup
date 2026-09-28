using WorkUp.BusinessLogic.Catalog;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Catalog;

// RF-02: services and class types are unique per business by name, priced above zero, with a positive duration and
// a max capacity of at least 1. They are archived, never deleted, so their history and the individual/group kind
// they already committed to stay intact.
public class CatalogServiceTests
{
    private readonly FakeServiceRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly CatalogService _service;

    public CatalogServiceTests()
    {
        _service = new CatalogService(_repository, _unitOfWork);
    }

    [Fact]
    public async Task Creates_an_individual_service_for_the_business()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        Assert.True(result.Succeeded);
        Assert.Equal("Corte", result.Value.Name);
        Assert.Equal(5000m, result.Value.Price);
        Assert.Equal(30, result.Value.DurationMinutes);
        Assert.Equal(1, result.Value.MaxCapacity);
        Assert.True(result.Value.IsActive);
        Assert.Equal(1, result.Value.BusinessId);
    }

    [Fact]
    public async Task Creates_a_class_service_with_capacity_greater_than_one()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));

        Assert.True(result.Succeeded);
        Assert.Equal(12, result.Value.MaxCapacity);
    }

    [Fact]
    public async Task Fails_when_the_name_is_blank()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("   ", 5000m, 30, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("service_name_required", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_name_is_already_used_by_the_same_business()
    {
        await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 6000m, 45, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("service_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Allows_the_same_name_in_a_different_business()
    {
        await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.CreateAsync(2, new CreateServiceRequest("Corte", 5000m, 30, 1));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_when_the_price_is_not_greater_than_zero()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 0m, 30, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_service_price", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_duration_is_not_greater_than_zero()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 0, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_service_duration", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_max_capacity_is_less_than_one()
    {
        var result = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 0));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_service_max_capacity", result.Error!.Code);
    }

    [Fact]
    public async Task Edits_an_existing_service()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Corte y barba", 6000m, 45, 1));

        Assert.True(result.Succeeded);
        var service = await _service.GetServiceAsync(1, created.Value.Id);
        Assert.Equal("Corte y barba", service!.Name);
        Assert.Equal(6000m, service.Price);
        Assert.Equal(45, service.DurationMinutes);
    }

    [Fact]
    public async Task Fails_to_edit_when_the_service_does_not_belong_to_the_business()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.UpdateAsync(2, created.Value.Id, new UpdateServiceRequest("Otro", 6000m, 45, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("service_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_edit_with_a_name_taken_by_another_service_of_the_same_business()
    {
        await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));
        var second = await _service.CreateAsync(1, new CreateServiceRequest("Peinado", 4000m, 20, 1));

        var result = await _service.UpdateAsync(1, second.Value.Id, new UpdateServiceRequest("Corte", 4000m, 20, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("service_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Keeping_its_own_name_while_editing_other_fields_does_not_fail_as_taken()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Corte", 5500m, 30, 1));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Cannot_switch_a_booked_service_from_individual_to_group()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));
        _repository.SeedBookingHistory(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Corte", 5000m, 30, 4));

        Assert.False(result.Succeeded);
        Assert.Equal("service_capacity_kind_locked", result.Error!.Code);
    }

    [Fact]
    public async Task Cannot_switch_a_booked_class_from_group_to_individual()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.SeedBookingHistory(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Yoga", 3000m, 60, 1));

        Assert.False(result.Succeeded);
        Assert.Equal("service_capacity_kind_locked", result.Error!.Code);
    }

    [Fact]
    public async Task Can_change_the_capacity_of_a_booked_service_without_switching_kind()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.SeedBookingHistory(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Yoga", 3000m, 60, 15));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Can_switch_kind_freely_when_the_service_has_no_booking_history()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateServiceRequest("Corte", 5000m, 30, 4));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Archiving_never_removes_the_service_from_the_list()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        var service = Assert.Single(list);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task An_archived_service_can_be_reactivated()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        Assert.True(list.Single().IsActive);
    }

    [Fact]
    public async Task Fails_to_archive_when_the_service_does_not_belong_to_the_business()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        var result = await _service.SetActiveAsync(2, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("service_not_found", result.Error!.Code);
    }

    // T27 (hueco de T04, RF-02/RF-04): archiving a service that an active grid class still uses would leave that
    // class without a valid class type, so the archive is rejected and the stored row keeps IsActive unchanged.
    [Fact]
    public async Task Cannot_archive_a_service_that_an_active_grid_class_still_uses()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.SeedGridClass(1, created.Value.Id);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("service_has_active_grid_class", result.Error!.Code);
        Assert.Equal(
            "Este servicio lo usa una clase activa de la grilla: para archivarlo, primero archivá esa clase.",
            result.Error.Message);
        var stored = await _service.GetServiceAsync(1, created.Value.Id);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task An_archived_grid_class_does_not_block_archiving_the_service()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.SeedGridClass(1, created.Value.Id, isActive: false);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var stored = await _service.GetServiceAsync(1, created.Value.Id);
        Assert.False(stored!.IsActive);
    }

    [Fact]
    public async Task A_grid_class_of_another_business_does_not_block_archiving_the_service()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.SeedGridClass(2, created.Value.Id);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var stored = await _service.GetServiceAsync(1, created.Value.Id);
        Assert.False(stored!.IsActive);
    }

    [Fact]
    public async Task Reactivating_a_service_that_an_active_grid_class_uses_stays_allowed()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);
        _repository.SeedGridClass(1, created.Value.Id);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        var stored = await _service.GetServiceAsync(1, created.Value.Id);
        Assert.True(stored!.IsActive);
    }

    // T27 (cierre del hueco de T04): the guard alone is not a race boundary. The archive takes the service row lock
    // before the guard and the write, so a competing grid-class create that committed just before the lock is seen by
    // the guard, and a rejection rolls back without ever touching IsActive.
    [Fact]
    public async Task Archiving_sees_a_grid_class_created_by_a_competing_writer_inside_the_lock_window()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        _repository.OnLockForGridClassWrite = _ => _repository.SeedGridClass(1, created.Value.Id);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("service_has_active_grid_class", result.Error!.Code);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.True((await _service.GetServiceAsync(1, created.Value.Id))!.IsActive);
    }

    [Fact]
    public async Task Archiving_an_unused_service_locks_the_row_then_writes_in_one_transaction()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        var locked = false;
        _repository.OnLockForGridClassWrite = _ => locked = true;

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        Assert.True(locked, "the archive took the row lock before the guard and the write");
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
        Assert.False((await _service.GetServiceAsync(1, created.Value.Id))!.IsActive);
    }

    // Triangulation: an active class type cannot break a class, so reactivation stays free of the archive lock and of
    // its transaction. Only the archive branch pays for the serialization it needs.
    [Fact]
    public async Task Reactivating_a_service_does_not_take_the_archive_lock_or_open_a_transaction()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);
        var beginsBefore = _unitOfWork.BeginCount;
        var lockCount = 0;
        _repository.OnLockForGridClassWrite = _ => lockCount++;

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        Assert.Equal(0, lockCount);
        Assert.Equal(beginsBefore, _unitOfWork.BeginCount);
        Assert.True((await _service.GetServiceAsync(1, created.Value.Id))!.IsActive);
    }

    [Fact]
    public async Task Archiving_a_service_that_does_not_exist_reports_it_and_rolls_back_the_lock_transaction()
    {
        var result = await _service.SetActiveAsync(1, 999, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("service_not_found", result.Error!.Code);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Has_no_group_services_when_there_are_none()
    {
        Assert.False(await _service.HasGroupServicesAsync(1));
    }

    [Fact]
    public async Task Has_no_group_services_when_every_service_is_individual()
    {
        await _service.CreateAsync(1, new CreateServiceRequest("Corte", 5000m, 30, 1));

        Assert.False(await _service.HasGroupServicesAsync(1));
    }

    [Fact]
    public async Task Has_group_services_when_an_active_class_exists()
    {
        await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));

        Assert.True(await _service.HasGroupServicesAsync(1));
    }

    [Fact]
    public async Task Does_not_count_an_archived_class_as_a_group_service()
    {
        var created = await _service.CreateAsync(1, new CreateServiceRequest("Yoga", 3000m, 60, 12));
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.False(await _service.HasGroupServicesAsync(1));
    }
}
