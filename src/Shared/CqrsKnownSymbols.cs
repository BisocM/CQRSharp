using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Shared;

/// <summary>
///     The CQRSharp framework types the source generator and the analyzers recognize, resolved from a
///     <see cref="Compilation" /> by metadata name. Built once per compilation and shared by every consumer.
/// </summary>
/// <remarks>
///     <see cref="TypeNames" /> is the one table of those names. Generated code names the same public types, so every
///     entry is part of the binary contract that already-compiled libraries' generated modules depend on for the whole
///     major version; a test resolves each entry against the real assemblies, so a rename fails the build instead of
///     silently switching the tooling off. Every generic type is stored as its unbound definition, which is what
///     callers compare <c>iface.OriginalDefinition</c> against.
/// </remarks>
internal sealed class CqrsKnownSymbols
{
    /// <summary>The metadata name of every framework type the generator or an analyzer recognizes.</summary>
    internal static class TypeNames
    {
        public const string IRequest = "CQRSharp.IRequest";
        public const string ICommand = "CQRSharp.ICommand";
        public const string ICommandWithResult = "CQRSharp.ICommand`1";
        public const string IQuery = "CQRSharp.IQuery`1";
        public const string IStreamRequestMarker = "CQRSharp.IStreamRequest";
        public const string IStreamRequest = "CQRSharp.IStreamRequest`1";
        public const string RequestBaseGeneric = "CQRSharp.RequestBase`1";
        public const string IRequestContext = "CQRSharp.IRequestContext";
        public const string RequestContextBase = "CQRSharp.RequestContextBase";
        public const string CommandResult = "CQRSharp.CommandResult";
        public const string CommandResultGeneric = "CQRSharp.CommandResult`1";
        public const string ICommandHandler = "CQRSharp.ICommandHandler`1";
        public const string IResultCommandHandler = "CQRSharp.IResultCommandHandler`2";
        public const string IQueryHandler = "CQRSharp.IQueryHandler`2";
        public const string IStreamRequestHandler = "CQRSharp.IStreamRequestHandler`2";
        public const string INotification = "CQRSharp.INotification";
        public const string INotificationHandler = "CQRSharp.INotificationHandler`1";
        public const string NotificationNameAttribute = "CQRSharp.NotificationNameAttribute";
        public const string NotificationHandlerNameAttribute = "CQRSharp.NotificationHandlerNameAttribute";
        public const string IPartitionedNotification = "CQRSharp.IPartitionedNotification";
        public const string IRequestValidator1 = "CQRSharp.IRequestValidator`1";
        public const string IRequestExceptionHandler3 = "CQRSharp.IRequestExceptionHandler`3";
        public const string IRequestExceptionAction2 = "CQRSharp.IRequestExceptionAction`2";
        public const string IPreHandlerAttribute = "CQRSharp.IPreHandlerAttribute";
        public const string IPostHandlerAttribute = "CQRSharp.IPostHandlerAttribute";
        public const string PipelineExemptionAttribute = "CQRSharp.PipelineExemptionAttribute";
        public const string IIdempotentRequest = "CQRSharp.IIdempotentRequest";
        public const string IFingerprintedRequest = "CQRSharp.IFingerprintedRequest";
        public const string ITransactionalCommand = "CQRSharp.ITransactionalCommand";
        public const string CqrsGeneratedModuleAttribute = "CQRSharp.Core.SourceGeneration.CqrsGeneratedModuleAttribute";
        public const string CqrsHandledRequestAttribute = "CQRSharp.Core.SourceGeneration.CqrsHandledRequestAttribute";
        public const string CqrsHandledNotificationAttribute = "CQRSharp.Core.SourceGeneration.CqrsHandledNotificationAttribute";
        public const string CqrsRegisteredContextFactoryAttribute = "CQRSharp.Core.SourceGeneration.CqrsRegisteredContextFactoryAttribute";
        public const string Dispatcher = "CQRSharp.ICqrsDispatcher";
        public const string ICqrsModule = "CQRSharp.Core.Modules.ICqrsModule";
        public const string IPipelineBehavior = "CQRSharp.Pipelines.IPipelineBehavior`2";
        public const string IStreamPipelineBehavior = "CQRSharp.Pipelines.IStreamPipelineBehavior`2";
        public const string INotificationPipelineBehavior = "CQRSharp.Pipelines.INotificationPipelineBehavior`1";
        public const string IRequestContextFactory = "CQRSharp.IRequestContextFactory`1";
        public const string DependencyInjectionExtensions = "CQRSharp.DependencyInjectionExtensions";
        public const string ICqrsBuilder = "CQRSharp.Pipelines.ICqrsBuilder";

        /// <summary>
        ///     The types of CQRSharp.Abstractions and CQRSharp.Core. Every assembly the generator emits a module for
        ///     references both, so a missing one means mismatched package versions (CQRGEN007).
        /// </summary>
        public static readonly ImmutableArray<string> Required = ImmutableArray.Create(
            IRequest, ICommand, ICommandWithResult, IQuery, IStreamRequestMarker, IStreamRequest,
            RequestBaseGeneric, IRequestContext, RequestContextBase, CommandResult, CommandResultGeneric,
            ICommandHandler, IResultCommandHandler, IQueryHandler, IStreamRequestHandler, INotification, INotificationHandler,
            NotificationNameAttribute, NotificationHandlerNameAttribute, IPartitionedNotification, IRequestValidator1,
            IRequestExceptionHandler3, IRequestExceptionAction2, IPreHandlerAttribute, IPostHandlerAttribute,
            PipelineExemptionAttribute, IIdempotentRequest, IFingerprintedRequest, ITransactionalCommand, CqrsGeneratedModuleAttribute,
            CqrsHandledRequestAttribute, CqrsHandledNotificationAttribute, CqrsRegisteredContextFactoryAttribute,
            Dispatcher, ICqrsModule, IPipelineBehavior, IStreamPipelineBehavior, INotificationPipelineBehavior,
            IRequestContextFactory, DependencyInjectionExtensions);

        /// <summary>The types of CQRSharp.Pipelines, which a project that does not configure CQRSharp may leave out.</summary>
        public static readonly ImmutableArray<string> Optional = ImmutableArray.Create(ICqrsBuilder);
    }

    /// <summary>The bootstrap type the generator emits in every CQRSharp.Core-referencing assembly (see <see cref="ModuleNamespaceFor" />).</summary>
    public const string BootstrapTypeName = "CqrsGeneratedBootstrap";

    private static readonly ConditionalWeakTable<Compilation, CqrsKnownSymbols> Cache = new();

    private readonly Dictionary<string, INamedTypeSymbol> _symbols = new(StringComparer.Ordinal);

    private CqrsKnownSymbols(Compilation compilation)
    {
        foreach (var name in TypeNames.Required.Concat(TypeNames.Optional))
            if (compilation.GetTypeByMetadataName(name) is { } symbol)
                _symbols[name] = symbol.OriginalDefinition;

        CoreReferenced = ICqrsModule is not null;
        AsyncEnumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        AttributeUsageAttribute = compilation.GetTypeByMetadataName("System.AttributeUsageAttribute");
        ObsoleteAttribute = compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
        ExperimentalAttribute = compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.ExperimentalAttribute");

        var handlers = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var handler in new[] { ICommandHandler, IResultCommandHandler, IQueryHandler, IStreamRequestHandler, INotificationHandler })
            if (handler is not null)
                handlers.Add(handler);
        DispatchHandlerDefinitions = handlers.ToImmutable();

        foreach (var role in new[] { IRequestValidator1, IRequestExceptionHandler3, IRequestExceptionAction2, IPipelineBehavior, IStreamPipelineBehavior, INotificationPipelineBehavior })
            if (role is not null)
                handlers.Add(role);
        RegisteredRoleDefinitions = handlers.ToImmutable();
    }

    /// <summary>
    ///     True when CQRSharp.Core is referenced. The generator emits nothing without it: every generated module
    ///     implements its <c>ICqrsModule</c>.
    /// </summary>
    public bool CoreReferenced { get; }

    /// <summary>
    ///     The handler interfaces the generator registers and whose absence fails a dispatch with "no handler": command,
    ///     value-returning command, query, stream and notification handlers. Pipeline behaviors, validators and exception
    ///     hooks are not among them.
    /// </summary>
    public ImmutableArray<INamedTypeSymbol> DispatchHandlerDefinitions { get; }

    /// <summary>
    ///     Every interface the generator registers an implementation under: the dispatch handlers, plus validators,
    ///     exception handlers and actions, and request, stream and notification pipeline behaviors (which are resolved as
    ///     <c>IEnumerable</c> of the interface rather than dispatched to).
    /// </summary>
    public ImmutableArray<INamedTypeSymbol> RegisteredRoleDefinitions { get; }

    /// <summary>The framework types of <paramref name="compilation" />, resolved on first use and then shared.</summary>
    public static CqrsKnownSymbols For(Compilation compilation)
        => Cache.GetValue(compilation, static c => new CqrsKnownSymbols(c));

    /// <summary>
    ///     The per-assembly namespace the generated module (its tables, registrar and bootstrap) lives in:
    ///     <c>CQRSharp.Generated.</c> followed by the assembly name, one namespace segment per dotted part, so two assemblies
    ///     in one reference graph never emit colliding generated types. A part that is not a C# identifier as it stands (a
    ///     '-' in it, a leading digit, a keyword, an empty part) is made one, and the last segment then carries a hash of
    ///     the exact assembly name: two names that make the same identifiers (<c>Orders-Api</c>, <c>Orders+Api</c>) still get
    ///     different namespaces.
    /// </summary>
    public static string ModuleNamespaceFor(string? assemblyName)
    {
        var name = string.IsNullOrEmpty(assemblyName) ? "Anonymous" : assemblyName!;
        var sb = new StringBuilder("CQRSharp.Generated");
        var lossy = false;
        foreach (var part in name.Split('.'))
        {
            sb.Append('.');
            var start = sb.Length;
            foreach (var c in part)
            {
                // A formatting character is dropped from identifiers when C# compares them, so two names differing only
                // in one would read as the same namespace.
                var keep = SyntaxFacts.IsIdentifierPartCharacter(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format;
                sb.Append(keep ? c : '_');
                lossy |= !keep;
            }

            if (sb.Length == start || !SyntaxFacts.IsIdentifierStartCharacter(sb[start]))
            {
                sb.Insert(start, '_');
                lossy = true;
            }

            if (SyntaxFacts.GetKeywordKind(sb.ToString(start, sb.Length - start)) != SyntaxKind.None)
            {
                sb.Append('_');
                lossy = true;
            }
        }

        if (lossy)
            sb.Append('_').Append(StableHash(name).ToString("X8", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    /// <summary>
    ///     FNV-1a over the UTF-16 code units: stable across processes and machines, unlike string.GetHashCode, so it can
    ///     be part of a generated identifier.
    /// </summary>
    internal static uint StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    /// <summary>Whether a type argument is, or contains, a type parameter, so it names no concrete type until closed.</summary>
    public static bool ContainsTypeParameter(ITypeSymbol type)
        => type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
            _ => false
        };

    /// <summary>
    ///     The required framework types that did not resolve although CQRSharp.Core is referenced. Empty when Core is
    ///     absent: the generator then has nothing to do.
    /// </summary>
    public IReadOnlyList<string> GetMissingRequiredTypeNames()
    {
        if (!CoreReferenced) return Array.Empty<string>();

        List<string>? missing = null;
        foreach (var name in TypeNames.Required)
            if (!_symbols.ContainsKey(name))
                (missing ??= new List<string>()).Add(name);

        return (IReadOnlyList<string>?)missing ?? Array.Empty<string>();
    }

    /// <summary>Whether <paramref name="iface" /> is a constructed dispatch-handler interface (see <see cref="DispatchHandlerDefinitions" />).</summary>
    public bool IsDispatchHandler(INamedTypeSymbol iface)
    {
        if (!iface.IsGenericType) return false;
        foreach (var definition in DispatchHandlerDefinitions)
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition))
                return true;
        return false;
    }

    /// <summary>
    ///     How <paramref name="type" /> is dispatched: the one request interface it is read by, in this order: a stream
    ///     request (<c>IStreamRequest&lt;TItem&gt;</c>), a query (<c>IQuery&lt;TResult&gt;</c>), a value-returning command
    ///     (<c>ICommand&lt;TValue&gt;</c>), a command (<c>ICommand</c>). Null for a type that is none of them.
    /// </summary>
    public CqrsRequestShape? ShapeOf(ITypeSymbol type)
    {
        if (IStreamRequest is { } stream && FindConstructed(type, stream) is { } streamInterface)
        {
            var item = streamInterface.TypeArguments[0];
            return AsyncEnumerable is null ? null : new CqrsRequestShape(CqrsRequestKind.Stream, AsyncEnumerable.Construct(item), item);
        }

        if (IQuery is { } query && FindConstructed(type, query) is { } queryInterface)
            return new CqrsRequestShape(CqrsRequestKind.Query, queryInterface.TypeArguments[0], queryInterface.TypeArguments[0]);

        if (ICommandWithResult is { } commandWithResult && CommandResultGeneric is { } commandResultGeneric &&
            FindConstructed(type, commandWithResult) is { } resultCommandInterface)
        {
            var result = commandResultGeneric.Construct(resultCommandInterface.TypeArguments[0]);
            return new CqrsRequestShape(CqrsRequestKind.ResultCommand, result, result);
        }

        if (ICommand is { } command && CommandResult is { } commandResult && type.AllInterfaces.Contains(command, SymbolEqualityComparer.Default))
            return new CqrsRequestShape(CqrsRequestKind.Command, commandResult, null);

        return null;
    }

    /// <summary>Whether <paramref name="iface" /> is a constructed interface of one of the <see cref="RegisteredRoleDefinitions" />.</summary>
    public bool IsRegisteredRole(INamedTypeSymbol iface)
    {
        if (!iface.IsGenericType) return false;
        foreach (var definition in RegisteredRoleDefinitions)
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition))
                return true;
        return false;
    }

    /// <summary>
    ///     The pipeline-behavior service a request's behaviors are resolved as: <c>IStreamPipelineBehavior&lt;TRequest,
    ///     TItem&gt;</c> for a stream request, otherwise <c>IPipelineBehavior&lt;TRequest, TResponse&gt;</c> over the
    ///     response the request is dispatched with (see <see cref="ShapeOf" />). Null for a type that is not a request, or
    ///     when a behavior interface is not referenced.
    /// </summary>
    public INamedTypeSymbol? PipelineBehaviorServiceOf(INamedTypeSymbol request)
    {
        if (ShapeOf(request) is not { } shape) return null;
        return shape.Kind == CqrsRequestKind.Stream
            ? IStreamPipelineBehavior?.Construct(request, shape.ResultOrItem!)
            : IPipelineBehavior?.Construct(request, shape.Response);
    }

    /// <summary>
    ///     The context type a request declares by deriving, directly or through a base such as
    ///     <c>CommandBase&lt;TContext&gt;</c>, from <c>RequestBase&lt;TContext&gt;</c>. Null when it does not derive from it.
    /// </summary>
    public ITypeSymbol? DeclaredContextOf(ITypeSymbol requestType)
    {
        if (RequestBaseGeneric is not { } requestBase) return null;
        for (var current = requestType as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.IsGenericType && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, requestBase))
                return current.TypeArguments[0];
        return null;
    }

    private static INamedTypeSymbol? FindConstructed(ITypeSymbol type, INamedTypeSymbol definition)
    {
        foreach (var iface in type.AllInterfaces)
            if (iface.IsGenericType && SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition))
                return iface;
        return null;
    }

    private INamedTypeSymbol? Get(string metadataName) => _symbols.TryGetValue(metadataName, out var symbol) ? symbol : null;

    public INamedTypeSymbol? IRequest => Get(TypeNames.IRequest);
    public INamedTypeSymbol? ICommand => Get(TypeNames.ICommand);
    public INamedTypeSymbol? ICommandWithResult => Get(TypeNames.ICommandWithResult);
    public INamedTypeSymbol? IQuery => Get(TypeNames.IQuery);
    public INamedTypeSymbol? IStreamRequestMarker => Get(TypeNames.IStreamRequestMarker);
    public INamedTypeSymbol? IStreamRequest => Get(TypeNames.IStreamRequest);
    public INamedTypeSymbol? RequestBaseGeneric => Get(TypeNames.RequestBaseGeneric);
    public INamedTypeSymbol? IRequestContext => Get(TypeNames.IRequestContext);
    public INamedTypeSymbol? RequestContextBase => Get(TypeNames.RequestContextBase);
    public INamedTypeSymbol? CommandResult => Get(TypeNames.CommandResult);
    public INamedTypeSymbol? CommandResultGeneric => Get(TypeNames.CommandResultGeneric);
    public INamedTypeSymbol? ICommandHandler => Get(TypeNames.ICommandHandler);
    public INamedTypeSymbol? IResultCommandHandler => Get(TypeNames.IResultCommandHandler);
    public INamedTypeSymbol? IQueryHandler => Get(TypeNames.IQueryHandler);
    public INamedTypeSymbol? IStreamRequestHandler => Get(TypeNames.IStreamRequestHandler);
    public INamedTypeSymbol? INotification => Get(TypeNames.INotification);
    public INamedTypeSymbol? INotificationHandler => Get(TypeNames.INotificationHandler);
    public INamedTypeSymbol? NotificationNameAttribute => Get(TypeNames.NotificationNameAttribute);
    public INamedTypeSymbol? NotificationHandlerNameAttribute => Get(TypeNames.NotificationHandlerNameAttribute);
    public INamedTypeSymbol? IPartitionedNotification => Get(TypeNames.IPartitionedNotification);
    public INamedTypeSymbol? IRequestValidator1 => Get(TypeNames.IRequestValidator1);
    public INamedTypeSymbol? IRequestExceptionHandler3 => Get(TypeNames.IRequestExceptionHandler3);
    public INamedTypeSymbol? IRequestExceptionAction2 => Get(TypeNames.IRequestExceptionAction2);
    public INamedTypeSymbol? IPreHandlerAttribute => Get(TypeNames.IPreHandlerAttribute);
    public INamedTypeSymbol? IPostHandlerAttribute => Get(TypeNames.IPostHandlerAttribute);
    public INamedTypeSymbol? PipelineExemptionAttribute => Get(TypeNames.PipelineExemptionAttribute);
    public INamedTypeSymbol? IIdempotentRequest => Get(TypeNames.IIdempotentRequest);
    public INamedTypeSymbol? IFingerprintedRequest => Get(TypeNames.IFingerprintedRequest);
    public INamedTypeSymbol? ITransactionalCommand => Get(TypeNames.ITransactionalCommand);
    public INamedTypeSymbol? CqrsGeneratedModuleAttribute => Get(TypeNames.CqrsGeneratedModuleAttribute);
    public INamedTypeSymbol? CqrsHandledRequestAttribute => Get(TypeNames.CqrsHandledRequestAttribute);
    public INamedTypeSymbol? CqrsHandledNotificationAttribute => Get(TypeNames.CqrsHandledNotificationAttribute);
    public INamedTypeSymbol? CqrsRegisteredContextFactoryAttribute => Get(TypeNames.CqrsRegisteredContextFactoryAttribute);
    public INamedTypeSymbol? Dispatcher => Get(TypeNames.Dispatcher);
    public INamedTypeSymbol? ICqrsModule => Get(TypeNames.ICqrsModule);
    public INamedTypeSymbol? IPipelineBehavior => Get(TypeNames.IPipelineBehavior);
    public INamedTypeSymbol? IStreamPipelineBehavior => Get(TypeNames.IStreamPipelineBehavior);
    public INamedTypeSymbol? INotificationPipelineBehavior => Get(TypeNames.INotificationPipelineBehavior);
    public INamedTypeSymbol? IRequestContextFactory => Get(TypeNames.IRequestContextFactory);
    public INamedTypeSymbol? DependencyInjectionExtensions => Get(TypeNames.DependencyInjectionExtensions);
    public INamedTypeSymbol? ICqrsBuilder => Get(TypeNames.ICqrsBuilder);

    /// <summary><c>System.Collections.Generic.IAsyncEnumerable&lt;T&gt;</c>, the response of a stream request.</summary>
    public INamedTypeSymbol? AsyncEnumerable { get; }

    /// <summary><c>System.AttributeUsageAttribute</c>, which decides whether an attribute on a base class applies to a derived one.</summary>
    public INamedTypeSymbol? AttributeUsageAttribute { get; }

    /// <summary><c>System.ObsoleteAttribute</c>.</summary>
    public INamedTypeSymbol? ObsoleteAttribute { get; }

    /// <summary><c>System.Diagnostics.CodeAnalysis.ExperimentalAttribute</c>, where the target framework has it.</summary>
    public INamedTypeSymbol? ExperimentalAttribute { get; }
}

/// <summary>How a request is dispatched (see <see cref="CqrsKnownSymbols.ShapeOf" />).</summary>
internal enum CqrsRequestKind
{
    /// <summary>A command: <c>ICommand</c>, dispatched with a <c>CommandResult</c>.</summary>
    Command,

    /// <summary>A value-returning command: <c>ICommand&lt;TValue&gt;</c>, dispatched with a <c>CommandResult&lt;TValue&gt;</c>.</summary>
    ResultCommand,

    /// <summary>A query: <c>IQuery&lt;TResult&gt;</c>.</summary>
    Query,

    /// <summary>A stream request: <c>IStreamRequest&lt;TItem&gt;</c>, dispatched with an <c>IAsyncEnumerable&lt;TItem&gt;</c>.</summary>
    Stream
}

/// <summary>A request's dispatch shape: its kind, and the types it is dispatched with.</summary>
internal readonly struct CqrsRequestShape
{
    public CqrsRequestShape(CqrsRequestKind kind, ITypeSymbol response, ITypeSymbol? resultOrItem)
    {
        Kind = kind;
        Response = response;
        ResultOrItem = resultOrItem;
    }

    /// <summary>How the request is dispatched.</summary>
    public CqrsRequestKind Kind { get; }

    /// <summary>
    ///     The <c>TResponse</c> of the <c>IRequest&lt;TResponse&gt;</c> the request is dispatched as: <c>CommandResult</c>,
    ///     <c>CommandResult&lt;TValue&gt;</c>, the query result, or <c>IAsyncEnumerable&lt;TItem&gt;</c>. Exception handlers
    ///     and the handler invoker are closed over it.
    /// </summary>
    public ITypeSymbol Response { get; }

    /// <summary>The query result, the <c>CommandResult&lt;TValue&gt;</c> of a value-returning command, or the streamed item; null for a command.</summary>
    public ITypeSymbol? ResultOrItem { get; }
}
