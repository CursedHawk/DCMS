/** The translate function the screens hold (usePluginT). */
type TFunction = (key: string, options?: Record<string, unknown>) => string;

/** One trigger, as a person reads it: "When crm.deals is updated", "Every 15m". */
export function describeTrigger(trigger: Record<string, unknown>, t: TFunction): string {
  const s = (key: string) => String(trigger[key] ?? '');
  switch (s('kind')) {
    case 'row':
      return t('triggers.row', { table: s('table'), event: t(`triggers.events.${s('event')}`) });
    case 'relation':
      return t('triggers.relation', { relationship: s('relationship'), event: t(`triggers.events.${s('event')}`) });
    case 'message':
      return t('triggers.message', { topic: s('topic') });
    case 'schedule':
      return t('triggers.schedule', { cron: s('cron') });
    case 'every':
      return t('triggers.every', { interval: s('interval') });
    case 'event':
      return t('triggers.event', { name: s('name') });
    default:
      return s('kind');
  }
}
