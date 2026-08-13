using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.UnitTests.Messaging;

public class NatsEmailQueueTests
{
    [Fact]
    public async Task Publishes_one_message_per_recipient()
    {
        var publisher = new RecordingPublisher();
        var queue = new NatsEmailQueue(publisher, NullLogger<NatsEmailQueue>.Instance);

        await queue.EnqueueAsync(new EmailMessage(
            Recipients: ["a@example.com", "b@example.com"],
            Subject: "New enquiry",
            HtmlBody: "<p>hi</p>",
            ReplyTo: "visitor@example.com",
            Purpose: "form-notification"), TestContext.Current.CancellationToken);

        // One message per address, so a retry after a bounce can't re-send to the
        // recipients that already succeeded.
        publisher.Published.Should().HaveCount(2);
        publisher.Published.Should().OnlyContain(p => p.Subject == Subjects.EmailSend);
        publisher.Published.Select(p => p.Event.To).Should().Equal("a@example.com", "b@example.com");
        publisher.Published.Should().OnlyContain(p =>
            p.Event.Subject == "New enquiry" &&
            p.Event.HtmlBody == "<p>hi</p>" &&
            p.Event.ReplyTo == "visitor@example.com" &&
            p.Event.Purpose == "form-notification");
    }

    [Fact]
    public async Task Trims_addresses_and_skips_blank_ones()
    {
        var publisher = new RecordingPublisher();
        var queue = new NatsEmailQueue(publisher, NullLogger<NatsEmailQueue>.Instance);

        await queue.EnqueueAsync(new EmailMessage(
            Recipients: ["  a@example.com  ", "", "   "],
            Subject: "s",
            HtmlBody: "b"), TestContext.Current.CancellationToken);

        publisher.Published.Should().ContainSingle()
            .Which.Event.To.Should().Be("a@example.com");
    }

    [Fact]
    public async Task Dedupe_key_is_scoped_to_the_recipient()
    {
        var publisher = new RecordingPublisher();
        var queue = new NatsEmailQueue(publisher, NullLogger<NatsEmailQueue>.Instance);

        await queue.EnqueueAsync(new EmailMessage(
            Recipients: ["a@example.com", "b@example.com"],
            Subject: "s",
            HtmlBody: "b",
            DedupeKey: "form-submission:42"), TestContext.Current.CancellationToken);

        // Per-recipient, otherwise JetStream would discard every address after the
        // first as a duplicate of the same id.
        publisher.Published.Select(p => p.MessageId).Should().Equal(
            "form-submission:42:a@example.com",
            "form-submission:42:b@example.com");
    }

    [Fact]
    public async Task Publishes_without_a_message_id_when_no_dedupe_key_is_given()
    {
        var publisher = new RecordingPublisher();
        var queue = new NatsEmailQueue(publisher, NullLogger<NatsEmailQueue>.Instance);

        await queue.EnqueueAsync(new EmailMessage(["a@example.com"], "s", "b"), TestContext.Current.CancellationToken);

        publisher.Published.Should().ContainSingle().Which.MessageId.Should().BeNull();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<(string Subject, EmailRequested Event, string? MessageId)> Published { get; } = [];

        public ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
            where T : IDcmsEvent
        {
            Published.Add((subject, (EmailRequested)(object)@event!, messageId));
            return ValueTask.CompletedTask;
        }
    }
}
