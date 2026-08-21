using Dcms.Shared.Audit.Redaction;

namespace Dcms.UnitTests.Audit;

/// <summary>
/// The redaction policy is the difference between an audit log and a data leak with
/// timestamps. These tests pin the direction it fails in: unknown things are withheld, not
/// published.
/// </summary>
public sealed class AuditRedactorTests
{
    private sealed class Opted
    {
        public string Name { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string ApiKeyCiphertext { get; set; } = string.Empty;
        public string Denied { get; set; } = string.Empty;
        public string Boring { get; set; } = string.Empty;

        [AuditSensitive]
        public string Marked { get; set; } = string.Empty;

        [AuditIgnore]
        public int Counter { get; set; }
    }

    private sealed class NeverMentioned
    {
        public string Anything { get; set; } = string.Empty;
    }

    [AuditedAttribute("self_described", LabelProperty = nameof(Title))]
    private sealed class SelfDescribed
    {
        public string Title { get; set; } = string.Empty;
    }

    private static AuditRedactor Redactor() =>
        new AuditRedactor().Allow(
            typeof(Opted), "opted", nameof(Opted.Name), deny: [nameof(Opted.Denied)]);

    [Fact]
    public void A_type_nobody_opted_in_gives_up_field_names_and_nothing_else()
    {
        var change = Redactor().Describe(typeof(NeverMentioned), "Anything", "before", "after");

        change.Should().NotBeNull();
        change!.Field.Should().Be("Anything");
        change.Redacted.Should().BeTrue();
        change.Before.Should().BeNull();
        change.After.Should().BeNull("a type that was never reviewed must not publish its values");
    }

    [Fact]
    public void An_opted_in_type_records_ordinary_fields_verbatim()
    {
        var change = Redactor().Describe(typeof(Opted), nameof(Opted.Boring), "old", "new");

        change!.Redacted.Should().BeFalse();
        change.Before.Should().Be("old");
        change.After.Should().Be("new");
    }

    [Theory]
    [InlineData(nameof(Opted.PasswordHash))]
    [InlineData(nameof(Opted.ApiKeyCiphertext))]
    [InlineData(nameof(Opted.Denied))]
    [InlineData(nameof(Opted.Marked))]
    public void Secrets_are_named_as_changed_but_never_shown(string property)
    {
        var change = Redactor().Describe(typeof(Opted), property, "s3cret", "n3wsecret");

        change.Should().NotBeNull("\"this secret changed\" and \"this field did not change\" "
                                  + "are different facts and must stay distinguishable");
        change!.Redacted.Should().BeTrue();
        change.Before.Should().BeNull();
        change.After.Should().BeNull();
    }

    [Fact]
    public void An_ignored_field_leaves_the_diff_entirely()
    {
        Redactor().Describe(typeof(Opted), nameof(Opted.Counter), 1, 2).Should().BeNull();
    }

    [Fact]
    public void A_type_can_opt_itself_in_with_an_attribute()
    {
        var redactor = new AuditRedactor();

        redactor.IsAllowed(typeof(SelfDescribed)).Should().BeTrue(
            "a plugin's entity has no access to the platform's composition root");
        redactor.ResourceTypeFor(typeof(SelfDescribed)).Should().Be("self_described");
        redactor.LabelPropertyFor(typeof(SelfDescribed)).Should().Be(nameof(SelfDescribed.Title));
    }

    [Fact]
    public void Long_values_are_truncated_and_say_so()
    {
        var body = new string('x', AuditRedactor.MaxValueLength + 500);

        var change = Redactor().Describe(typeof(Opted), nameof(Opted.Boring), null, body);

        var after = change!.After.Should().BeOfType<string>().Subject;
        after.Should().StartWith(new string('x', 50));
        after.Should().Contain("+500 chars", "a reader has to be able to tell truncation from a short value");
        after.Length.Should().BeLessThan(body.Length);
    }

    [Fact]
    public void Values_are_normalised_into_shapes_json_carries_unambiguously()
    {
        var redactor = Redactor();
        var id = Guid.NewGuid();
        var instant = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.FromHours(2));

        redactor.Describe(typeof(Opted), nameof(Opted.Boring), null, id)!.After.Should().Be(id.ToString());
        redactor.Describe(typeof(Opted), nameof(Opted.Boring), null, instant)!.After.Should()
            .Be("2026-08-21T07:30:00.0000000+00:00", "instants are stored in UTC so two records compare directly");
        redactor.Describe(typeof(Opted), nameof(Opted.Boring), null, new byte[9])!.After.Should()
            .Be("<9 bytes>", "a blob's contents are never the interesting part of a diff");
    }

    [Fact]
    public void Registering_a_type_twice_is_not_an_error()
    {
        var redactor = Redactor();

        var act = () => redactor.Allow(typeof(Opted), "opted-again");

        act.Should().NotThrow("the same type may be registered by a library default and by a plugin");
        redactor.ResourceTypeFor(typeof(Opted)).Should().Be("opted-again");
    }
}
