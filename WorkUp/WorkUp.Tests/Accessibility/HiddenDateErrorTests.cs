namespace WorkUp.Tests.Accessibility;

// T27 D1 (real HTTP): the booking and reprogramming forms render the chosen day as a hidden input with no
// validation span next to it, and their summary is asp-validation-summary="ModelOnly". ModelOnly renders only the
// entries keyed by string.Empty, so an error on the hidden day used to answer 200 with a rebuilt form, no write and
// no message at all: nothing on screen told the owner or the client what was missing.
//
// The controllers now add a model-level companion for every hidden selector error. These source-level assertions pin
// the two view facts that make that companion necessary (the hidden day has no span and the summary is ModelOnly) and
// the announcement hook that makes it perceivable (role="alert" on the summary element itself, which is the live
// region that turns visible once the summary has an entry). They deliberately stay at source level: they prove the
// attributes the Razor views emit, never the computed accessibility tree, and the real HTTP/DOM smoke with a database
// is a separate opt-in harness that is not recreated here.
public sealed class HiddenDateErrorTests
{
    [Theory]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/NewBooking.cshtml")]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/Reschedule.cshtml")]
    [InlineData("WorkUp/Views/ClientReservations/Individual.cshtml")]
    [InlineData("WorkUp/Views/ClientAppointments/Reschedule.cshtml")]
    public void The_error_summary_of_the_hidden_day_is_a_model_level_alert(string relativePath)
    {
        var summary = TagContaining(ReadSource(relativePath), "asp-validation-summary=");

        // ModelOnly: the summary is the only place a model-level entry shows up, and the entry the controller adds
        // is invisible unless this tag exists as a live region.
        Assert.Contains("asp-validation-summary=\"ModelOnly\"", summary);
        Assert.Contains("role=\"alert\"", summary);
    }

    [Theory]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/NewBooking.cshtml")]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/Reschedule.cshtml")]
    [InlineData("WorkUp/Views/ClientReservations/Individual.cshtml")]
    [InlineData("WorkUp/Views/ClientAppointments/Reschedule.cshtml")]
    public void The_hidden_day_is_rendered_without_a_validation_span(string relativePath)
    {
        var view = ReadSource(relativePath);
        var hiddenDay = TagContaining(view, "asp-for=\"Input.Date\"");

        Assert.Contains("type=\"hidden\"", hiddenDay);
        // No span can ever show a field error for the hidden day, which is exactly why the controller has to lift it
        // into the summary instead.
        Assert.DoesNotContain("asp-validation-for=\"Input.Date\"", view);
    }

    [Theory]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/NewBooking.cshtml")]
    [InlineData("WorkUp/Areas/Owner/Views/Agenda/Reschedule.cshtml")]
    [InlineData("WorkUp/Views/ClientReservations/Individual.cshtml")]
    [InlineData("WorkUp/Views/ClientAppointments/Reschedule.cshtml")]
    public void The_error_summary_is_never_widened_to_every_field(string relativePath)
    {
        // All would mirror every field error, including the ones that already have their own span. The lift stays
        // narrow: only a hidden selector entry is copied into the summary.
        Assert.DoesNotContain("asp-validation-summary=\"All\"", ReadSource(relativePath));
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string TagContaining(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"La fuente debe contener {marker}.");
        var start = source.LastIndexOf('<', index);
        Assert.True(start >= 0, $"El marcador {marker} debe estar dentro de una etiqueta.");
        var end = source.IndexOf('>', index);
        Assert.True(end > start, $"La etiqueta que contiene {marker} debe cerrar.");
        return source[start..(end + 1)];
    }

    // Mirrors the view-source convention of the rest of the suite: the resolved root is the solution folder.
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
