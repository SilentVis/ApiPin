namespace ApiPin.Core.Models;

/// <summary>A named set of variables (Local, Dev, Staging…); switching environments re-targets every request.</summary>
public sealed class EnvironmentDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Variable> Variables { get; set; } = [];

    public EnvironmentDefinition Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid() : Id,
        Name = Name,
        Variables = Variables.ConvertAll(v => v.Clone()),
    };

    /// <summary>Secret variables whose value isn't on this computer (e.g. right after cloning the workspace).</summary>
    public IEnumerable<Variable> MissingSecrets => Variables.Where(v => v.IsActive && v.Secret && v.Value.Length == 0);
}
