import { approveSite, runFiles, useAgentHost } from '../agent/hostStore';
import { ApprovalCard } from '../ide/agent/ApprovalCard';
import { Transcript } from '../ide/agent/Transcript';

/** One site agent's transcript and pending question, inside the dock's Agents tab. */
export default function AgentRunDetail({ siteKey }: { siteKey: string }) {
  const entries = useAgentHost((state) => state.sessions[siteKey]?.entries);
  const pending = useAgentHost((state) => state.sessions[siteKey]?.pending);

  return (
    <div className="max-h-[50vh] overflow-y-auto">
      <Transcript entries={entries ?? []} />
      {pending ? (
        <div className="px-3 pb-3">
          <ApprovalCard calls={pending.tools} files={runFiles(siteKey)} onDecide={(decision) => approveSite(siteKey, decision)} />
        </div>
      ) : null}
    </div>
  );
}
