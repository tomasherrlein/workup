using Microsoft.AspNetCore.Mvc;
using WorkUp.Data;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddWorkUp(builder.Configuration);
builder.Services.AddWorkUpAuthentication();

var app = builder.Build();

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
