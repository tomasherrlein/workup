using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.DemoData;
using WorkUp.Data;
using WorkUp.Data.DemoData;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;

var commandLine = DemoSeedCommandLine.Parse(args);
if (!commandLine.IsValid)
{
    var flagProblem = commandLine.ErrorCode switch
    {
        "duplicate_command_flag" => "the --seed-demo-data flag may appear only once",
        _ => "--seed-demo-data must be passed without a value"
    };
    Console.Error.WriteLine($"Demo data seeding rejected: {flagProblem}.");
    Environment.ExitCode = 2;
    return;
}

var seedDemoData = commandLine.SeedRequested;
var demoPassword = seedDemoData ? Environment.GetEnvironmentVariable("WORKUP_DEMO_PASSWORD") : null;
var builder = WebApplication.CreateBuilder(commandLine.BuilderArguments);
if (seedDemoData)
{
    var gate = DemoSeedGate.Evaluate(
        hasCommandFlag: seedDemoData,
        environmentName: builder.Environment.EnvironmentName,
        enabled: builder.Configuration.GetValue<bool>("WorkUp:DemoData:Enabled"),
        password: demoPassword);
    if (!gate.IsAllowed)
    {
        var requirement = gate.ErrorCode switch
        {
            "development_only" => "the Development environment",
            "demo_configuration_required" => "WorkUp:DemoData:Enabled=true",
            "password_required" => "WORKUP_DEMO_PASSWORD in the process environment",
            _ => "the --seed-demo-data command-line flag"
        };
        Console.Error.WriteLine($"Demo data seeding rejected: {requirement} is required.");
        Environment.ExitCode = 2;
        return;
    }

    var target = DemoSeedDatabaseTarget.Evaluate(builder.Configuration.GetConnectionString("WorkUp"));
    if (!target.IsAllowed)
    {
        Console.Error.WriteLine("Demo data seeding rejected: use the (localdb)\\MSSQLLocalDB instance and a dedicated WorkUpDemo* or WorkUpT07Demo* database.");
        Environment.ExitCode = 2;
        return;
    }
}

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddWorkUp(builder.Configuration, builder.Environment);
builder.Services.AddWorkUpAuthentication();

var app = builder.Build();

if (seedDemoData)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(demoPassword!);
        Console.WriteLine($"Demo data seed complete: {outcome.CreatedBusinesses} created, {outcome.AlreadySeededBusinesses} already seeded.");
        Environment.ExitCode = 0;
    }
    catch (DemoSeedSafetyException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 2;
    }
    catch (DemoSeedConflictException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Demo data seed failed; the transaction was rolled back where possible. Error type: {ErrorType}", ex.GetType().Name);
        Environment.ExitCode = 1;
    }

    return;
}

// Creates the database and applies pending scripts, so a teammate only needs a connection string to run the app.
if (app.Configuration.GetValue("WorkUp:MigrateOnStartup", true))
{
    var applied = await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    foreach (var script in applied)
        app.Logger.LogInformation("Applied database script {Script}", script);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

// Owner panel: /panel/{controller}/{action}. Public pages use attribute routes (/r/{slug}, /t/{token}).
app.MapAreaControllerRoute(
        name: "owner",
        areaName: "Owner",
        pattern: "panel/{controller=Day}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
