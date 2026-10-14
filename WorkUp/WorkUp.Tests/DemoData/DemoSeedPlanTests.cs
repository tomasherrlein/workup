using System.Text.Json;
using WorkUp.BusinessLogic.DemoData;

namespace WorkUp.Tests.DemoData;

public class DemoSeedPlanTests
{
    [Theory]
    [InlineData(false, "Development", true, true, "command_flag_required")]
    [InlineData(true, "Production", true, true, "development_only")]
    [InlineData(true, "Development", false, true, "demo_configuration_required")]
    [InlineData(true, "Development", true, false, "password_required")]
    public void Seed_gate_rejects_each_missing_opt_in(
        bool hasCommandFlag,
        string environmentName,
        bool enabled,
        bool passwordProvided,
        string expectedError)
    {
        var password = passwordProvided ? new string('x', 24) : null;
        var result = DemoSeedGate.Evaluate(hasCommandFlag, environmentName, enabled, password);

        Assert.False(result.IsAllowed);
        Assert.Equal(expectedError, result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Seed_gate_rejects_a_missing_or_blank_environment_password(string? password)
    {
        var result = DemoSeedGate.Evaluate(true, "Development", true, password);

        Assert.False(result.IsAllowed);
        Assert.Equal("password_required", result.ErrorCode);
    }

    [Fact]
    public void Seed_gate_allows_only_the_fully_opted_in_development_run()
    {
        var result = DemoSeedGate.Evaluate(true, "Development", true, new string('x', 24));

        Assert.True(result.IsAllowed);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Seed_argument_is_removed_before_web_builder_receives_arguments()
    {
        var parsed = DemoSeedCommandLine.Parse(["--environment", "Development", "--seed-demo-data"]);

        Assert.True(parsed.IsValid);
        Assert.True(parsed.SeedRequested);
        Assert.Equal(["--environment", "Development"], parsed.BuilderArguments);
    }

    [Fact]
    public void Duplicate_seed_flags_are_rejected()
    {
        var parsed = DemoSeedCommandLine.Parse(["--seed-demo-data", "--seed-demo-data"]);

        Assert.False(parsed.IsValid);
        Assert.Equal("duplicate_command_flag", parsed.ErrorCode);
    }

    [Theory]
    [InlineData("--seed-demo-data=true")]
    [InlineData("--seed-demo-dat")]
    public void Malformed_seed_flags_are_rejected(string argument)
    {
        var parsed = DemoSeedCommandLine.Parse([argument]);

        Assert.False(parsed.IsValid);
        Assert.Equal("malformed_command_flag", parsed.ErrorCode);
    }

    [Fact]
    public void Normal_arguments_pass_through_when_seed_flag_is_absent()
    {
        var parsed = DemoSeedCommandLine.Parse(["--environment", "Development"]);

        Assert.True(parsed.IsValid);
        Assert.False(parsed.SeedRequested);
        Assert.Equal(["--environment", "Development"], parsed.BuilderArguments);
    }

    [Theory]
    [InlineData("WorkUpDemoT07Test")]
    [InlineData("WorkUpT07DemoSeedRun")]
    public void Dedicated_demo_localdb_target_is_allowed(string databaseName)
    {
        var result = DemoSeedDatabaseTarget.Evaluate(
            $"Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog={databaseName};Integrated Security=True;TrustServerCertificate=True");

        Assert.True(result.IsAllowed);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=WorkUp;Integrated Security=True")]
    [InlineData("Data Source=localhost;Initial Catalog=WorkUpDemoTest;Integrated Security=True")]
    [InlineData("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=Other;Integrated Security=True")]
    [InlineData("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=WorkUpDemoTest;Integrated Security=True;User ID=placeholder")]
    public void Non_dedicated_or_credentialed_database_targets_are_rejected(string connectionString)
    {
        var result = DemoSeedDatabaseTarget.Evaluate(connectionString);

        Assert.False(result.IsAllowed);
        Assert.Equal("dedicated_localdb_required", result.ErrorCode);
    }

    [Fact]
    public void Multiset_comparison_accepts_reordering_but_rejects_duplicate_rows()
    {
        var first = new Snapshot("worker-1", 1);
        var second = new Snapshot("worker-2", 2);

        Assert.True(DemoSeedMultiset.HasSameRows([first, second], [second, first]));
        Assert.False(DemoSeedMultiset.HasSameRows([first, first], [first, second]));
        Assert.False(DemoSeedMultiset.HasSameRows([first, first], [first]));
    }

    [Fact]
    public void Plan_is_deterministic_and_contains_safe_fictional_business_data()
    {
        var first = DemoSeedPlan.Create();
        var second = DemoSeedPlan.Create();

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(2, first.Count);
        Assert.All(first, business =>
        {
            Assert.Equal(2, business.Workers.Count);
            Assert.EndsWith(".invalid", business.NotificationEmail);
            Assert.All(business.Workers, worker => Assert.NotEmpty(worker.Schedules));
            Assert.All(business.Services, service =>
            {
                Assert.True(service.Price > 0);
                Assert.True(service.DurationMinutes > 0);
            });
        });

        var salon = Assert.Single(first, business => business.Slug == "peluqueria-marina-demo");
        var yoga = Assert.Single(first, business => business.Slug == "estudio-yoga-lucia-demo");
        Assert.All(salon.Services, service => Assert.Equal(1, service.MaxCapacity));
        Assert.All(yoga.Services, service => Assert.True(service.MaxCapacity > 1));
        Assert.NotEmpty(yoga.GridClasses);
        Assert.Empty(salon.GridClasses);
        Assert.All(yoga.GridClasses, gridClass =>
        {
            Assert.Contains(yoga.Services, service => service.Key == gridClass.ServiceKey && service.MaxCapacity > 1);
            Assert.Contains(yoga.Workers, worker => worker.Key == gridClass.WorkerKey);
            var worker = yoga.Workers.Single(candidate => candidate.Key == gridClass.WorkerKey);
            var service = yoga.Services.Single(candidate => candidate.Key == gridClass.ServiceKey);
            var schedule = Assert.Single(worker.Schedules, item => item.DayOfWeek == gridClass.DayOfWeek);
            Assert.InRange(gridClass.StartTime, schedule.StartTime, schedule.EndTime.AddMinutes(-service.DurationMinutes));
        });
    }

    private sealed record Snapshot(string Key, int Value);
}
