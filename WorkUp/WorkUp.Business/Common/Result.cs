namespace WorkUp.Business.Common;

/// <summary>
/// Outcome of a business operation. Rule violations are expected outcomes, not exceptions: services return a failed
/// result with a user-facing message in Spanish, and controllers show it next to the form.
/// </summary>
public class Result
{
    protected Result(bool succeeded, BusinessError? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }
    public BusinessError? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(string code, string message, string? field = null) => new(false, new BusinessError(code, message, field));
    public static Result Failure(BusinessError error) => new(false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(true, null) => _value = value;
    private Result(BusinessError error) : base(false, error) { }

    /// <summary>The value of a successful result. Reading it from a failed result is a programming error.</summary>
    public T Value => Succeeded ? _value! : throw new InvalidOperationException("A failed result has no value.");

    public static Result<T> Success(T value) => new(value);
    public static new Result<T> Failure(string code, string message, string? field = null) => new(new BusinessError(code, message, field));
    public static new Result<T> Failure(BusinessError error) => new(error);
}

/// <param name="Code">Stable identifier used by tests, such as <c>slot_taken</c>.</param>
/// <param name="Message">Plain Argentine Spanish shown to the user.</param>
/// <param name="Field">Form field the error belongs to, when there is one.</param>
public sealed record BusinessError(string Code, string Message, string? Field = null);
