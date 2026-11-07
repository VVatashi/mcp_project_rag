using Microsoft.Extensions.Options;

namespace ProjectRag.Core.Tests.Code;

// Options that never change, for the classes that read IOptionsMonitor.
internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
