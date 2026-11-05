import { describe, expect, it } from 'vitest';
import { declaredPackages } from './packages';

describe('declared packages', () => {
  it('reads package.json and Functions.csproj, without the SDK', () => {
    expect(
      declaredPackages({
        'package.json': '{ "dependencies": { "zod": "^4.0.0" } }',
        'Functions.csproj':
          '<Project><ItemGroup><PackageReference Include="Dcms.Functions.Sdk" Version="$(V)" /><PackageReference Include="NodaTime" Version="3.2.2" /></ItemGroup></Project>',
      }),
    ).toEqual([
      { ecosystem: 'npm', name: 'zod', version: '^4.0.0' },
      { ecosystem: 'nuget', name: 'NodaTime', version: '3.2.2' },
    ]);
    expect(declaredPackages({ 'package.json': 'not json' })).toEqual([]);
  });
});
