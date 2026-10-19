import { useMemo } from 'react';
import type { ToolSpec } from '../../agent/contracts';
import { useContractTools } from '../../assistant/contractTools';
import type { AssistantTool } from '../../assistant/tools';
import { fromAssistantTool, type TenantToolContext } from '../../ide/agent/tenantTools';

/**
 * The Dynamic Apps model tools the site agent gets: enough to read an app and to propose the
 * tables a site needs, nothing that changes what the app serves.
 *
 * <p>They come from the server's AI catalog (`dynamic-apps.config@1`), so an app whose "AI tools"
 * switch is off, or a member without its model permissions, simply has none. Of its operations
 * the site agent keeps the reads, draft preview and validation, and applying a change set — which
 * writes to the app's draft only. Publishing, discarding, rolling back and running flows stay
 * with the app's own Configuration screen and the console assistant: a site build that rewrote
 * the live data model on the way would be a much larger thing than the author asked for.</p>
 *
 * <p>Applying a change set asks the author first even in the default mode (the operation itself
 * is only "safe"): the author decided that the site agent proposes model changes and they
 * approve them (2026-10-19).</p>
 */
const KEPT = /_config_(get_summary|get_table|get_public_api|preview_draft|validate_draft|apply_change_set)$/;

export function siteAgentAppTools(contract: readonly AssistantTool[]): ToolSpec<TenantToolContext>[] {
  return contract
    .filter((tool) => KEPT.test(tool.name))
    .map((tool) => fromAssistantTool(tool.name.endsWith('_apply_change_set') ? { ...tool, risk: 'dangerous' } : tool));
}

export function useSiteAgentAppTools(): ToolSpec<TenantToolContext>[] {
  const contract = useContractTools();
  return useMemo(() => siteAgentAppTools(contract), [contract]);
}
