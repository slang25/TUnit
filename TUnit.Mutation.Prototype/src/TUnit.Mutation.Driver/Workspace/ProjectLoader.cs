using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace TUnit.Mutation.Driver.Workspace;

public sealed class ProjectLoader : IDisposable
{
    private readonly MSBuildWorkspace _workspace;

    public ProjectLoader(string configuration)
    {
        var props = new Dictionary<string, string>
        {
            ["Configuration"] = configuration,
            ["DesignTimeBuild"] = "false",
        };
        _workspace = MSBuildWorkspace.Create(props);
        _workspace.LoadMetadataForReferencedProjects = true;
        _workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}");
            }
        };
    }

    public Task<Project> LoadAsync(string projectPath, CancellationToken ct) =>
        _workspace.OpenProjectAsync(projectPath, cancellationToken: ct);

    public void Dispose() => _workspace.Dispose();
}
