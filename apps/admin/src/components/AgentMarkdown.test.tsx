import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AgentMarkdown } from './AgentMarkdown';

describe('AgentMarkdown', () => {
  it('renders the Markdown an agent writes', () => {
    const { container } = render(
      <AgentMarkdown text={'## Done\n\n- **one**\n- `two`\n\n```ts\nconst x = 1;\n```\n\n[docs](https://example.com)'} />,
    );
    expect(container.querySelector('h2')?.textContent).toBe('Done');
    expect(container.querySelectorAll('li')).toHaveLength(2);
    expect(container.querySelector('strong')?.textContent).toBe('one');
    expect(container.querySelector('pre code')?.textContent).toContain('const x = 1;');
    expect(container.querySelector('a')?.getAttribute('target')).toBe('_blank');
  });

  it('never turns raw HTML or script links into elements', () => {
    const { container } = render(
      <AgentMarkdown text={'<img src=x onerror="alert(1)"> <script>alert(2)</script> [x](javascript:alert(3))'} />,
    );
    expect(container.querySelector('img, script')).toBeNull();
    expect(container.querySelector('a')?.getAttribute('href') ?? '').not.toMatch(/^javascript:/i);
  });
});
