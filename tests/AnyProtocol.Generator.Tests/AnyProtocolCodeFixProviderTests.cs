using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Generator.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AnyProtocol.Generator.Tests;

public sealed class AnyProtocolCodeFixProviderTests
{
    [Fact]
    public async Task Changes_invalid_return_type_to_value_task()
    {
        const string source = """
            using AnyProtocol.Configuration;

            namespace Demo;

            public interface IService
            {
                void Execute();
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(
            ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                "CodeFixTests",
                "CodeFixTests",
                LanguageNames.CSharp,
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: GetReferences()));
        var document = workspace.AddDocument(
            project.Id,
            "Contract.cs",
            SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AnyProtocolGenerator());
        driver = driver.RunGenerators(compilation!);
        var diagnostic = Assert.Single(
            driver.GetRunResult().Diagnostics.Where(static item => item.Id == "CLNK005"));
        var actions = new List<CodeAction>();
        var provider = new AnyProtocolCodeFixProvider();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await provider.RegisterCodeFixesAsync(context);
        var action = Assert.Single(actions);
        var operation = Assert.Single(
            (await action.GetOperationsAsync(CancellationToken.None))
            .OfType<ApplyChangesOperation>());
        var updated = operation.ChangedSolution.GetDocument(document.Id);

        Assert.NotNull(updated);
        Assert.Contains(
            "global::System.Threading.Tasks.ValueTask Execute()",
            (await updated.GetTextAsync()).ToString());
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return trustedAssemblies.Concat(
            [
                MetadataReference.CreateFromFile(typeof(LinkBuilder).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IClientInvoker).Assembly.Location)
            ]);
    }
}
