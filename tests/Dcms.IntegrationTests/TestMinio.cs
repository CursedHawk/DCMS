using Testcontainers.Minio;

namespace Dcms.IntegrationTests;

/// <summary>
/// The throwaway object store every media-touching fixture boots against.
///
/// <para><b>Pulled from this project's own registry.</b> MinIO stopped publishing images: first
/// its Docker Hub repositories (<c>minio/minio</c>, <c>minio/mc</c>) and then quay.io/minio/*
/// began refusing anonymous pulls, each time failing every fixture here at once (56 tests) for a
/// reason outside this repository. The image is now the official build, mirrored into
/// <c>mirror/</c> of the GitLab registry. CI authenticates through <c>DOCKER_AUTH_CONFIG</c>
/// (the integration job builds it from the job token); a developer machine needs
/// <c>docker login registry-gitlab.highgeek.eu</c> once.</para>
///
/// <para>Shared here rather than repeated in each fixture: four copies of an image literal is
/// exactly how the next registry change turns into four separate discoveries. <c>docker-compose.yml</c>
/// carries the same two images for the deployed stack and was moved with it.</para>
/// </summary>
internal static class TestMinio
{
    /// <summary>Keep in lockstep with the <c>minio</c> service in docker-compose.yml.</summary>
    public const string Image = "registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/minio:RELEASE.2025-09-07T16-13-09Z";

    public static MinioContainer Build() => new MinioBuilder(Image).Build();
}
