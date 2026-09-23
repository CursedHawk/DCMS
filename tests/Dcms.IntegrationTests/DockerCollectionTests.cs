using System.Reflection;

namespace Dcms.IntegrationTests;

/// <summary>
/// Every <c>[Collection]</c> in this suite shares a fixture that starts containers. A test in one
/// of them that is a plain <c>[Fact]</c> rather than a <see cref="DockerFactAttribute"/> does not
/// skip on a runner without Docker, so xUnit constructs the fixture for it — which throws, and
/// fails every test in the collection, the skipped ones included.
///
/// <para>That is not hypothetical: one source-scanning guard placed in the AdminApi collection
/// turned 68 skips into 68 failures and stopped the pipeline at its first stage, while every run
/// on a machine with Docker stayed green. So the rule is checked, not remembered.</para>
/// </summary>
public sealed class DockerCollectionTests
{
    [Fact]
    public void Every_test_in_a_container_fixture_collection_skips_without_Docker()
    {
        var offenders = typeof(DockerCollectionTests).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<CollectionAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.GetCustomAttribute<FactAttribute>(inherit: true) is { } fact
                                 && fact is not DockerFactAttribute)
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToList();

        offenders.Should().BeEmpty(
            "a plain [Fact] in a Docker-fixture collection builds that fixture on a runner without "
            + "Docker, and its failure fails the whole collection; use [DockerFact], or move the test "
            + "to a class with no [Collection]");
    }
}
