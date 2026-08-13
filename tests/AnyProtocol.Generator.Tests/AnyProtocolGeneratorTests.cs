using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AnyProtocol.Generator.Tests;

public sealed class AnyProtocolGeneratorTests
{
    [Fact]
    public void Generates_event_consumer_dispatch_for_closed_registration()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record OrderPlaced(string Id);

            public sealed class OrderPlacedHandler : IEventConsumer<OrderPlaced>
            {
                public ValueTask ConsumeAsync(OrderPlaced e) => ValueTask.CompletedTask;
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link)
                    => link.AddEventHandler<OrderPlaced, OrderPlacedHandler>();
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);

        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = GetGeneratedSource(result);
        Assert.Contains("GeneratedEventDispatchRegistry.Register", generated);
        Assert.Contains("typeof(global::Demo.OrderPlaced)", generated);
        Assert.Contains("typeof(global::Demo.OrderPlacedHandler)", generated);
        Assert.Contains(
            "((global::Demo.OrderPlacedHandler)target).ConsumeAsync((global::Demo.OrderPlaced)message)",
            generated);
        Assert.Contains("ModuleInitializer", generated);
    }

    [Fact]
    public void Reports_diagnostic_for_open_generic_event_registration()
    {
        const string source = """
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public static class Setup
            {
                public static void Configure<TEvent, THandler>(LinkBuilder link)
                    where TEvent : class
                    where THandler : class, IEventConsumer<TEvent>
                    => link.AddEventHandler<TEvent, THandler>();
            }
            """;

        var diagnostic = AssertDiagnostic(Run(source), "CLNK007");
        Assert.Contains("event and handler types must be closed", diagnostic.GetMessage());
    }

    [Fact]
    public void Deduplicates_duplicate_event_registration()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record OrderPlaced(string Id);

            public sealed class OrderPlacedHandler : IEventConsumer<OrderPlaced>
            {
                public ValueTask ConsumeAsync(OrderPlaced e) => ValueTask.CompletedTask;
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link)
                {
                    link.AddEventHandler<OrderPlaced, OrderPlacedHandler>();
                    link.AddEventHandler<OrderPlaced, OrderPlacedHandler>();
                }
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);

        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal(
            1,
            result.GeneratedTrees.Count(tree =>
                tree.ToString().Contains(
                    "GeneratedEventDispatchRegistry.Register",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void Generates_complete_metadata_for_registered_contract()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record Request(string Value);
            public sealed record Response(string Value);

            public interface IService
            {
                ValueTask<Response> ExecuteAsync(Request request, CancellationToken cancellationToken);
                Task<Response> ExecuteTaskAsync(Request request);
                Task NotifyAsync();
                ValueTask NotifyValueAsync(Request request);
                IAsyncEnumerable<Response> StreamAsync(
                    Request request,
                    CancellationToken cancellationToken);
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);

        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = GetGeneratedSource(result);
        Assert.Contains("IService", generated);
        Assert.Contains("RequestAsync<global::Demo.Request, global::Demo.Response>", generated);
        Assert.Contains("SendAsync<global::AnyProtocol.Abstraction.EmptyRequest>", generated);
        Assert.Contains("GeneratedContractRegistry.Register", generated);
        Assert.Contains("SerializableTypes", generated);
        Assert.Contains("GeneratedServerMethodRegistration[] ServerMethods", generated);
        Assert.Contains("ValueTask<object?> Dispatch0", generated);
        Assert.Contains("return await ((global::Demo.IService)target).@ExecuteAsync", generated);
        Assert.Contains(").@ExecuteTaskAsync", generated);
        Assert.Contains("await ((global::Demo.IService)target).@NotifyAsync", generated);
        Assert.Contains(").@NotifyValueAsync", generated);
        Assert.Contains("IAsyncEnumerable<object?> Dispatch", generated);
        Assert.Contains(".GetAsyncEnumerator(cancellationToken)", generated);
        Assert.Contains("ContractMethodDescriptor", generated);
        Assert.Contains("ModuleInitializer", generated);
    }

    [Fact]
    public void Reports_diagnostic_for_non_interface_contract()
    {
        const string source = """
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed class NotAContract;

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<NotAContract>();
            }
            """;

        AssertDiagnostic(Run(source), "CLNK001");
    }

    [Fact]
    public void Reports_diagnostic_for_open_generic_registration()
    {
        const string source = """
            using AnyProtocol.Configuration;

            namespace Demo;

            public static class Setup
            {
                public static void Configure<TContract>(LinkBuilder link)
                    where TContract : class
                    => link.AddClient<TContract>();
            }
            """;

        var diagnostic = AssertDiagnostic(Run(source), "CLNK007");
        Assert.Contains("register each closed contract directly", diagnostic.GetMessage());
    }

    [Fact]
    public void Generated_metadata_includes_empty_request_and_erases_nullable_annotations()
    {
        const string source = """
            #nullable enable
            using System.Threading.Tasks;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record Request(string Value);
            public sealed record Response(string Value);

            public interface IService
            {
                ValueTask<Response?> ExecuteAsync(Request? request);
                ValueTask PingAsync();
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);
        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = GetGeneratedSource(result);
        Assert.Contains(
            "typeof(global::AnyProtocol.Abstraction.EmptyRequest),",
            generated);
        Assert.Contains(
            "typeof(global::AnyProtocol.Abstraction.FaultMessage),",
            generated);
        Assert.Contains(
            "typeof(global::AnyProtocol.Abstraction.Unit),",
            generated);
        Assert.Contains("typeof(global::System.Object),", generated);
        Assert.Contains("typeof(global::Demo.Request),", generated);
        Assert.Contains("typeof(global::Demo.Response),", generated);
        Assert.DoesNotContain("typeof(global::Demo.Request?)", generated);
        Assert.DoesNotContain("typeof(global::Demo.Response?)", generated);
    }

    [Theory]
    [InlineData("string Name { get; }")]
    [InlineData("event System.EventHandler Changed;")]
    [InlineData("static System.Threading.Tasks.ValueTask PingAsync() => default;")]
    [InlineData("static virtual IService operator +(IService left, IService right) => left;")]
    public void Reports_diagnostic_for_unsupported_contract_members(string member)
    {
        var source = $$"""
            using AnyProtocol.Configuration;

            namespace Demo;

            public interface IService
            {
                {{member}}
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        AssertDiagnostic(Run(source), "CLNK008");
    }

    [Fact]
    public void Private_interface_helpers_are_not_generated_as_wire_operations()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Configuration;

            namespace Demo;

            public interface IService
            {
                ValueTask ExecuteAsync();
                private ValueTask HelperAsync() => default;
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);
        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain("HelperAsync", GetGeneratedSource(result));
    }

    [Fact]
    public void Blank_channel_attributes_use_conventional_route()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            [Channel("  ")]
            public interface IService
            {
                [Channel("")]
                ValueTask ExecuteAsync();
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var result = Run(source);
        AssertNoErrors(result);
        Assert.Contains(
            "channelPrefix + \".service.execute\"",
            GetGeneratedSource(result));
    }

    [Fact]
    public void Reports_diagnostic_for_inherited_partition_key_collision()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public class BaseRequest
            {
                [PartitionKey]
                public string First { get; init; } = "";
            }

            public sealed class Request : BaseRequest
            {
                [PartitionKey]
                public string Second { get; init; } = "";
            }

            public interface IService
            {
                ValueTask SendAsync(Request request);
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        AssertDiagnostic(Run(source), "CLNK006");
    }

    [Fact]
    public void Generated_and_runtime_descriptors_resolve_inherited_interface_partition_key()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public interface IBaseRequest
            {
                [PartitionKey]
                string Key { get; }
            }

            public interface IRequest : IBaseRequest;

            public interface IService
            {
                ValueTask SendAsync(IRequest request);
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var (result, outputCompilation) = RunWithCompilation(source);
        AssertNoErrors(result);
        Assert.Empty(outputCompilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            """typeof(global::Demo.IBaseRequest).GetProperty("Key")""",
            GetGeneratedSource(result));

        var descriptor = new ContractDescriptorFactory()
            .Create<IRuntimeInheritedPartitionContract>();
        Assert.Equal(
            typeof(IRuntimeBaseRequest),
            Assert.Single(descriptor.Methods).PartitionKeyProperty?.DeclaringType);
    }

    [Theory]
    [MemberData(nameof(InvalidShapeVectors))]
    public void Analyzer_and_runtime_reject_the_same_contract_shapes(
        string source,
        string diagnosticId,
        Type runtimeContract)
    {
        AssertDiagnostic(Run(source), diagnosticId);
        Assert.Throws<ContractShapeException>(
            () => new ContractDescriptorFactory().Create(runtimeContract));
    }

    [Fact]
    public void Analyzer_and_runtime_accept_inherited_idempotent_contract()
    {
        const string source = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record Request(string Value);
            public sealed record Response(string Value);

            [Idempotent]
            public interface IBaseService
            {
                ValueTask<Response> ExecuteAsync(Request request);
            }

            public interface IService : IBaseService;

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;

        var result = Run(source);
        AssertNoErrors(result);
        Assert.Contains("IsIdempotent = true", GetGeneratedSource(result));

        var descriptor = new ContractDescriptorFactory().Create<IRuntimeValidContract>();
        Assert.True(Assert.Single(descriptor.Methods).IsIdempotent);
    }

    [Theory]
    [MemberData(nameof(IncrementalInvalidationVectors))]
    public void Incremental_generation_invalidates_for_contract_metadata_changes(
        string initialSource,
        string updatedSource,
        string updatedFragment)
    {
        var (initial, updated) = RunIncremental(initialSource, updatedSource);

        Assert.NotEqual(initial, updated);
        Assert.Contains(updatedFragment, updated);
    }

    public static IEnumerable<object[]> InvalidShapeVectors()
    {
        yield return
        [
            """
            using System.Threading.Tasks;
            using AnyProtocol.Configuration;
            namespace Demo;
            public interface IService { ValueTask<T> ExecuteAsync<T>(); }
            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """,
            "CLNK002",
            typeof(IRuntimeGenericMethodContract)
        ];
        yield return
        [
            """
            using AnyProtocol.Configuration;
            namespace Demo;
            public interface IService { string Name { get; } }
            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """,
            "CLNK008",
            typeof(IRuntimePropertyContract)
        ];
        yield return
        [
            """
            using AnyProtocol.Configuration;
            namespace Demo;
            public interface IService
            {
                static virtual IService operator +(IService left, IService right) => left;
            }
            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """,
            "CLNK008",
            typeof(IRuntimeOperatorContract)
        ];
        yield return
        [
            """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;
            namespace Demo;
            public class BaseRequest
            {
                [PartitionKey]
                public string First { get; init; } = "";
            }
            public sealed class Request : BaseRequest
            {
                [PartitionKey]
                public string Second { get; init; } = "";
            }
            public interface IService { ValueTask SendAsync(Request request); }
            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """,
            "CLNK006",
            typeof(IRuntimePartitionContract)
        ];
    }

    public static IEnumerable<object[]> IncrementalInvalidationVectors()
    {
        yield return Invalidation(
            "ValueTask<Response> ExecuteAsync(Request request);",
            """
            ValueTask<Response> ExecuteAsync(Request request);
            ValueTask NotifyAsync();
            """,
            "NotifyAsync");
        yield return Invalidation(
            """[Channel("first")] ValueTask<Response> ExecuteAsync(Request request);""",
            """[Channel("second")] ValueTask<Response> ExecuteAsync(Request request);""",
            "\"second\"");
        yield return Invalidation(
            "public sealed record Request(string Key);",
            "public sealed record Request([property: PartitionKey] string Key);",
            """GetProperty("Key")""");
        yield return Invalidation(
            "public interface IBaseService { ValueTask<Response> BaseAsync(Request request); }",
            """
            public interface IBaseService
            {
                ValueTask<Response> BaseAsync(Request request);
                ValueTask<Response> AddedAsync(Request request);
            }
            """,
            "AddedAsync");
        yield return Invalidation(
            "public interface IService : IBaseService",
            "[Idempotent] public interface IService : IBaseService",
            "IsIdempotent = true");
    }

    private static object[] Invalidation(
        string initialReplacement,
        string updatedReplacement,
        string updatedFragment)
    {
        const string template = """
            using System.Threading.Tasks;
            using AnyProtocol.Abstraction;
            using AnyProtocol.Configuration;

            namespace Demo;

            public sealed record Request(string Key);
            public sealed record Response(string Value);
            public interface IBaseService { ValueTask<Response> BaseAsync(Request request); }

            public interface IService : IBaseService
            {
                ValueTask<Response> ExecuteAsync(Request request);
            }

            public static class Setup
            {
                public static void Configure(LinkBuilder link) => link.AddClient<IService>();
            }
            """;
        return
        [
            ReplaceVectorTarget(template, initialReplacement, updatedReplacement, useInitial: true),
            ReplaceVectorTarget(template, initialReplacement, updatedReplacement, useInitial: false),
            updatedFragment
        ];
    }

    private static string ReplaceVectorTarget(
        string template,
        string initialReplacement,
        string updatedReplacement,
        bool useInitial)
    {
        var replacement = useInitial ? initialReplacement : updatedReplacement;
        if (initialReplacement.StartsWith("public sealed record", StringComparison.Ordinal))
        {
            return template.Replace(
                "public sealed record Request(string Key);",
                replacement,
                StringComparison.Ordinal);
        }

        if (initialReplacement.StartsWith("public interface IBaseService", StringComparison.Ordinal))
        {
            return template.Replace(
                "public interface IBaseService { ValueTask<Response> BaseAsync(Request request); }",
                replacement,
                StringComparison.Ordinal);
        }

        if (initialReplacement.StartsWith("public interface IService", StringComparison.Ordinal))
        {
            return template.Replace(
                "public interface IService : IBaseService",
                replacement,
                StringComparison.Ordinal);
        }

        return template.Replace(
            "ValueTask<Response> ExecuteAsync(Request request);",
            replacement,
            StringComparison.Ordinal);
    }

    private static (string Initial, string Updated) RunIncremental(
        string initialSource,
        string updatedSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var initialTree = CSharpSyntaxTree.ParseText(initialSource, parseOptions);
        var compilation = CreateCompilation(initialTree);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AnyProtocolGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var initial = GetGeneratedSource(driver.GetRunResult());

        var updatedTree = CSharpSyntaxTree.ParseText(updatedSource, parseOptions);
        var updatedCompilation = compilation.ReplaceSyntaxTree(initialTree, updatedTree);
        driver = driver.RunGeneratorsAndUpdateCompilation(updatedCompilation, out _, out _);
        var updated = GetGeneratedSource(driver.GetRunResult());
        return (initial, updated);
    }

    private static GeneratorDriverRunResult Run(string source)
        => RunWithCompilation(source).Result;

    private static (GeneratorDriverRunResult Result, Compilation OutputCompilation)
        RunWithCompilation(string source)
    {
        var compilation = CreateCompilation(
            CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Latest)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AnyProtocolGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out _);
        return (driver.GetRunResult(), outputCompilation);
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree syntaxTree)
        => CSharpCompilation.Create(
            "GeneratorTests",
            [syntaxTree],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static Diagnostic AssertDiagnostic(
        GeneratorDriverRunResult result,
        string diagnosticId)
    {
        var diagnostic = Assert.Single(result.Diagnostics.Where(
            item => item.Id == diagnosticId));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        return diagnostic;
    }

    private static void AssertNoErrors(GeneratorDriverRunResult result)
        => Assert.Empty(result.Diagnostics.Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static string GetGeneratedSource(GeneratorDriverRunResult result)
        => Assert.Single(result.GeneratedTrees).ToString();

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

    private interface IRuntimeGenericMethodContract
    {
        ValueTask<T> ExecuteAsync<T>();
    }

    private interface IRuntimePropertyContract
    {
        string Name { get; }
    }

    private interface IRuntimeOperatorContract
    {
        static virtual IRuntimeOperatorContract operator +(
            IRuntimeOperatorContract left,
            IRuntimeOperatorContract right)
            => left;
    }

    private class RuntimeBaseRequest
    {
        [PartitionKey]
        public string First { get; init; } = "";
    }

    private sealed class RuntimeRequest : RuntimeBaseRequest
    {
        [PartitionKey]
        public string Second { get; init; } = "";
    }

    private interface IRuntimePartitionContract
    {
        ValueTask SendAsync(RuntimeRequest request);
    }

    private interface IRuntimeBaseRequest
    {
        [PartitionKey]
        string Key { get; }
    }

    private interface IRuntimeDerivedRequest : IRuntimeBaseRequest;

    private interface IRuntimeInheritedPartitionContract
    {
        ValueTask SendAsync(IRuntimeDerivedRequest request);
    }

    private sealed record RuntimeValidRequest(string Value);

    [Idempotent]
    private interface IRuntimeValidContract
    {
        ValueTask<string> ExecuteAsync(RuntimeValidRequest request);
    }
}
