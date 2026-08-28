using Dcms.Shared.Data.Ai;

namespace Dcms.UnitTests.Data;

/// <summary>
/// The write-side half of the AI base-URL fix. A base URL is an outbound destination a tenant
/// user chooses, and ai-gateway attaches an API key to every request it sends there — so the
/// shapes that must never be storable are the ones that reach inward (the compose network,
/// the cloud metadata endpoint) or that carry credentials of their own.
/// </summary>
public class AiBaseUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_is_valid_and_means_use_the_default(string? value)
        => AiBaseUrl.Validate(value, AiProvider.Anthropic).Should().BeNull();

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://gateway.example.com/anthropic")]
    [InlineData("https://api.openai.com/v1")]
    public void A_public_https_endpoint_is_accepted(string value)
        => AiBaseUrl.Validate(value, AiProvider.Anthropic).Should().BeNull();

    [Theory]
    // The compose network, which is what the SSRF reached for.
    [InlineData("http://vault:8200")]
    [InlineData("http://admin-api:8080")]
    [InlineData("http://forgejo:3000")]
    // Cloud instance metadata.
    [InlineData("http://169.254.169.254/latest/meta-data")]
    // Private and loopback ranges, v4 and v6.
    [InlineData("http://10.0.0.5")]
    [InlineData("http://172.16.4.1")]
    [InlineData("http://192.168.1.10")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:8200")]
    [InlineData("https://localhost:8200")]
    [InlineData("http://[::1]:8200")]
    [InlineData("https://[fd00::1]/v1")]
    public void An_inward_destination_is_refused_for_a_hosted_provider(string value)
        => AiBaseUrl.Validate(value, AiProvider.Anthropic).Should().NotBeNull();

    [Fact]
    public void Plain_http_is_refused_for_a_hosted_provider()
    {
        // This is what actually keeps the compose network out of reach: a hostname that
        // resolves inward is still just a hostname, but nothing on that network speaks TLS.
        AiBaseUrl.Validate("http://api.anthropic.com", AiProvider.Anthropic)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://192.168.1.50:1234/v1")]
    public void A_local_provider_may_use_a_private_plaintext_address(string value)
    {
        // Ollama and LM Studio are private by definition and are never sent a real credential,
        // so the hosted-provider rules would make them unusable rather than safer.
        AiBaseUrl.Validate(value, AiProvider.Ollama).Should().BeNull();
        AiBaseUrl.Validate(value, AiProvider.LmStudio).Should().BeNull();
    }

    [Theory]
    [InlineData("https://user:pass@proxy.example.com")]
    [InlineData("http://user:pass@localhost:11434")]
    public void Embedded_credentials_are_refused_for_every_provider(string value)
    {
        // They would be sent on every call and would sit in the settings row in plaintext,
        // outside the Transit-encrypted key column.
        AiBaseUrl.Validate(value, AiProvider.Anthropic).Should().NotBeNull();
        AiBaseUrl.Validate(value, AiProvider.Ollama).Should().NotBeNull();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("api.anthropic.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    public void A_non_http_or_relative_value_is_refused(string value)
        => AiBaseUrl.Validate(value, AiProvider.Anthropic).Should().NotBeNull();

    [Fact]
    public void Inherit_is_held_to_the_hosted_rules()
    {
        // Inherit resolves to whatever the tenant or platform configured, which may well be
        // Anthropic — so it cannot be the lenient branch.
        AiBaseUrl.Validate("http://vault:8200", AiProvider.Inherit).Should().NotBeNull();
        AiBaseUrl.Validate("https://api.anthropic.com", AiProvider.Inherit).Should().BeNull();
    }
}
