using ApiPin.Core.Models;

namespace ApiPin.Core.Variables;

/// <summary>Where a variable's value comes from, highest precedence first.</summary>
public enum VariableSource { Runtime, Environment, Folder, Collection, Global }

/// <summary>A variable as seen by a request: its value and the scope it came from.</summary>
public sealed record ResolvedVariable(string Name, string Value, VariableSource Source, string? ScopeName, bool Secret);

/// <summary>
/// The variables in effect for one request, as layers searched from highest to lowest precedence:
/// runtime/captured → active environment → folders (innermost first) → collection → globals.
/// Immutable: <see cref="With"/> returns a new context.
/// </summary>
public sealed class VariableContext
{
    public static readonly VariableContext Empty = new([]);

    private readonly IReadOnlyList<Layer> _layers;

    private VariableContext(IReadOnlyList<Layer> layers) => _layers = layers;

    /// <summary>Adds a layer. Layers are ordered by <paramref name="source"/>; within a source, earlier layers win.</summary>
    public VariableContext With(VariableSource source, string? scopeName, IEnumerable<Variable> variables)
    {
        var values = new Dictionary<string, ResolvedVariable>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            if (!v.IsActive) continue;
            var name = v.Key.Trim();
            values.TryAdd(name, new ResolvedVariable(name, v.Value, source, scopeName, v.Secret));
        }
        if (values.Count == 0) return this;

        var layers = _layers.Append(new Layer(source, values)).OrderBy(l => l.Source).ToList(); // stable sort keeps insertion order
        return new VariableContext(layers);
    }

    public VariableContext With(VariableSource source, string? scopeName, IReadOnlyDictionary<string, string> values) =>
        With(source, scopeName, values.Select(kv => new Variable { Key = kv.Key, Value = kv.Value }));

    /// <summary>The highest-precedence variable with this name, or null.</summary>
    public ResolvedVariable? Lookup(string name)
    {
        foreach (var layer in _layers)
        {
            if (layer.Values.TryGetValue(name, out var value)) return value;
        }
        return null;
    }

    /// <summary>Every variable in effect (the winning one per name), with the ones it shadows.</summary>
    public IReadOnlyList<(ResolvedVariable Variable, IReadOnlyList<ResolvedVariable> Shadowed)> Effective()
    {
        var byName = new Dictionary<string, List<ResolvedVariable>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var layer in _layers)
        {
            foreach (var value in layer.Values.Values)
            {
                if (!byName.TryGetValue(value.Name, out var list))
                {
                    byName[value.Name] = list = [];
                    order.Add(value.Name);
                }
                list.Add(value);
            }
        }
        return order
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (byName[n][0], (IReadOnlyList<ResolvedVariable>)byName[n].Skip(1).ToList()))
            .ToList();
    }

    /// <summary>Values of secret variables, so they can be masked in previews and logs.</summary>
    public IEnumerable<string> SecretValues() =>
        _layers.SelectMany(l => l.Values.Values).Where(v => v.Secret && v.Value.Length > 0).Select(v => v.Value).Distinct();

    private sealed record Layer(VariableSource Source, Dictionary<string, ResolvedVariable> Values);
}
