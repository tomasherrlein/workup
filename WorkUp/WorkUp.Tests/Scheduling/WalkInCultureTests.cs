using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Localization;
using WorkUp.Areas.Owner.Models.Day;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// T12 HTTP regression: the walk-in form rendered HTTP 500 on a real host because
/// <c>[Range(typeof(decimal), "0.01", "9999999999.99")]</c> makes <see cref="RangeAttribute"/> convert its
/// textual bounds with <see cref="CultureInfo.CurrentCulture"/>. Under comma-decimal cultures such as es-AR
/// the dotted bounds are unparseable, so materializing the DataAnnotations client validator throws while
/// rendering <c>_WalkInForm</c>. These tests pin the es-AR behavior of <see cref="WalkInViewModel.AgreedPrice"/>:
/// optional, in-range, out-of-range and decimal-precision cases, plus the MVC client-validator path.
/// </summary>
public sealed class WalkInCultureTests
{
    private const string Min = "0.01";
    private const string Max = "9999999999.99";

    [Fact]
    public void Optional_price_is_valid_under_es_ar()
    {
        var invalid = UnderEsAr(() => InvalidMembers(new WalkInViewModel
        {
            ClientId = 1, ServiceId = 2, WorkerId = 3, AgreedPrice = null
        }));

        Assert.DoesNotContain(nameof(WalkInViewModel.AgreedPrice), invalid);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("0.1")]
    [InlineData("1500.5")]
    [InlineData("1500.50")]
    [InlineData("9999999999.99")]
    public void In_range_prices_are_valid_under_es_ar(string price)
    {
        var invalid = UnderEsAr(() => InvalidMembers(new WalkInViewModel
        {
            ClientId = 1, ServiceId = 2, WorkerId = 3, AgreedPrice = Invariant(price)
        }));

        Assert.DoesNotContain(nameof(WalkInViewModel.AgreedPrice), invalid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("9999999999.991")]
    public void Out_of_range_prices_are_invalid_under_es_ar(string price)
    {
        var invalid = UnderEsAr(() => InvalidMembers(new WalkInViewModel
        {
            ClientId = 1, ServiceId = 2, WorkerId = 3, AgreedPrice = Invariant(price)
        }));

        Assert.Contains(nameof(WalkInViewModel.AgreedPrice), invalid);
    }

    [Fact]
    public void Precision_boundaries_are_compared_as_decimal_not_double()
    {
        // These two differ from the exact bounds only beyond double's precision, so a double-based bound check
        // would accept them as 0.01 / 9999999999.99; an exact decimal check must not. Their immediate decimal
        // neighbours stay valid, so the test does not merely assert that everything fails.
        var justBelowMinimum = UnderEsAr(() => InvalidMembers(ViewModel(Invariant("0.009999999999999999999"))));
        var justAboveMaximum = UnderEsAr(() => InvalidMembers(ViewModel(Invariant("9999999999.990000000000000001"))));
        var justAboveMinimum = UnderEsAr(() => InvalidMembers(ViewModel(Invariant("0.010000000000000000001"))));
        var justBelowMaximum = UnderEsAr(() => InvalidMembers(ViewModel(Invariant("9999999999.989999999999999999"))));

        Assert.Contains(nameof(WalkInViewModel.AgreedPrice), justBelowMinimum);
        Assert.Contains(nameof(WalkInViewModel.AgreedPrice), justAboveMaximum);
        Assert.DoesNotContain(nameof(WalkInViewModel.AgreedPrice), justAboveMinimum);
        Assert.DoesNotContain(nameof(WalkInViewModel.AgreedPrice), justBelowMaximum);
    }

    [Fact]
    public void Mvc_client_validator_rules_materialize_under_es_ar()
    {
        var attribute = Assert.IsAssignableFrom<ValidationAttribute>(typeof(WalkInViewModel)
            .GetProperty(nameof(WalkInViewModel.AgreedPrice))!
            .GetCustomAttributes(typeof(ValidationAttribute), inherit: false)
            .Single());
        var metadataProvider = new DefaultModelMetadataProvider(new NoopMetadataDetailsProvider());
        var context = new ClientModelValidationContext(new ActionContext(),
            metadataProvider.GetMetadataForType(typeof(decimal?)), metadataProvider,
            new Dictionary<string, string>());

        // Materializing the adapter is the exact frame that threw while rendering _WalkInForm.
        var adapter = UnderEsAr(() => new ValidationAttributeAdapterProvider()
            .GetAttributeAdapter(attribute, new FakeStringLocalizer()));
        var validator = Assert.IsAssignableFrom<IClientModelValidator>(adapter);
        UnderEsAr(() =>
        {
            validator.AddValidation(context);
            return true;
        });

        var rendered = string.Join(", ", context.Attributes.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.True(context.Attributes.TryGetValue("data-val-range-min", out var min), rendered);
        Assert.True(context.Attributes.TryGetValue("data-val-range-max", out var max), rendered);
        Assert.Equal(Min, min);
        Assert.Equal(Max, max);
    }

    [Fact]
    public void Validation_outcome_is_the_same_under_es_ar_and_the_invariant_culture()
    {
        foreach (var price in new[] { Min, "1500.50", Max, "0", "-1", "0.009999999999999999999" })
        {
            var invariant = UnderCulture(CultureInfo.InvariantCulture, () => InvalidMembers(ViewModel(Invariant(price))));
            var esAr = UnderEsAr(() => InvalidMembers(ViewModel(Invariant(price))));

            Assert.Equal(invariant.Contains(nameof(WalkInViewModel.AgreedPrice)),
                esAr.Contains(nameof(WalkInViewModel.AgreedPrice)));
        }
    }

    [Fact]
    public void Default_error_message_reports_the_parsed_bounds_for_the_current_culture()
    {
        // Without a custom ErrorMessage the base message is built from the parsed bounds, which is a second
        // culture-dependent path (FormatErrorMessage) of the same defect: it must not throw under es-AR, and the
        // displayed bounds stay localized (comma) while the parsing itself stays invariant.
        var attribute = new InvariantDecimalRangeAttribute(Min, Max);

        var esAr = UnderEsAr(() => attribute.FormatErrorMessage(nameof(WalkInViewModel.AgreedPrice)));
        var invariant = UnderCulture(CultureInfo.InvariantCulture,
            () => attribute.FormatErrorMessage(nameof(WalkInViewModel.AgreedPrice)));

        Assert.Contains("0,01", esAr);
        Assert.Contains("9999999999,99", esAr);
        Assert.Contains(Min, invariant);
        Assert.Contains(Max, invariant);
    }

    private static decimal Invariant(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static WalkInViewModel ViewModel(decimal? price) =>
        new() { ClientId = 1, ServiceId = 2, WorkerId = 3, AgreedPrice = price };

    private static IReadOnlyCollection<string> InvalidMembers(WalkInViewModel model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results.SelectMany(result => result.MemberNames).Distinct().ToList();
    }

    private static T UnderEsAr<T>(Func<T> action) => UnderCulture(CultureInfo.GetCultureInfo("es-AR"), action);

    private static T UnderCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try { return action(); }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private sealed class FakeStringLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => new(name, name, resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    /// <summary>Minimal composite details provider: these tests only need metadata identity, not MVC details.</summary>
    private sealed class NoopMetadataDetailsProvider : ICompositeMetadataDetailsProvider
    {
        public void CreateBindingMetadata(BindingMetadataProviderContext context) { }

        public void CreateDisplayMetadata(DisplayMetadataProviderContext context) { }

        public void CreateValidationMetadata(ValidationMetadataProviderContext context) { }
    }
}
