using System.Collections;

namespace CashMovements.Grpc.Interceptors;

/// <summary>
/// A log scope of named values. Structured outputs (JSON, log stores) read the values as fields; text output gets a
/// readable <c>Name:value Name:value</c> instead of the type name a plain dictionary would print.
/// </summary>
internal sealed class LogScope(params (string Name, object Value)[] values) : IReadOnlyList<KeyValuePair<string, object>>
{
    private readonly KeyValuePair<string, object>[] _values = values.Select(v => new KeyValuePair<string, object>(v.Name, v.Value)).ToArray();

    public int Count => _values.Length;

    public KeyValuePair<string, object> this[int index] => _values[index];

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object>>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(" ", _values.Select(v => $"{v.Key}:{v.Value}"));
}
