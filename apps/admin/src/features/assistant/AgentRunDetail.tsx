import { answerSite, approveSite, runFiles, useAgentHost, useWatchAgent } from '../agent/hostStore';
import { QuestionCard } from '../agent/QuestionCard';
import { ApprovalCard } from '../ide/agent/ApprovalCard';
import { Transcript } from '../ide/agent/Transcript';

/** One site agent's transcript, pending approval and questions, inside the dock's Agents tab. */
export default function AgentRunDetail({ siteKey }: { siteKey: string }) {
  const entries = useAgentHost((state) => state.sessions[siteKey]?.entries);
  const pending = useAgentHost((state) => state.sessions[siteKey]?.pending);
  const question = useAgentHost((state) => state.sessions[siteKey]?.question);
  useWatchAgent(siteKey);

  return (
    <div className="max-h-[50vh] overflow-y-auto">
      <Transcript entries={entries ?? []} />
      {pending ? (
        <div className="px-3 pb-3">
          <ApprovalCard calls={pending.tools} files={runFiles(siteKey)} onDecide={(decision) => approveSite(siteKey, decision)} />
        </div>
      ) : null}
      {question ? (
        <QuestionCard key={question.id} questions={question.questions} onAnswer={(answers) => answerSite(siteKey, answers)} />
      ) : null}
    </div>
  );
}
