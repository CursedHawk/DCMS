import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Button,
  Checkbox,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
} from '@dcms/ui';
import { useVfs } from '../../site-source';
import { fileOf, problemOf, starterOf, type HttpAccess, type NewFunction, type Start } from './newFunction';

const STARTS: Start[] = ['http', 'manual', 'row', 'message', 'schedule', 'every'];
const ACCESS: HttpAccess[] = ['public', 'user', 'role', 'apiKey'];

const EMPTY: NewFunction = {
  language: 'ts',
  path: '',
  description: '',
  start: 'http',
  access: ['user'],
  table: '',
  event: 'updated',
  topic: '',
  cron: '0 * * * *',
  interval: '15m',
};

const select = 'h-9 w-full rounded-md border bg-background px-2 text-sm';

/**
 * "New function": the language, the path (which is the name and the HTTP address), what starts
 * it and who may call it. It writes the file the build expects there and opens it.
 */
export function NewFunctionDialog({ open, onOpenChange, app }: { open: boolean; onOpenChange: (open: boolean) => void; app: string }) {
  const { t } = useTranslation();
  const [f, setF] = useState<NewFunction>(EMPTY);
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<NewFunction>) => {
    setF((prev) => ({ ...prev, ...patch }));
    setError(null);
  };
  const problem = f.path ? problemOf(f) : null;
  const file = f.path && !problem ? fileOf(f) : null;

  const create = () => {
    if (problemOf(f) || !file) return;
    if (!useVfs.getState().createFile(file, starterOf(f))) {
      setError(t('ide.newFn.exists', { file }));
      return;
    }
    onOpenChange(false);
    setF(EMPTY);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent data-testid="new-function">
        <DialogHeader>
          <DialogTitle>{t('ide.newFn.title')}</DialogTitle>
        </DialogHeader>
        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault();
            create();
          }}
        >
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="new-fn-language">{t('ide.newFn.language')}</Label>
              <select id="new-fn-language" className={select} value={f.language} onChange={(e) => set({ language: e.target.value as NewFunction['language'] })}>
                <option value="ts">TypeScript</option>
                <option value="cs">C#</option>
              </select>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="new-fn-start">{t('ide.newFn.start')}</Label>
              <select id="new-fn-start" className={select} value={f.start} onChange={(e) => set({ start: e.target.value as Start })}>
                {STARTS.map((s) => (
                  <option key={s} value={s}>
                    {t(`ide.newFn.starts.${s}`)}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="new-fn-path">{t('ide.newFn.path')}</Label>
            <Input
              id="new-fn-path"
              autoComplete="off"
              placeholder="orders/total"
              value={f.path}
              onChange={(e) => set({ path: e.target.value.trim().toLowerCase() })}
            />
            <p className="text-xs text-muted-foreground">
              {file ? (
                <>
                  <span className="font-mono">{file}</span>
                  {f.start === 'http' && <span className="font-mono"> · /api/{app}/fn/{f.path}</span>}
                </>
              ) : (
                t('ide.newFn.pathHint')
              )}
            </p>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="new-fn-description">{t('ide.newFn.description')}</Label>
            <Input id="new-fn-description" value={f.description} onChange={(e) => set({ description: e.target.value })} />
          </div>

          {f.start === 'http' && (
            <fieldset className="space-y-1.5">
              <legend className="text-sm font-medium">{t('ide.newFn.access')}</legend>
              {ACCESS.map((a) => (
                <label key={a} className="flex items-center gap-2 text-sm">
                  <Checkbox
                    checked={f.access.includes(a)}
                    onCheckedChange={(on) => set({ access: on ? [...f.access, a] : f.access.filter((x) => x !== a) })}
                  />
                  {t(`ide.newFn.who.${a}`)}
                </label>
              ))}
            </fieldset>
          )}
          {f.start === 'row' && (
            <div className="grid grid-cols-[1fr_8rem] gap-3">
              <div className="space-y-1.5">
                <Label htmlFor="new-fn-table">{t('ide.newFn.table')}</Label>
                <Input id="new-fn-table" placeholder="crm.deals" value={f.table} onChange={(e) => set({ table: e.target.value.trim() })} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="new-fn-event">{t('ide.newFn.event')}</Label>
                <select id="new-fn-event" className={select} value={f.event} onChange={(e) => set({ event: e.target.value as NewFunction['event'] })}>
                  {(['created', 'updated', 'deleted'] as const).map((ev) => (
                    <option key={ev} value={ev}>
                      {t(`ide.newFn.events.${ev}`)}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}
          {f.start === 'message' && (
            <div className="space-y-1.5">
              <Label htmlFor="new-fn-topic">{t('ide.newFn.topic')}</Label>
              <Input id="new-fn-topic" placeholder="orders.paid" value={f.topic} onChange={(e) => set({ topic: e.target.value.trim() })} />
            </div>
          )}
          {f.start === 'schedule' && (
            <div className="space-y-1.5">
              <Label htmlFor="new-fn-cron">{t('ide.newFn.cron')}</Label>
              <Input id="new-fn-cron" className="font-mono" value={f.cron} onChange={(e) => set({ cron: e.target.value })} />
            </div>
          )}
          {f.start === 'every' && (
            <div className="space-y-1.5">
              <Label htmlFor="new-fn-interval">{t('ide.newFn.interval')}</Label>
              <Input id="new-fn-interval" className="font-mono" value={f.interval} onChange={(e) => set({ interval: e.target.value.trim() })} />
            </div>
          )}

          {(problem || error) && <p className="text-sm text-destructive">{error ?? t(problem!)}</p>}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" disabled={!f.path || !!problem}>
              {t('ide.newFn.create')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
