import { describe, expect, it, vi } from 'vitest';
import type { TypeCheckRequest, TypeCheckResponse } from './typecheck.worker';

/**
 * The agent run's own type checker (ADR 0033), driven in-process.
 *
 * <p>The two codes are the ones the HrSite run left behind when it lost track of App.tsx: a
 * duplicated component (TS2393) and a reference to one it had deleted (TS2304). Monaco's checker
 * reported neither for a run whose editor was not on screen.</p>
 */
describe('typecheck worker', () => {
  it('reports what the HrSite run broke, and nothing for a clean file', async () => {
    const posted: TypeCheckResponse[] = [];
    vi.spyOn(self, 'postMessage').mockImplementation((message: unknown) => {
      posted.push(message as TypeCheckResponse);
    });
    await import('./typecheck.worker');

    const request: TypeCheckRequest = {
      id: 1,
      files: {
        'src/App.ts': 'function WorkHub() {}\nfunction WorkHub() {}\nexport const page = Records;\n',
        'src/ok.ts': 'export const answer: number = 42;\n',
      },
      paths: ['src/App.ts', 'src/ok.ts'],
      extraLibs: [],
    };
    (self.onmessage as (event: MessageEvent<TypeCheckRequest>) => void)({ data: request } as MessageEvent<TypeCheckRequest>);

    const response = posted.find((m) => m.id === 1);
    expect(response?.error).toBeUndefined();
    const codes = (response?.diagnostics?.['src/App.ts'] ?? []).map((d) => d.code);
    expect(codes).toEqual(expect.arrayContaining([2393, 2304]));
    expect(response?.diagnostics?.['src/ok.ts']).toEqual([]);
  }, 60_000);
});
