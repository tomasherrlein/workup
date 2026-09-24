using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WorkUp.Infrastructure;

/// <summary>
/// Scoped decimal binder for amounts posted by HTML <c>type="number"</c> inputs.
/// </summary>
/// <remarks>
/// HTML number inputs always post the invariant dot form ("100.25"), but MVC's default decimal binder converts
/// with the request culture. Under comma-decimal cultures (es-AR) the dot is a group separator, so "100.25"
/// binds as 10025.00 -- a silent 100x money error. This binder parses the raw posted text with
/// <see cref="CultureInfo.InvariantCulture"/> and <see cref="NumberStyles.AllowLeadingSign"/> +
/// <see cref="NumberStyles.AllowDecimalPoint"/> only, so group separators are never accepted and an ambiguous
/// string ("100,25", "1.000,25", "1,000.25") is rejected with a model error instead of being transformed.
/// It is applied per field -- <c>[ModelBinder(typeof(InvariantDecimalModelBinder))]</c> -- never globally, so
/// unrelated forms keep the framework default.
/// </remarks>
public sealed class InvariantDecimalModelBinder : IModelBinder
{
    /// <summary>Sign and decimal point only: no thousands/group separator and no exponent.</summary>
    private const NumberStyles AmountStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (valueResult == ValueProviderResult.None)
        {
            // No value at all: leave the target untouched so an optional amount stays null without an error.
            return Task.CompletedTask;
        }

        // Keep the raw posted text so a rejected value is re-displayed and reported, never rewritten.
        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
        var raw = valueResult.FirstValue;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // An empty optional amount is absence, not zero.
            bindingContext.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        if (decimal.TryParse(raw, AmountStyles, CultureInfo.InvariantCulture, out var value))
        {
            bindingContext.Result = ModelBindingResult.Success(value);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(bindingContext.ModelName,
            bindingContext.ModelMetadata.ModelBindingMessageProvider.AttemptedValueIsInvalidAccessor(
                raw, bindingContext.ModelName));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Guarantees that a decimal rendered into an HTML <c>type="number"</c> input uses the invariant dot form.
/// </summary>
/// <remarks>
/// MVC's input tag helper renders the model value with the request culture ("100,25" under es-AR), which a
/// browser silently discards for a number input, so the field would come back empty on the next post. The tag
/// helper prefers a ModelState attempted value when one exists, so <see cref="Ensure"/> seeds the invariant
/// text into ModelState when there is no attempted value yet and keeps the raw posted text when there is.
/// </remarks>
public static class InvariantDecimalFormValue
{
    /// <summary>Seeds the invariant text for <paramref name="fieldName"/> if missing and returns the text to render.</summary>
    public static string Ensure(ModelStateDictionary modelState, string fieldName, decimal? value)
    {
        ArgumentNullException.ThrowIfNull(modelState);
        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        if (modelState.TryGetValue(fieldName, out var entry) && !string.IsNullOrEmpty(entry.AttemptedValue))
            return entry.AttemptedValue;

        // Nothing to render: leave ModelState untouched so an absent optional amount adds no state (and never
        // makes ModelState.Invalid, which the Jornada uses to decide whether to keep the form open).
        if (value is not { } amount)
            return string.Empty;

        var text = amount.ToString(CultureInfo.InvariantCulture);
        modelState.SetModelValue(fieldName, new ValueProviderResult(text, CultureInfo.InvariantCulture));
        return text;
    }
}
