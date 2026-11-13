# ADR 0034: Whole-conversation compaction and the AI knowledge store

**Status:** accepted (2026-11-13) · kanban epic #640 · replaces the pruning in `agent/context.ts` described in [`docs/ai-agent.md`](../ai-agent.md)

## Context

**The old pruning.**
- Long agent conversations were kept small by `pruneHistory`. It blanked old tool results, dropped whole exchanges from the front, and prepended a few remembered facts.
- It ran against a fixed 40k *estimated* budget that counted messages only. The provider's real token counts never fed back into it.
- It lost parts of the conversation outright. Each cut rewrote the front of the request, which emptied the prompt cache.
- The newest models also bind thinking blocks to the history they were produced in. A rewritten front has to be stripped of them, or the request is refused.

**What was asked for instead (2026-11-13):**
- compaction that happens by itself;
- one that summarizes the *whole* conversation and continues from that summary, so caching works again;
- a place where agents keep what they learn: past decisions, information, setups. Agents load it only when they decide they need it.

Decisions taken with the user:
- **Writing:** agents save entries; people curate them.
- **Scope:** one store per workspace, with entries optionally tied to a site.
- **Saving decisions:** agents save on their own judgement, and compaction also extracts decisions automatically.
- **Structure:** entries live at folder-like paths. Compaction archives each compacted part of a conversation there, so an agent resuming afterwards can refer back to it.

## Decision

1. **Simple compaction, client-side** (`apps/admin/src/features/agent/compaction.ts`).
   - **Trigger:** compaction happens before a model call whose prompt would reach the tenant's threshold (platform console; 100 000 tokens by default).
   - **Measuring:** the size comes from the provider's own count for the previous call, plus an estimate of what was appended since. When the provider reports no counts, the whole request is estimated.
   - **The summary request:** the model is asked to summarize the whole conversation (the `compaction` prompt). That request is the previous one plus one appended message, so it is a cache read.
   - **Continuing:** the conversation continues from a single message, the *continuation*: the summary plus references to what was archived. No earlier turn and no earlier thinking block is sent again. That is the shape the API documents as surviving the history-editing check, and every request after it is append-only.
   - **When it may run:** only at a call boundary, where every tool call has its result. In the assistant it runs before the new question is appended, so the question follows the summary word for word.
   - **Why client-side:** we don't use the server-side compaction beta because ai-gateway also translates for OpenAI-compatible and local providers, and the bridge drops fields it does not know.
2. **The transcript stays whole.**
   - The continuation is stored as a message of kind `compaction` (`ai.messages.Kind`), and the model's context starts at the last one.
   - People still see everything, with a divider where the conversation was compacted.
   - Resuming starts from the last compaction.
3. **The knowledge store** (`ai.knowledge`, `AiKnowledgeEndpoints`, Settings → AI knowledge).
   - **Entries:** an entry has a path (`decisions/…`, `setups/…`, `info/…`, `conversations/…`), a title, a one-line description and a markdown body. It belongs to the workspace or to one site.
   - **Who sees what:** a site's agent sees the workspace's entries and its site's. The console assistant and the page see all of them.
   - **Search:** full text over title, description and body.
4. **Agents read it only when they ask.**
   - The tools are `knowledge_list`, `knowledge_search`, `knowledge_read`, `knowledge_save` and `conversation_recall`, in `agent/knowledgeTools.ts`. Site agents of every kind get them at every scope, and so does the assistant.
   - Each prompt says the store exists (the `knowledge` prompt), but none of its content is injected. That keeps the prefix stable and costs nothing on turns that do not need it.
5. **Compaction archives into it.**
   - The summary of each compacted part is saved at `conversations/<date>-<title>-<id>/part-<n>`, with the conversation and the seq range it covered. Each lasting fact the summarizer extracted is saved at its own path.
   - The continuation names both. `conversation_recall` searches the stored turns of an archived range (`GET /api/admin/ai/conversations/{id}/excerpts`), so detail a summary dropped can still be found. It is readable by whoever may read that conversation.
6. **Writing needs `ai:knowledge`; reading does not.**
   - Every member's agents read the store, so every member may.
   - What is written there is read by other members' agents. A store anyone could write would be a way to put words in all of them.
   - Owner holds the permission by default. Compaction archives only for members who hold it; for others the conversation is still compacted, but nothing is saved.
7. **An archive is as private as its conversation.**
   - An archive summarizes its conversation, and a console conversation can be private to its owner. So an entry linked to a conversation is listed, searched and read only by those who may read that conversation: its owner, everyone once it is shared, and holders of `ai:chats:read-all`.
   - Only the conversation's owner may link an entry to it, and nobody overwrites an archive they cannot see.
   - Once the conversation is deleted (retention), its archive is its creator's.
   - The facts the summarizer extracts are *not* linked. A decision is the workspace's to know, exactly as when an agent saves one itself, and saving them is what was asked for.
8. **Entries are information, not instructions.** The `knowledge` prompt says so, alongside the existing rules about tool output.
9. **The public site chatbot never sees the store.** It holds internal decisions. The chatbot also keeps its 12-message window, and gets no compaction.

## Consequences

- **Cost:** a long conversation costs one summary call per compaction, which is mostly a cache read. After that its requests read their prefix from the cache again instead of missing on every cut.
- **What survives a compaction** is the summary. Thinking from before it is not carried forward. The compaction prompt says what a summary must keep, and recall reaches back into the stored turns.
- **The knowledge store grows** with every compaction. People prune it on the Knowledge page, and it counts toward the tenant's AI usage (`TenantUsageMap`).
- **Recall** scans at most 2 000 stored messages in memory. A full-text index on `ai.messages` would be the next step if ranges grow past that.
- **The assistant's archive** covers the whole conversation (no seq range), because it does not track sequence numbers.
