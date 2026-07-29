// Remembers which documents were open per site so re-entering the IDE for the
// same site restores the previous set of tabs (and the focused one). Stored in
// localStorage — a per-browser, per-user convenience, not authoritative state.

export interface OpenDocsSnapshot {
  openTabs: string[];
  activePath: string | null;
}

const key = (siteId: string) => `dcms.ide.openDocs.${siteId}`;

export function loadOpenDocs(siteId: string): OpenDocsSnapshot | null {
  try {
    const raw = localStorage.getItem(key(siteId));
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

export function saveOpenDocs(siteId: string, snap: OpenDocsSnapshot): void {
  try {
    localStorage.setItem(key(siteId), JSON.stringify(snap));
  } catch {
    // ignore quota / disabled storage
  }
}
