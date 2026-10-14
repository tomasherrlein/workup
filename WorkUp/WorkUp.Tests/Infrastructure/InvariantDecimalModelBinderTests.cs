using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.Infrastructure;

namespace WorkUp.Tests.Infrastructure;

/// <summary>
/// T12 amount-binding regression: an HTML <c>type="number"</c> posts the invariant dot form ("100.25").
/// The default MVC decimal binder honors the request culture, so under es-AR the dot is a group separator
/// and "100.25" binds as 10025.00 -- a silent 100x financial error. <see cref="WalkInViewModel.AgreedPrice"/>
/// and the owner Day <c>Finish</c>/<c>Charge</c> amounts must parse invariantly, reject ambiguous text and
/// keep optional empty amounts as <c>null</c>.
/// </summary>
public sealed class InvariantDecimalModelBinderTests
{
    private const string Field = nameof(WalkInViewModel.AgreedPrice);

    [Theory]
    [InlineData("100.25", "100.25")]
    [InlineData("0.01", "0.01")]
    [InlineData("1500.5", "1500.5")]
    [InlineData("9999999999.99", "9999999999.99")]
    [InlineData("-0.5", "-0.5")]
    public async Task Walk_in_price_posts_a_dot_value_and_binds_it_exactly_under_es_ar(string posted, string expected)
    {
        var context = await BindWalkInPriceAsync(posted);

        Assert.True(context.Result.IsModelSet, "The posted dot value must bind.");
        Assert.Equal(Invariant(expected), Assert.IsType<decimal>(context.Result.Model));
        // The raw posted text is retained so a rejected value can be re-displayed exactly as typed.
        Assert.Equal(posted, context.ModelState[Field]!.AttemptedValue);
    }

    [Theory]
    [InlineData("1,000.25")] // dot decimal with comma grouping
    [InlineData("100,25")] // comma used as the decimal separator
    [InlineData("1.000,25")] // es-AR grouping/decimal form
    [InlineData("100.25.5")]
    [InlineData("1e3")] // exponent is not accepted for money
    [InlineData("abc")]
    public async Task Ambiguous_amounts_are_rejected_instead_of_being_transformed_under_es_ar(string posted)
    {
        var context = await BindWalkInPriceAsync(posted);

        Assert.False(context.Result.IsModelSet, "An ambiguous amount must never be transformed into a model value.");
        Assert.False(context.ModelState.IsValid);
        var error = Assert.Single(context.ModelState[Field]!.Errors);
        Assert.Contains(posted, error.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(posted, context.ModelState[Field]!.AttemptedValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_optional_amount_binds_as_absent_null_without_error(string posted)
    {
        var context = await BindWalkInPriceAsync(posted);

        Assert.True(context.Result.IsModelSet);
        Assert.Null(context.Result.Model);
        // No error is added; the entry is left for the validation pipeline to mark valid.
        Assert.Empty(context.ModelState[Field]!.Errors);
    }

    [Fact]
    public async Task Missing_amount_leaves_the_target_unset_without_error()
    {
        var context = await BindWalkInPriceAsync(posted: null);

        Assert.False(context.Result.IsModelSet);
        Assert.True(context.ModelState.IsValid);
    }

    [Fact]
    public void Walk_in_price_declares_the_invariant_binder()
    {
        var attribute = typeof(WalkInViewModel).GetProperty(nameof(WalkInViewModel.AgreedPrice))!
            .GetCustomAttribute<ModelBinderAttribute>();

        Assert.Equal(typeof(InvariantDecimalModelBinder), attribute?.BinderType);
    }

    [Theory]
    [InlineData(nameof(DayController.Finish))]
    [InlineData(nameof(DayController.Charge))]
    public void Owner_day_amount_parameters_declare_the_invariant_binder(string action)
    {
        var parameter = typeof(DayController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == action)
            .GetParameters().Single(candidate => candidate.Name == "amount");

        Assert.Equal(typeof(InvariantDecimalModelBinder), parameter.GetCustomAttribute<ModelBinderAttribute>()?.BinderType);
    }

    [Fact]
    public void Seeded_invariant_text_is_what_the_input_tag_helper_renders_under_es_ar()
    {
        // The shared walk-in form seeds ModelState through InvariantDecimalFormValue.Ensure; the MVC input tag
        // helper renders that attempted value verbatim, so the number input emits "100.25" and not "100,25".
        var rendered = UnderEsAr(() =>
        {
            var modelState = new ModelStateDictionary();
            InvariantDecimalFormValue.Ensure(modelState, Field, 100.25m);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMvcCore().AddViews();
            using var provider = services.BuildServiceProvider();
            var generator = provider.GetRequiredService<IHtmlGenerator>();
            var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
            var metadata = metadataProvider.GetMetadataForType(typeof(decimal?));
            var viewData = new ViewDataDictionary(metadataProvider, modelState);
            var viewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext(),
                ViewData = viewData,
                FormContext = new FormContext()
            };

            return generator.GenerateTextBox(viewContext, new ModelExplorer(metadataProvider, metadata, 100.25m),
                Field, 100.25m, null, null).Attributes["value"];
        });

        Assert.Equal("100.25", rendered);
    }

    [Fact]
    public void Raw_posted_text_is_kept_and_never_rewritten_by_the_form_helper()
    {
        var modelState = new ModelStateDictionary();
        modelState.SetModelValue(Field, new ValueProviderResult("100,25", CultureInfo.InvariantCulture));

        var rendered = UnderEsAr(() => InvariantDecimalFormValue.Ensure(modelState, Field, 100.25m));

        Assert.Equal("100,25", rendered);
        Assert.Equal("100,25", modelState[Field]!.AttemptedValue);
    }

    [Fact]
    public void Absent_optional_value_adds_no_model_state_and_no_text()
    {
        var modelState = new ModelStateDictionary();

        var rendered = UnderEsAr(() => InvariantDecimalFormValue.Ensure(modelState, Field, null));

        Assert.Equal(string.Empty, rendered);
        Assert.False(modelState.ContainsKey(Field),
            "An absent optional amount must not add ModelState that the Jornada reads as an error.");
    }

    private static decimal Invariant(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static async Task<ModelBindingContext> BindWalkInPriceAsync(string? posted)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IModelBinderFactory>();
        var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
        var metadata = metadataProvider.GetMetadataForProperty(typeof(WalkInViewModel), nameof(WalkInViewModel.AgreedPrice));
        var binder = factory.CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });

        // Real MVC builds its form/query value providers with the request culture, so the dot is a group
        // separator under es-AR. Passing the request culture here is what reproduces the 100.25 -> 10025.00 bug.
        IValueProvider valueProvider = posted is null
            ? new CompositeValueProvider()
            : new QueryStringValueProvider(BindingSource.Query,
                new QueryCollection(new Dictionary<string, StringValues> { [Field] = posted }),
                CultureInfo.GetCultureInfo("es-AR"));

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var context = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext { HttpContext = httpContext }, valueProvider, metadata, new BindingInfo(), Field);
        context.ModelState = new ModelStateDictionary();

        await UnderEsArAsync(() => binder.BindModelAsync(context));
        return context;
    }

    private static T UnderEsAr<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-AR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-AR");
        try { return action(); }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private static async Task UnderEsArAsync(Func<Task> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-AR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-AR");
        try { await action(); }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
