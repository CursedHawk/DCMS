import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { GitBranch, Palette, Pencil } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { RELEASE_BRANCH } from './constants';
import { EDITOR_THEMES, editorThemeChoice, setEditorThemeChoice } from './editorThemes';
import { gitApi } from './git';

// A slim VS Code-style status bar: current branch (click to open Source Control)
// and the pending-change count. The release branch is flagged so users know a
// commit there publishes. On the right, the editors' colour theme.
export function StatusBar({
  siteId,
  branch,
  dirty,
  onOpenScm,
}: {
  siteId: string;
  branch: string;
  dirty: boolean;
  onOpenScm: () => void;
}) {
  const { t } = useTranslation();
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });
  const count = changes.data?.length ?? 0;
  const [theme, setTheme] = useState(editorThemeChoice);

  return (
    <div className="flex h-6 shrink-0 items-center gap-3 border-t bg-card px-3 text-2xs text-muted-foreground">
      <button
        type="button"
        onClick={onOpenScm}
        className="flex items-center gap-1 hover:text-foreground"
        title={t('ide.git.branch')}
      >
        <GitBranch className="h-3 w-3" />
        {branch}
        {branch === RELEASE_BRANCH && <span className="text-warning-foreground">({t('ide.git.live')})</span>}
      </button>
      <button type="button" onClick={onOpenScm} className="flex items-center gap-1 hover:text-foreground">
        <Pencil className="h-3 w-3" />
        {t('ide.git.changesCount', { count })}
      </button>
      {dirty && <span className="text-muted-foreground">{t('common.saving')}</span>}
      <label className="ml-auto flex items-center gap-1 hover:text-foreground" title={t('ide.editorTheme.label')}>
        <Palette className="h-3 w-3" />
        <select
          className="cursor-pointer bg-transparent text-2xs outline-none"
          aria-label={t('ide.editorTheme.label')}
          value={theme}
          onChange={(e) => {
            setTheme(e.target.value);
            setEditorThemeChoice(e.target.value);
          }}
        >
          <option value="auto">{t('ide.editorTheme.auto')}</option>
          {EDITOR_THEMES.map((th) => (
            <option key={th.id} value={th.id}>
              {th.label}
            </option>
          ))}
        </select>
      </label>
    </div>
  );
}
