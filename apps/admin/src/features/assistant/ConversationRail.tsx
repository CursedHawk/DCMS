import { MessageSquarePlus, Trash2, Users } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { can, relativeTime } from '@dcms/core';
import { Button, Spinner, cn, usePermissions } from '@dcms/ui';
import { Perm } from '../../lib/permissions';
import {
  type AiSurface,
  type ConversationScope,
  type ConversationSummary,
  useConversations,
  useDeleteConversation,
} from './conversations';

/**
 * Past work, grouped by whose it is.
 *
 * <p>Three tabs rather than one list with owner labels: they answer different questions. Mine
 * is "what was I doing"; Shared is "what has the team agreed the assistant did"; All exists
 * only for the role that answers for the workspace, and is the reason `ai:chats:read-all` is a
 * permission rather than an assumption about owners.</p>
 *
 * <p><b>Shared by both surfaces, scoped by `surface`.</b> The console dock and the IDE agent
 * store their transcripts in one table, so this component serves both — but each instance shows
 * one surface only. Without that the IDE's many short runs would bury the console's few long
 * conversations, which is the one real argument against a shared table and is answered here.</p>
 */
export function ConversationRail({
  scope,
  onScopeChange,
  selectedId,
  onSelect,
  onNew,
  surface = 'console',
  siteId,
}: {
  scope: ConversationScope;
  onScopeChange: (scope: ConversationScope) => void;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  /** Which surface's history this rail shows. Defaults to the console, its first caller. */
  surface?: AiSurface;
  /** Narrows an IDE rail to one site. Ignored on the console surface. */
  siteId?: string;
}) {
  const { t } = useTranslation();
  const me = usePermissions();
  const canReadAll = can(me, Perm.AiChatsReadAll);
  const conversations = useConversations(scope, surface, { siteId });
  const remove = useDeleteConversation();

  const scopes: ConversationScope[] = canReadAll
    ? ['mine', 'workspace', 'all']
    : ['mine', 'workspace'];

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-1 border-b px-2 py-2">
        <Button size="sm" variant="subtle" className="h-7 flex-1 justify-start" onClick={onNew}>
          <MessageSquarePlus className="h-3.5 w-3.5" aria-hidden />
          {t('assistant.newChat')}
        </Button>
      </div>

      <div className="flex gap-0.5 border-b px-2 py-1.5" role="tablist">
        {scopes.map((option) => (
          <button
            key={option}
            type="button"
            role="tab"
            aria-selected={scope === option}
            onClick={() => onScopeChange(option)}
            className={cn(
              'rounded-sm px-2 py-1 text-xs transition-colors',
              scope === option
                ? 'bg-accent font-medium text-accent-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {t(`assistant.scope.${option}`)}
          </button>
        ))}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-1.5">
        {conversations.isPending ? (
          <div className="p-4">
            <Spinner />
          </div>
        ) : conversations.data?.length ? (
          <ul className="space-y-px">
            {conversations.data.map((conversation) => (
              <Row
                key={conversation.id}
                conversation={conversation}
                selected={conversation.id === selectedId}
                onSelect={() => onSelect(conversation.id)}
                onDelete={
                  conversation.mine ? () => void remove.mutateAsync(conversation.id) : undefined
                }
              />
            ))}
          </ul>
        ) : (
          <p className="px-3 py-6 text-xs text-muted-foreground">
            {t(`assistant.noneIn.${scope}`)}
          </p>
        )}
      </div>
    </div>
  );
}

function Row({
  conversation,
  selected,
  onSelect,
  onDelete,
}: {
  conversation: ConversationSummary;
  selected: boolean;
  onSelect: () => void;
  onDelete?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <li className="group relative">
      <button
        type="button"
        onClick={onSelect}
        className={cn(
          'w-full rounded-sm px-2 py-1.5 pr-7 text-left transition-colors',
          selected ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/60',
        )}
      >
        <span className="flex items-center gap-1.5">
          {conversation.visibility === 'Workspace' ? (
            <Users
              className="h-3 w-3 shrink-0 text-muted-foreground"
              aria-label={t('assistant.sharedWithWorkspace')}
            />
          ) : null}
          <span className="min-w-0 flex-1 truncate text-[13px]">{conversation.title}</span>
        </span>
        <span className="mt-0.5 block truncate font-mono text-[10px] text-muted-foreground">
          {relativeTime(conversation.updatedAt)} · {conversation.messageCount}
          {/* The branch an IDE conversation was last run against. Absent on the console
              surface, where there is nothing for it to mean. */}
          {conversation.branch ? ` · ${conversation.branch}` : ''}
        </span>
      </button>
      {onDelete ? (
        <button
          type="button"
          onClick={onDelete}
          aria-label={t('assistant.deleteChat', { title: conversation.title })}
          className="absolute right-1 top-1.5 hidden rounded p-1 text-muted-foreground hover:text-destructive group-hover:block"
        >
          <Trash2 className="h-3 w-3" aria-hidden />
        </button>
      ) : null}
    </li>
  );
}
