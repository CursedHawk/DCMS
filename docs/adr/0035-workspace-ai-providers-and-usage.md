# ADR 0035: Several AI providers per workspace, and a record of what each was used for

**Status:** accepted (2026-11-16) · kanban epic #645

## Context

**Before this:**
- A workspace had one AI provider (`ai.tenant_ai_settings`: kind, model, base URL, key).
- A member could override it with their own key (`ai.user_ai_settings`).
- What was spent lived only in Redis day counters, kept to enforce the budget (`AiQuota`), and in Prometheus. Neither is per provider, and neither lasts.

**Asked for (2026-11-16):**
- several providers per workspace, switchable when an agent is started;
- a settings page charting the workspace's AI usage: one chart per provider, plus charts summarizing and comparing them all;
- a member who overrides the AI settings sees that, and what it spent.

## Decision

1. **Providers.**
   - `ai.providers` (`AiProviderConfig`): name, kind, model, base URL and Transit-encrypted key. There can be several per workspace, at most 20, with exactly one default (a partial unique index).
   - The migration moved each workspace's configured provider in as its default and dropped those columns from `tenant_ai_settings`, which keeps only the agent policy.
   - A workspace with no provider uses the platform's.
   - Managed at `/api/admin/ai/providers` (`ai:settings`).
2. **Picking.**
   - A conversation carries `ProviderId`, chosen in the composer before its first message and fixed after: thinking blocks are bound to the model that wrote them.
   - Every turn sends it as `?provider=` on `/api/admin/ai/messages`, and admin-api passes it to ai-gateway.
   - The gateway finds it only within the calling tenant. An id from elsewhere is `unknown_provider`.
   - Deleting a provider moves its conversations back to the default.
3. **Resolution (`AiProviderResolver`).**
   - **A picked provider is used whole:** the member's own settings do not apply to it. The platform's values fill only what it leaves unset.
   - **With nothing picked,** the chain is unchanged: the member's settings, then the default provider, then the platform's.
   - The base-URL/key scope rule stands, with one change: a local provider (Ollama, LM Studio) without a key is sent none instead of failing the scope check. That check used to refuse a workspace Ollama that had no key of its own.
4. **Usage.**
   - ai-gateway upserts one `ai.usage` row per day, payer, provider and model after every call, failed calls included. Each row holds calls, input, output, cache write and cache read tokens.
   - Payer (`Source`) is who owns the key: `workspace`, `platform` or `user`.
   - User rows carry the member's id and no provider. Everyone else's rows carry the workspace provider that served them, and its name at the time, so a deleted provider keeps a label.
   - It is best effort: a row that fails to write is logged, never turned into a failed call.
5. **Who sees what (`GET /api/admin/ai/usage`).**
   - The workspace's rows need `ai:settings`.
   - Every member sees their own key's rows, and the settings that override the workspace's. Nobody sees another member's own key.
   - The page, Settings → AI usage, shows:
     - all providers stacked per day;
     - their totals compared, as a chart and a table;
     - one chart per provider, split by token kind.

## Consequences

- **Rows** are tiny and daily, with no retention sweep. They are deleted with the tenant, and count in `TenantUsageMap`.
- **The budget** is still enforced by the Redis counters. This table is history, not a limit.
- **The site chatbot** has no picker. It runs on the default provider, and its calls are recorded like any other.
