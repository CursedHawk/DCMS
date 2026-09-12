using Testcontainers.Minio;

namespace Dcms.IntegrationTests;

/// <summary>
/// The throwaway object store every media-touching fixture boots against.
///
/// <para><b>Pulled from quay.io, not Docker Hub.</b> MinIO's Docker Hub repositories
/// (<c>minio/minio</c> and <c>minio/mc</c>) stopped serving anonymous pulls, so every fixture
/// using them began failing with "pull access denied ... may require 'docker login'" — 56 tests
/// at once, none of them for a reason in this repository. quay.io is MinIO's own registry and
/// serves the same images without credentials, which keeps CI and a fresh developer machine
/// working with no secret to distribute.</para>
///
/// <para>Shared here rather than repeated in each fixture: four copies of an image literal is
/// exactly how the next registry change turns into four separate discoveries. <c>docker-compose.yml</c>
/// carries the same two images for the deployed stack and was moved with it.</para>
/// </summary>
internal static class TestMinio
{
    /// <summary>Keep in lockstep with the <c>minio</c> service in docker-compose.yml.</summary>
    public const string Image = "quay.io/minio/minio:latest";

    public static MinioContainer Build() => new MinioBuilder(Image).Build();
}
