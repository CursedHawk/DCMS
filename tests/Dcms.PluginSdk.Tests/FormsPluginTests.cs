using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Forms;

namespace Dcms.PluginSdk.Tests;

public class FormsPluginTests
{
    private const string BookingConfig = """
        {
          "forms": [
            {
              "name": "booking",
              "title": "Booking request",
              "successMessage": "We'll be in touch within 24 hours.",
              "fields": [
                { "name": "name", "label": "Name", "type": "text", "required": true, "maxLength": 200 },
                { "name": "email", "label": "Email", "type": "email", "required": true },
                { "name": "eventDate", "label": "Event date", "type": "date", "required": true },
                { "name": "venue", "label": "Venue", "type": "text" },
                { "name": "message", "label": "Message", "type": "textarea", "maxLength": 4000 }
              ]
            }
          ]
        }
        """;

    private static PluginInstanceContext Instance(string config)
        => new(Guid.NewGuid(), Guid.NewGuid(), "forms", "moordoor-forms", "Moordoor forms",
            "Booking requests from the Moordoor site.", JsonDocument.Parse(config));

    [Fact]
    public void Reads_the_forms_declared_on_an_instance()
    {
        var forms = FormsPlugin.ReadForms(JsonDocument.Parse(BookingConfig));

        forms.Should().HaveCount(1);
        var booking = forms[0];
        booking.Name.Should().Be("booking");
        booking.SuccessMessage.Should().Be("We'll be in touch within 24 hours.");
        booking.Fields.Select(f => f.Name).Should()
            .BeEquivalentTo(["name", "email", "eventDate", "venue", "message"]);
    }

    [Fact]
    public void Defaults_field_type_and_max_length_when_omitted()
    {
        var forms = FormsPlugin.ReadForms(JsonDocument.Parse(BookingConfig));

        var venue = forms[0].Fields.Single(f => f.Name == "venue");
        venue.Type.Should().Be("text");
        venue.Required.Should().BeFalse();
        venue.MaxLength.Should().Be(2000);
    }

    [Fact]
    public void Reads_no_forms_from_an_unconfigured_instance()
    {
        FormsPlugin.ReadForms(JsonDocument.Parse("{}")).Should().BeEmpty();
    }

    [Fact]
    public void Reads_no_notification_when_the_form_declares_none()
    {
        FormsPlugin.ReadForms(JsonDocument.Parse(BookingConfig))[0].Notify.Should().BeNull();
    }

    [Fact]
    public void Reads_the_notification_settings_of_a_form()
    {
        var config = NotifyConfig("""
            { "enabled": true, "recipients": ["ops@example.com", " booking@example.com "],
              "subject": "New booking", "replyToField": "email" }
            """);

        var notify = FormsPlugin.ReadForms(JsonDocument.Parse(config))[0].Notify;

        notify.Should().NotBeNull();
        notify!.Recipients.Should().BeEquivalentTo(["ops@example.com", "booking@example.com"]);
        notify.Subject.Should().Be("New booking");
        notify.ReplyToField.Should().Be("email");
    }

    [Theory]
    // Off, and on-but-nowhere-to-send, are both "do not notify" — the latter is
    // what a half-finished config looks like while an operator is editing it.
    [InlineData("""{ "enabled": false, "recipients": ["ops@example.com"] }""")]
    [InlineData("""{ "enabled": true, "recipients": [] }""")]
    [InlineData("""{ "enabled": true }""")]
    public void Reads_no_notification_when_it_would_have_nowhere_to_go(string notify)
    {
        FormsPlugin.ReadForms(JsonDocument.Parse(NotifyConfig(notify)))[0].Notify.Should().BeNull();
    }

    private static string NotifyConfig(string notify) => $$"""
        {
          "forms": [
            {
              "name": "booking",
              "fields": [{ "name": "email", "type": "email" }],
              "notify": {{notify}}
            }
          ]
        }
        """;

    [Fact]
    public void Documents_a_post_per_declared_form()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new FormsPlugin()]));

        var doc = assembler.Build("Moordoor", [Instance(BookingConfig)]);

        var submit = doc["paths"]!["/api/moordoor-forms/forms/booking"]!["post"]!;
        submit.Should().NotBeNull();
        submit["description"]!.GetValue<string>().Should().Contain("Booking requests from the Moordoor site.");

        var body = doc["components"]!["schemas"]!["moordoor-forms_booking_submission"]!;
        body["properties"]!.AsObject().Should().ContainKeys("name", "email", "eventDate", "venue", "message");
        body["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should()
            .BeEquivalentTo(["name", "email", "eventDate"]);
    }

    [Fact]
    public void Declares_no_content_routes()
    {
        // Submissions are writes served by content-api, not published content.
        var routes = new PluginRouteTable(new PluginRegistry([new FormsPlugin()]));

        routes.Find("forms", "booking").Should().BeNull();
    }
}
