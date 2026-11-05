export type Ecosystem = 'npm' | 'nuget';

export interface DeclaredPackage {
  ecosystem: Ecosystem;
  name: string;
  version: string;
}

/** What package.json's dependencies and Functions.csproj's PackageReferences declare (the SDK's own excepted). */
export function declaredPackages(files: Record<string, string>): DeclaredPackage[] {
  const found: DeclaredPackage[] = [];
  try {
    const deps = (JSON.parse(files['package.json'] ?? '{}') as { dependencies?: Record<string, string> }).dependencies ?? {};
    for (const [name, version] of Object.entries(deps)) found.push({ ecosystem: 'npm', name, version: String(version) });
  } catch {
    // Not JSON: the build says so.
  }
  const project = files['Functions.csproj'] ?? '';
  for (const m of project.matchAll(/<PackageReference\s+Include="([^"]+)"(?:\s+Version="([^"]*)")?/g)) {
    if (m[1].toLowerCase() !== 'dcms.functions.sdk') found.push({ ecosystem: 'nuget', name: m[1], version: m[2] ?? '' });
  }
  return found;
}
