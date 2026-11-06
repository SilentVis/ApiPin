using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Execution;

/// <summary>A request ready to send, and what variable resolution found.</summary>
public sealed record PreparedRequest(RequestDefinition Request, ResolveReport Report, VariableContext Variables);

/// <summary>Steps 1–2 of the pipeline: inheritance (folder/collection headers and auth), then variables.</summary>
public static class RequestPreparer
{
    /// <param name="request">The request as edited.</param>
    /// <param name="chain">Collection first, down to the request's folder; empty for an unsaved request.</param>
    /// <param name="variables">Runtime, environment and global variables; folder and collection variables are added here.</param>
    public static PreparedRequest Prepare(RequestDefinition request, IReadOnlyList<RequestFolder> chain, VariableContext variables)
    {
        var context = InheritanceResolver.WithFolderVariables(variables, chain);
        var report = new ResolveReport();
        var resolved = VariableResolver.ResolveRequest(InheritanceResolver.Apply(request, chain), context, report);
        return new PreparedRequest(resolved, report, context);
    }
}
