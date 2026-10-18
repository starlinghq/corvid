using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Corvid.Tools.Generators;

// The incremental pipeline caches on value equality, so nothing below holds a symbol, syntax node or Location.

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items) => _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Length => Items.Length;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in Items)
        {
            hash = (hash * 31) + item.GetHashCode();
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location) =>
        location is null || location.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(), Arguments.Items.ToArray<object>());
}

/// <summary>One [Tool] method, already rendered to the C# fragments the emitter needs.</summary>
internal sealed record ToolModel(
    string TypeMetadataName,
    string Namespace,
    string TypeName,
    string TypeKeyword,
    bool TypeIsStatic,
    string ToolName,
    string ClassName,
    string DescriptorExpression,
    string Body,
    bool IsAsync,
    string Helpers,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics);
