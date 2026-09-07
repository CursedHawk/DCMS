import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('react-i18next', () => ({
  // Mirrors the real thing closely enough for these components: every t() here passes a
  // defaultValue precisely so a host app missing the key still renders English.
  useTranslation: () => ({
    t: (key: string, o?: Record<string, unknown>) =>
      ((o?.defaultValue as string | undefined) ?? key).replace('{{value}}', String(o?.value ?? '')),
  }),
}));

const { ConfirmDeleteDialog } = await import('./ConfirmDeleteDialog');

const props = {
  open: true,
  onOpenChange: () => {},
  title: 'Delete site',
  description: 'This cannot be undone.',
  consequences: ['Every build is discarded', 'The git repository is removed'],
  confirmationValue: 'marketing-site',
  confirmLabel: 'Delete site',
  onConfirm: () => {},
};

const confirmButton = () => screen.getByRole('button', { name: 'Delete site' });

describe('ConfirmDeleteDialog', () => {
  it('spells out every consequence before asking', () => {
    render(<ConfirmDeleteDialog {...props} />);
    expect(screen.getByText('Every build is discarded')).toBeInTheDocument();
    expect(screen.getByText('The git repository is removed')).toBeInTheDocument();
  });

  it('starts disarmed', () => {
    render(<ConfirmDeleteDialog {...props} />);
    expect(confirmButton()).toBeDisabled();
  });

  it('stays disarmed for a near miss', async () => {
    render(<ConfirmDeleteDialog {...props} />);
    await userEvent.type(screen.getByRole('textbox'), 'marketing-sit');
    expect(confirmButton()).toBeDisabled();
  });

  it('arms on an exact match', async () => {
    render(<ConfirmDeleteDialog {...props} />);
    await userEvent.type(screen.getByRole('textbox'), 'marketing-site');
    expect(confirmButton()).toBeEnabled();
  });

  it('tolerates surrounding whitespace, which a paste brings along', async () => {
    render(<ConfirmDeleteDialog {...props} />);
    await userEvent.type(screen.getByRole('textbox'), '  marketing-site  ');
    expect(confirmButton()).toBeEnabled();
  });

  it('is case sensitive — the name is the name', async () => {
    render(<ConfirmDeleteDialog {...props} />);
    await userEvent.type(screen.getByRole('textbox'), 'Marketing-Site');
    expect(confirmButton()).toBeDisabled();
  });

  it('calls onConfirm once armed', async () => {
    const onConfirm = vi.fn();
    render(<ConfirmDeleteDialog {...props} onConfirm={onConfirm} />);
    await userEvent.type(screen.getByRole('textbox'), 'marketing-site');
    await userEvent.click(confirmButton());
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('stays disarmed while a delete is already in flight', async () => {
    render(<ConfirmDeleteDialog {...props} pending />);
    await userEvent.type(screen.getByRole('textbox'), 'marketing-site');
    expect(confirmButton()).toBeDisabled();
  });

  it('clears the typed name when it reopens for a DIFFERENT resource', async () => {
    // Otherwise the button is armed before the operator has read what they are about
    // to destroy — which is the entire point of the dialog.
    const { rerender } = render(<ConfirmDeleteDialog {...props} />);
    await userEvent.type(screen.getByRole('textbox'), 'marketing-site');
    expect(confirmButton()).toBeEnabled();

    rerender(<ConfirmDeleteDialog {...props} confirmationValue="marketing-site" open={false} />);
    rerender(<ConfirmDeleteDialog {...props} confirmationValue="blog-site" confirmLabel="Delete site" open />);
    expect(screen.getByRole('textbox')).toHaveValue('');
    expect(confirmButton()).toBeDisabled();
  });

  it('cancels without confirming', async () => {
    const onOpenChange = vi.fn();
    const onConfirm = vi.fn();
    render(<ConfirmDeleteDialog {...props} onOpenChange={onOpenChange} onConfirm={onConfirm} />);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
