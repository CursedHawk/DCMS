using Microsoft.AspNetCore.Builder;

namespace Dcms.Shared.Security;

/// <summary>What an operation can change (ADR 0032): the same three steps as a contract's <c>OpRisk</c>.</summary>
public enum CliRisk
{
    /// <summary>Reads only; a read-only agent may call it.</summary>
    Read,

    /// <summary>Changes something that can be changed back.</summary>
    Safe,

    /// <summary>Deletes, publishes, or otherwise cannot simply be undone.</summary>
    Dangerous,

    /// <summary>
    /// The route dispatches to operations that each say their own risk (the contracts proxy):
    /// the route itself refuses what a read-only agent may not do.
    /// </summary>
    PerOperation,
}

/// <summary>
/// A route the dcms command line and desktop agents may call (ADR 0032): its stable name, what it
/// does, and what it can change. <paramref name="HumanOnly"/> keeps it to a person's own sign-in:
/// an agent token never reaches it, whatever the person's permissions.
/// </summary>
/// <param name="Files">A multipart route's file fields (<c>["file"]</c>); its other form fields are named in the summary.</param>
public sealed record CliOperationMetadata(string Name, string Summary, CliRisk Risk, bool HumanOnly = false, string[]? Files = null);

/// <summary>A route deliberately not offered to the command line, and why.</summary>
public sealed record NotForCliMetadata(string Reason);

public static class CliEndpointExtensions
{
    /// <summary>Offers this route to the dcms command line and, unless <paramref name="humanOnly"/>, to desktop agents.</summary>
    public static TBuilder Cli<TBuilder>(this TBuilder builder, string name, string summary, CliRisk risk, bool humanOnly = false, string[]? files = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new CliOperationMetadata(name, summary, risk, humanOnly, files));
        return builder;
    }

    public static TBuilder NotForCli<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new NotForCliMetadata(reason));
        return builder;
    }
}
