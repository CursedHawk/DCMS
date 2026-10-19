import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, o?: Record<string, unknown>) => (o?.defaultValue as string | undefined) ?? key,
  }),
}));

const { ConfirmProvider, useConfirm } = await import('./ConfirmDialog');

function Asker({ onAnswer }: { onAnswer: (answer: boolean) => void }) {
  const confirm = useConfirm();
  return (
    <button
      type="button"
      onClick={async () =>
        onAnswer(
          await confirm({ title: 'Remove member?', confirmLabel: 'Remove', destructive: true }),
        )
      }
    >
      ask
    </button>
  );
}

describe('useConfirm', () => {
  it('resolves true when confirmed and false when cancelled', async () => {
    const answers: boolean[] = [];
    render(
      <ConfirmProvider>
        <Asker onAnswer={(a) => answers.push(a)} />
      </ConfirmProvider>,
    );

    await userEvent.click(screen.getByText('ask'));
    expect(screen.getByRole('dialog', { name: 'Remove member?' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(answers).toEqual([true]));

    await userEvent.click(screen.getByText('ask'));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(answers).toEqual([true, false]));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('falls back to the browser box outside a provider', async () => {
    const native = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const answers: boolean[] = [];
    render(<Asker onAnswer={(a) => answers.push(a)} />);
    await userEvent.click(screen.getByText('ask'));
    await waitFor(() => expect(answers).toEqual([true]));
    expect(native).toHaveBeenCalledWith('Remove member?');
  });
});
