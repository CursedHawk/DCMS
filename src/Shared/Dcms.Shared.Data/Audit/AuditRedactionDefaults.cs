using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// The list of entity types whose field values may appear verbatim in an audit record.
///
/// <para>Everything not named here is still recorded — the action, the actor, the table, which
/// fields changed — but with no values. That default is deliberate and it is the whole reason
/// this file is a list rather than a rule: adding a column that holds something sensitive
/// should not silently start publishing it, and adding a table should not either. Whoever adds
/// one comes here, reads the row above theirs, and makes a decision.</para>
///
/// <para>Each entry names the resource type the record carries — the same vocabulary
/// <c>.WithAudit(..., resourceType)</c> uses at the endpoints, so an EF-derived record and an
/// endpoint-derived one filter alike — the property that reads as a human-friendly label, and
/// any field the name heuristic would not catch on its own.</para>
/// </summary>
public static class AuditRedactionDefaults
{
    public static AuditRedactor Apply(this AuditRedactor redactor)
    {
        // ---- tenancy: who is in a tenant and what they may do ----
        redactor.Allow(typeof(Tenant), "tenant", nameof(Tenant.Name));
        redactor.Allow(typeof(TenantMembership), "membership", nameof(TenantMembership.Email));
        redactor.Allow(typeof(TenantRole), "role", nameof(TenantRole.Name));
        redactor.Allow(typeof(TenantRolePermission), "role_permission", nameof(TenantRolePermission.Permission));
        redactor.Allow(typeof(MemberRole), "member_role");
        redactor.Allow(typeof(Domain), "domain", nameof(Domain.Hostname));

        // The token is the invitation's bearer credential in hashed form; the heuristic already
        // catches TokenHash, and it is named again here so removing the heuristic cannot quietly
        // start disclosing it.
        redactor.Allow(
            typeof(Invitation), "invitation", nameof(Invitation.Email),
            deny: [nameof(Invitation.TokenHash)]);

        // ---- content and media ----
        redactor.Allow(typeof(ContentItem), "content_item", nameof(ContentItem.Slug));
        redactor.Allow(typeof(PluginInstance), "plugin_instance", nameof(PluginInstance.Name));
        redactor.Allow(typeof(ScheduledPublish), "scheduled_publish");
        redactor.Allow(typeof(MediaAsset), "media_asset", nameof(MediaAsset.FileName));
        redactor.Allow(typeof(MediaFolder), "media_folder", nameof(MediaFolder.Name));
        redactor.Allow(typeof(MediaVariant), "media_variant", nameof(MediaVariant.Kind));

        // A content version's body is the tenant's own data and can be megabytes of it. What
        // changed matters; reprinting the document into the audit log does not.
        redactor.Allow(
            typeof(ContentVersion), "content_version",
            omit: [nameof(ContentVersion.DataJson)]);

        // ---- sites ----
        // The definition blobs are whole documents. Their versions are recorded, so "the site
        // definition changed, from version 7 to 8" is answerable without carrying the payload.
        redactor.Allow(
            typeof(Site), "site", nameof(Site.Name),
            omit: [nameof(Site.DraftDefinitionJson)]);
        redactor.Allow(
            typeof(SiteBuild), "site_build",
            omit: [nameof(SiteBuild.DefinitionSnapshotJson)]);
        redactor.Allow(
            typeof(SiteDraft), "site_draft", nameof(SiteDraft.Branch),
            omit: [nameof(SiteDraft.DefinitionJson)]);

        // ---- AI ----
        // The key ciphertext is denied explicitly as well as by heuristic: the record that a
        // tenant's provider credentials were replaced is exactly the kind of thing an
        // investigation needs, and exactly the kind of value it must not carry.
        redactor.Allow(
            typeof(TenantAiSettings), "ai_settings",
            deny: [nameof(TenantAiSettings.ApiKeyCiphertext)]);
        redactor.Allow(
            typeof(UserAiSettings), "ai_user_settings",
            deny: [nameof(UserAiSettings.ApiKeyCiphertext)]);

        // ---- visitor-facing tables ----
        // These hold the tenant's end users, not the tenant's staff. Structural facts only:
        // an operator reading the audit log should not thereby read the mailing list, and a
        // submission body or a chat message is the visitor's content, not an administrative act.
        redactor.Allow(
            typeof(VisitorAccount), "visitor",
            deny: [nameof(VisitorAccount.Email), nameof(VisitorAccount.DisplayName)]);
        redactor.Allow(
            typeof(FormSubmission), "form_submission", nameof(FormSubmission.FormName),
            omit: [nameof(FormSubmission.DataJson), nameof(FormSubmission.UserAgent)]);
        redactor.Allow(
            typeof(ChatConversation), "chat_conversation",
            deny: [nameof(ChatConversation.VisitorName)]);
        redactor.Allow(
            typeof(ChatMessage), "chat_message",
            omit: [nameof(ChatMessage.Body)]);

        return redactor;
    }
}
