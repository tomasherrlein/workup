using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WorkUp.Business.Common;

namespace WorkUp.Infrastructure;

public enum FlashType
{
    Success,
    Info,
    Warning,
    Error
}

public static class ControllerExtensions
{
    /// <summary>Shows a business rule violation next to its field, or in the form summary when it has none.</summary>
    public static void AddBusinessError(this ModelStateDictionary modelState, BusinessError error) =>
        modelState.AddModelError(error.Field ?? string.Empty, error.Message);

    /// <summary>
    /// One-time message rendered by _Flash after a redirect. It complements, never replaces, the visible state
    /// change on the page.
    /// </summary>
    public static void Flash(this Controller controller, string message, FlashType type = FlashType.Success)
    {
        controller.TempData["FlashMessage"] = message;
        controller.TempData["FlashType"] = type.ToString();
    }
}
