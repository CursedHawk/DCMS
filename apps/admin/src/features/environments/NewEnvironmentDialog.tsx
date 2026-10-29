import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  Callout,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Switch,
  toastApiError,
} from '@dcms/ui';
import { ChoiceList } from './ChoiceList';
import { useAddEnvironment, useCopyScopes, type Environment } from './api';

export const ENVIRONMENT_NAME = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/;

type Purpose = 'stage' | 'feature' | 'service';
const EMPTY = 'empty';

/**
 * A new environment in three steps, each one decision: what it is for (which says where it sits),
 * its name and what it starts as, and what data comes with it. The configuration of the one it
 * copies always comes; data is ticked.
 */
export function NewEnvironmentDialog({
  all,
  onClose,
  onCreated,
}: {
  all: Environment[];
  onClose: () => void;
  onCreated: (env: Environment) => void;
}) {
  const { t } = useTranslation();
  const add = useAddEnvironment();
  const scopes = useCopyScopes();
  // Only what is ready can be copied: one still being set up has nothing complete to give.
  const usable = all.filter((e) => e.ready !== false);
  const production = usable.find((e) => e.isProduction);
  const firstStage = [...usable].filter((e) => e.purpose === 'stage').sort((a, b) => a.order - b.order)[0];
  const branchable = usable.filter((e) => e.purpose !== 'service');

  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [purpose, setPurpose] = useState<Purpose | null>(null);
  const [name, setName] = useState('');
  const [from, setFrom] = useState<string>(production?.id ?? '');
  const [deleteWhenMerged, setDeleteWhenMerged] = useState(true);
  const [copy, setCopy] = useState<string[]>([]);

  const empty = from === EMPTY;
  const source = all.find((e) => e.id === from);
  const nameTaken = all.some((e) => e.name === name.trim());
  const nameOk = ENVIRONMENT_NAME.test(name.trim()) && !nameTaken;
  const personal = copy.some((c) => scopes.data?.find((x) => x.scope === c)?.personalData);

  const create = () =>
    add.mutate(
      {
        name: name.trim(),
        ...(empty ? { empty: true } : { from, copy: copy.length > 0 ? copy : undefined }),
        ...(purpose === 'feature' ? { feature: true, deleteWhenMerged } : {}),
        ...(purpose === 'service' ? { service: true } : {}),
      },
      { onSuccess: (env) => onCreated(env as Environment), onError: (err) => toastApiError(err, t) },
    );

  const where =
    purpose === 'stage'
      ? firstStage
        ? t('environments.new.whereStage', { name: firstStage.name })
        : ''
      : purpose === 'feature'
        ? t('environments.new.whereFeature', { name: source?.name ?? '' })
        : t('environments.new.whereService');

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>{t('environments.new.title')}</DialogTitle>
          <DialogDescription>{t('environments.new.step', { step, count: purpose === 'feature' || !empty ? 3 : 2 })}</DialogDescription>
        </DialogHeader>
        <DialogBody className="space-y-4">
          {step === 1 ? (
            <ChoiceList<Purpose>
              label={t('environments.new.purpose')}
              value={purpose}
              onChange={(p) => {
                setPurpose(p);
                // A feature most often branches the first stage (development); a copy starts from production.
                setFrom(p === 'feature' ? (firstStage?.id ?? production?.id ?? '') : (production?.id ?? ''));
              }}
              choices={[
                { value: 'stage', title: t('environments.purposes.stage'), description: t('environments.new.stageHint') },
                { value: 'feature', title: t('environments.purposes.feature'), description: t('environments.new.featureHint') },
                { value: 'service', title: t('environments.purposes.service'), description: t('environments.new.serviceHint') },
              ]}
            />
          ) : null}

          {step === 2 ? (
            <>
              <div className="space-y-1.5">
                <Label htmlFor="new-env-name">{t('environments.name')}</Label>
                <Input
                  id="new-env-name"
                  value={name}
                  placeholder={purpose === 'feature' ? `${source?.name ?? 'development'}-feature1` : purpose === 'service' ? 'billing' : 'staging'}
                  onChange={(e) => setName(e.target.value.toLowerCase())}
                />
                <p className="text-xs text-muted-foreground">
                  {nameTaken ? t('environments.new.nameTaken') : t('environments.new.nameHint')}
                </p>
              </div>
              <div className="space-y-1.5">
                <Label>{purpose === 'feature' ? t('environments.new.branchOf') : t('environments.new.startFrom')}</Label>
                <ChoiceList<string>
                  label={purpose === 'feature' ? t('environments.new.branchOf') : t('environments.new.startFrom')}
                  value={from}
                  onChange={setFrom}
                  columns={2}
                  choices={[
                    ...(purpose === 'feature' ? branchable : usable).map((e) => ({
                      value: e.id,
                      title: purpose === 'feature' ? e.name : t('environments.new.copyOf', { name: e.name }),
                      description: t(`environments.purposes.${e.isProduction ? 'production' : e.purpose}`),
                    })),
                    ...(purpose === 'feature'
                      ? []
                      : [{ value: EMPTY, title: t('environments.new.empty'), description: t('environments.new.emptyHint') }]),
                  ]}
                />
              </div>
              {purpose === 'feature' ? (
                <label className="flex items-start gap-3 rounded-lg border p-3">
                  <Switch checked={deleteWhenMerged} onCheckedChange={setDeleteWhenMerged} className="mt-0.5" />
                  <span className="text-sm">
                    <span className="block font-medium">{t('environments.deleteWhenMerged')}</span>
                    <span className="block text-xs text-muted-foreground">{t('environments.new.deleteWhenMergedHint')}</span>
                  </span>
                </label>
              ) : null}
            </>
          ) : null}

          {step === 3 ? (
            <>
              <p className="text-sm">{t('environments.new.configurationComes', { name: source?.name ?? '' })}</p>
              {scopes.data && scopes.data.length > 0 ? (
                <fieldset className="space-y-2">
                  <legend className="mb-2 text-sm font-medium">{t('environments.copyData')}</legend>
                  {scopes.data.map(({ scope, personalData }) => (
                    <label key={scope} className="flex items-center gap-2 text-sm">
                      <Checkbox
                        checked={copy.includes(scope)}
                        onCheckedChange={(on) => setCopy((prev) => (on === true ? [...prev, scope] : prev.filter((x) => x !== scope)))}
                      />
                      <span className="min-w-0">{t(`environments.copyScopes.${scope}`, { defaultValue: scope })}</span>
                      {personalData ? <Badge tone="warning">{t('environments.personalData')}</Badge> : null}
                    </label>
                  ))}
                </fieldset>
              ) : null}
              {personal ? <Callout tone="warning">{t('environments.personalDataHint')}</Callout> : null}
            </>
          ) : null}

          {step > 1 && where ? <p className="text-xs text-muted-foreground">{where}</p> : null}
        </DialogBody>
        <DialogFooter>
          {step > 1 ? (
            <Button variant="outline" onClick={() => setStep((s) => (s === 3 ? 2 : 1))}>
              {t('actions.back')}
            </Button>
          ) : (
            <Button variant="outline" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
          )}
          {step === 1 ? (
            <Button disabled={!purpose} onClick={() => setStep(2)}>
              {t('actions.next')}
            </Button>
          ) : step === 2 && !empty ? (
            <Button disabled={!nameOk || !from} onClick={() => setStep(3)}>
              {t('actions.next')}
            </Button>
          ) : (
            <Button disabled={!nameOk || add.isPending} onClick={create}>
              {t('environments.new.create', { name: name.trim() || '…' })}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
