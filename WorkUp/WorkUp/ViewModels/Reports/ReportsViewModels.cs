using WorkUp.BusinessLogic.Reports;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;

namespace WorkUp.ViewModels.Reports;

/// <summary>
/// Spanish labels and provisional definitions of the report screen (RF-16, assumption 7). The vocabulary is the one
/// the owner already reads in the panel, and the two indicator definitions are spelled out because they are
/// provisional: the owner must be able to tell what "recurrent" and "stopped coming" mean here instead of trusting a
/// number whose rule is hidden.
/// </summary>
public static class ReportLabels
{
    /// <summary>The visible name of the unallocated membership bucket; it comes from the service, not duplicated here.</summary>
    public static string Membership => ReportsService.MembershipRevenueLabel;

    /// <summary>Definition of a recurring client, marked as provisional (assumption 7).</summary>
    public const string RecurringDefinition =
        "Clienta recurrente: dos o más asistencias dentro del período. Es una definición provisoria.";

    /// <summary>Definition of a client that stopped coming, with the 90-day lookback and the provisional tag (assumption 7).</summary>
    public const string LapsedDefinition =
        "Dejó de venir: asistió en los 90 días anteriores al período y no volvió a asistir dentro de él. " +
        "Es una definición provisoria.";

    /// <summary>The kind of session a service line was allocated to, in the owner's words.</summary>
    public static string For(AppointmentKind kind) => kind switch
    {
        AppointmentKind.Individual => "Turno individual",
        AppointmentKind.ClassSession => "Clase",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>One labelled amount of the report (RF-16): the label and its money travel together, never a bare number.</summary>
public sealed record ReportMoneyLineViewModel(string Label, decimal Amount);

/// <summary>Production of one worker (assumption 7): how many attentions were registered and how much was collected in those sessions.</summary>
public sealed record ReportWorkerLineViewModel(string WorkerName, int AttendedCount, decimal CollectedAmount);

/// <summary>One labelled row of the comparison between the requested period and another one (RF-16).</summary>
public sealed record ReportComparisonLineViewModel(string Label, decimal PeriodAmount, decimal ComparisonAmount)
{
    /// <summary>Positive when the requested period is above the comparison, negative when it is below.</summary>
    public decimal Difference => PeriodAmount - ComparisonAmount;
}

/// <summary>
/// One computed period, ready to read (RF-16). It wraps the service result — the screen never computes a bucket — and
/// adds the labels, the period text and whether there is anything to show. An empty period is a real answer, so the
/// page shows an explicit empty state instead of zeros that look like missing data.
/// </summary>
public sealed class ReportsPeriodViewModel
{
    public ReportsPeriodViewModel(PeriodReport report) => Report = report;

    public PeriodReport Report { get; }

    public DateOnly RequestedFrom => Report.RequestedFrom;

    public DateOnly RequestedTo => Report.RequestedTo;

    public DateOnly EffectiveFrom => Report.EffectiveFrom;

    public DateOnly EffectiveTo => Report.EffectiveTo;

    /// <summary>True when the period ran past today and was computed only up to today (RF-16).</summary>
    public bool IsPartial => Report.IsPartial;

    public decimal PaymentsTotal => Report.PaymentsTotal;

    public decimal RefundsTotal => Report.RefundsTotal;

    public decimal ExpensesTotal => Report.ExpensesTotal;

    public decimal NetResult => Report.NetResult;

    public decimal MembershipRevenue => Report.MembershipRevenue;

    public decimal UnallocatedRevenue => Report.UnallocatedRevenue;

    /// <summary>The visible name of the unallocated membership bucket (RF-16): "Abonos".</summary>
    public string MembershipLabel => ReportLabels.Membership;

    public int RecurringClientCount => Report.RecurringClientCount;

    public int LapsedClientCount => Report.LapsedClientCount;

    public int AbsenceCount => Report.AbsenceCount;

    /// <summary>The period as the owner asked for it, both ends inclusive.</summary>
    public string PeriodLabel => $"{Formatting.ShortDate(RequestedFrom)} al {Formatting.ShortDate(RequestedTo)}";

    /// <summary>
    /// The cutoff in words: a period that ran past today was computed only up to today, and the screen says so instead
    /// of letting a partial number look like a closed one.
    /// </summary>
    public string CutoffLabel => IsPartial
        ? $"El período pedido llega hasta el {Formatting.ShortDate(RequestedTo)}: se calcula hasta hoy, " +
          $"{Formatting.ShortDate(EffectiveTo)}, porque los días futuros todavía no ocurrieron."
        : $"Período completo: {Formatting.ShortDate(EffectiveFrom)} al {Formatting.ShortDate(EffectiveTo)}.";

    /// <summary>The three amounts the result is built from, each with its label.</summary>
    public IReadOnlyList<ReportMoneyLineViewModel> ResultLines =>
    [
        new("Cobros no anulados", PaymentsTotal),
        new("Devoluciones del período", RefundsTotal),
        new("Gastos del período", ExpensesTotal)
    ];

    /// <summary>Revenue of each concrete service, with the kind of session it was allocated to.</summary>
    public IReadOnlyList<ReportMoneyLineViewModel> RevenueLines => Report.RevenueByService
        .Select(line => new ReportMoneyLineViewModel(
            $"{ServiceName(line.ServiceName)} · {ReportLabels.For(line.Kind)}", line.Amount))
        .ToArray();

    /// <summary>Expenses of the period grouped by type.</summary>
    public IReadOnlyList<ReportMoneyLineViewModel> ExpenseTypeLines => Report.ExpensesByType
        .Select(line => new ReportMoneyLineViewModel(line.ExpenseTypeName, line.Amount))
        .ToArray();

    /// <summary>Attentions and collection per worker.</summary>
    public IReadOnlyList<ReportWorkerLineViewModel> WorkerLines => Report.WorkerProduction
        .Select(line => new ReportWorkerLineViewModel(
            WorkerName(line.WorkerName), line.AttendedCount, line.CollectedAmount))
        .ToArray();

    public bool HasServiceRevenue => Report.RevenueByService.Count > 0;

    public bool HasExpensesByType => Report.ExpensesByType.Count > 0;

    public bool HasWorkerProduction => Report.WorkerProduction.Count > 0;

    public bool HasMembershipRevenue => MembershipRevenue != 0m;

    public bool HasUnallocatedRevenue => UnallocatedRevenue != 0m;

    /// <summary>
    /// Whether the period has anything to show. Money, service revenue, expenses, worker production or an absence is
    /// enough; only a period with none of them is empty, so a period that only had attentions is never called empty.
    /// </summary>
    public bool HasMovements =>
        PaymentsTotal != 0m ||
        RefundsTotal != 0m ||
        ExpensesTotal != 0m ||
        MembershipRevenue != 0m ||
        UnallocatedRevenue != 0m ||
        HasServiceRevenue ||
        HasExpensesByType ||
        HasWorkerProduction ||
        AbsenceCount != 0;

    private static string ServiceName(string? name) => string.IsNullOrWhiteSpace(name) ? "Servicio sin nombre" : name;

    private static string WorkerName(string? name) => string.IsNullOrWhiteSpace(name) ? "Sin trabajadora" : name;
}

/// <summary>
/// The report screen (RF-16). <see cref="From"/> and <see cref="To"/> are the inclusive period shown in the form (the
/// resolved default when the owner did not ask for one); <see cref="ComparisonFrom"/> and <see cref="ComparisonTo"/>
/// are the optional comparison pair. <see cref="Period"/> is null only when the request was refused, in which case the
/// page shows the filters to correct and the error, never a report of an impossible period.
/// </summary>
public sealed class ReportsIndexViewModel
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public DateOnly? ComparisonFrom { get; init; }

    public DateOnly? ComparisonTo { get; init; }

    public ReportsPeriodViewModel? Period { get; init; }

    public ReportsPeriodViewModel? Comparison { get; init; }

    /// <summary>The business's today: the ceiling of every date input and of the computed period.</summary>
    public DateOnly Today { get; init; }

    public bool HasReport => Period is not null;

    public bool HasComparison => Period is not null && Comparison is not null;

    /// <summary>The four labelled lines of the comparison, computed only when there is a comparison period.</summary>
    public IReadOnlyList<ReportComparisonLineViewModel> ComparisonLines => HasComparison
        ? [
            new("Cobros no anulados", Period!.PaymentsTotal, Comparison!.PaymentsTotal),
            new("Devoluciones del período", Period!.RefundsTotal, Comparison!.RefundsTotal),
            new("Gastos del período", Period!.ExpensesTotal, Comparison!.ExpensesTotal),
            new("Resultado del período", Period!.NetResult, Comparison!.NetResult)
        ]
        : [];

    /// <summary>Empty-state copy: the period is a real answer with nothing in it, not a missing report.</summary>
    public string EmptyMessage =>
        "No hay movimientos en el período elegido: ni cobros, ni devoluciones, ni gastos, ni atenciones registradas.";
}
