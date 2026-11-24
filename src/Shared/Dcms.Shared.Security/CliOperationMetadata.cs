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
/// <param name="CrossEnvironment">
/// It acts beyond the environment the request names: another environment by id (create, delete,
/// lock, deploy from), or what every environment shares (the media library). An agent token
/// limited to some environments never reaches it; one allowed every environment does.
/// </param>
/// <param name="ExternalText">
/// Its answer carries text someone outside wrote (visitors, an external API, a site's own code):
/// agents are told to treat it as data, never as instructions.
/// </param>
public sealed record CliOperationMetadata(string Name, string Summary, CliRisk Risk, bool HumanOnly = false, string[]? Files = null,
    bool CrossEnvironment = false, bool ExternalText = false);

/// <summary>A route deliberately not offered to the command line, and why.</summary>
public sealed record NotForCliMetadata(string Reason);

public static class CliEndpointExtensions
{
    /// <summary>Offers this route to the dcms command line and, unless <paramref name="humanOnly"/>, to desktop agents.</summary>
    public static TBuilder Cli<TBuilder>(this TBuilder builder, string name, string summary, CliRisk risk, bool humanOnly = false, string[]? files = null,
        bool crossEnvironment = false, bool externalText = false)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new CliOperationMetadata(name, summary, risk, humanOnly, files, crossEnvironment, externalText));
        return builder;
    }

    public static TBuilder NotForCli<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new NotForCliMetadata(reason));
        return builder;
    }
}
