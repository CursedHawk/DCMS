import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createRef } from 'react';
import { Button } from './button';

describe('Button', () => {
  it('renders a real <button>, so it is keyboard and form reachable for free', () => {
    render(<Button>Save changes</Button>);
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInstanceOf(HTMLButtonElement);
  });

  it('calls onClick', async () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>Publish</Button>);
    await userEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('does not fire when disabled', async () => {
    const onClick = vi.fn();
    render(<Button disabled onClick={onClick}>Publish</Button>);
    await userEvent.click(screen.getByRole('button'));
    expect(onClick).not.toHaveBeenCalled();
  });

  it('lets a caller override a variant class rather than stacking with it', () => {
    // The whole point of cn(): a page passing bg-* must win over the variant's bg-*.
    const { container } = render(<Button variant="destructive" className="bg-primary" />);
    const classes = container.querySelector('button')!.className.split(/\s+/);
    expect(classes).toContain('bg-primary');
    expect(classes).not.toContain('bg-destructive');
  });

  it('does NOT override the variant’s hover colour, which is a sharp edge worth knowing', () => {
    // `bg-primary` and `hover:bg-destructive/90` are not conflicting utilities, so
    // tailwind-merge keeps both: the button rests primary and hovers destructive.
    // Callers restyling a variant have to pass the hover class too.
    const { container } = render(<Button variant="destructive" className="bg-primary" />);
    expect(container.querySelector('button')!.className.split(/\s+/))
      .toContain('hover:bg-destructive/90');
  });

  it('forwards a ref, which Radix asChild triggers rely on', () => {
    const ref = createRef<HTMLButtonElement>();
    render(<Button ref={ref}>x</Button>);
    expect(ref.current).toBeInstanceOf(HTMLButtonElement);
  });

  it('passes through arbitrary button attributes', () => {
    render(<Button type="submit" aria-label="Send" form="f1" />);
    const btn = screen.getByRole('button', { name: 'Send' });
    expect(btn).toHaveAttribute('type', 'submit');
    expect(btn).toHaveAttribute('form', 'f1');
  });

  it('keeps a visible focus ring — the console is used from the keyboard', () => {
    const { container } = render(<Button />);
    expect(container.querySelector('button')!.className).toContain('focus-visible:ring-2');
  });
});
