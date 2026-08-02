using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AnyProtocol.Generator.CodeFixes;

/// <summary>
/// Provides anyprotocol code fix values to AnyProtocol operations.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AnyProtocolCodeFixProvider))]
[Shared]
public sealed class AnyProtocolCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// Gets the fixable diagnostic ids.
    /// </summary>
    /// <value>The fixable diagnostic ids.</value>
    public override ImmutableArray<string> FixableDiagnosticIds => ["CLNK005"];

    /// <summary>
    /// Gets fix all provider.
    /// </summary>
    /// <returns>The result of the get fix all provider operation.</returns>
    public override FixAllProvider GetFixAllProvider()
        => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers code fixes async.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (root?.FindNode(context.Span) is not { } node)
        {
            return;
        }

        var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Change return type to ValueTask",
                cancellationToken => ChangeReturnTypeAsync(
                    context.Document,
                    root,
                    method,
                    cancellationToken),
                equivalenceKey: "AnyProtocol.CLNK005.ValueTask"),
            context.Diagnostics);
    }

    private static Task<Document> ChangeReturnTypeAsync(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax method,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var returnType = SyntaxFactory.ParseTypeName(
                "global::System.Threading.Tasks.ValueTask")
            .WithTriviaFrom(method.ReturnType);
        var updated = method.WithReturnType(returnType);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(method, updated)));
    }
}
