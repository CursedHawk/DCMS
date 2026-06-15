using System.Runtime.CompilerServices;

namespace Dcms.Shared.Kernel;

public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
        => value ?? throw new ArgumentNullException(name);

    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be null or whitespace.", name)
            : value;

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null)
        => value == Guid.Empty
            ? throw new ArgumentException("Value must not be an empty GUID.", name)
            : value;
}
