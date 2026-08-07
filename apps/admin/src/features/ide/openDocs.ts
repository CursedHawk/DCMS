// Remembers which documents were open per site + branch so switching branches (or
// re-entering the IDE) restores that branch's own set of tabs (and the focused one).
// Stored in localStorage — a per-browser, per-user convenience, not authoritative state.

export interface OpenDocsSnapshot {
  openTabs: string[];
  activePath: string | null;
}

const key = (siteId: string, branch: string) => `dcms.ide.openDocs.${siteId}.${branch}`;

export function loadOpenDocs(siteId: string, branch: string): OpenDocsSnapshot | null {
  try {
    const raw = localStorage.getItem(key(siteId, branch));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<OpenDocsSnapshot>;
    if (!parsed || !Array.isArray(parsed.openTabs)) return null;
    return {
      openTabs: parsed.openTabs.filter((p): p is string => typeof p === 'string'),
      activePath: typeof parsed.activePath === 'string' ? parsed.activePath : null,
    };
  } catch {
    return null;
  }
}

export function saveOpenDocs(siteId: string, branch: string, snap: OpenDocsSnapshot): void {
  try {
    localStorage.setItem(key(siteId, branch), JSON.stringify(snap));
  } catch {
    // ignore quota / disabled storage
  }
}
