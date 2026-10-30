/**
 * An IDE workspace: a site by its id, or a plugin's git repository (ADR 0029) as
 * `repo:{bindingId}`. Both answer the same `ide/…` and `git/…` routes, under
 * `/admin/sites/{id}` and `/admin/repos/{id}`, so only the prefix differs.
 */
export function workspacePath(id: string): string {
  return isRepoWorkspace(id) ? `/admin/repos/${id.slice(5)}` : `/admin/sites/${id}`;
}

export function isRepoWorkspace(id: string): boolean {
  return id.startsWith('repo:');
}
