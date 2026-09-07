import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TooltipProvider } from '../ui/tooltip';
import { user } from '../test/user';
import { TourButton } from './TourButton';
import { TourOverlay } from './TourOverlay';
import { TourProvider, useTour } from './TourProvider';
import { TourTarget } from './TourTarget';
import { usePageTour } from './usePageTour';
import type { TourStep } from './types';

const STEPS: TourStep[] = [
  { target: 'one', title: 'The first thing', body: 'What it does.' },
  { target: 'two', title: 'The second thing', body: 'What that does.' },
];

function Page({ steps = STEPS, targets = ['one', 'two'] }: { steps?: TourStep[]; targets?: string[] }) {
  usePageTour(steps);
  return (
    <div>
      {targets.map((id) => (
        <TourTarget key={id} id={id}>
          <button>{id}</button>
        </TourTarget>
      ))}
    </div>
  );
}

function App(props: React.ComponentProps<typeof Page>) {
  return (
    <TooltipProvider>
      <TourProvider>
        <TourButton />
        <Page {...props} />
        <TourOverlay />
      </TourProvider>
    </TooltipProvider>
  );
}

const launcher = () => screen.queryByRole('button', { name: 'Show me around this page' });

describe('the page tour', () => {
  it('does not start by itself', async () => {
    // A tour that opens over the page somebody navigated to is an obstacle between them and
    // their work, and the second time it happens they learn to dismiss the product.
    render(<App />);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('offers a launcher once the page declares steps', () => {
    render(<App />);
    expect(launcher()).toBeInTheDocument();
  });

  it('shows no launcher at all on a page with no tour', () => {
    // Rather than a disabled one: a permanently dead control in the top bar teaches people to
    // stop looking at that corner.
    render(<App steps={[]} targets={[]} />);
    expect(launcher()).not.toBeInTheDocument();
  });

  it('walks forward and back through the steps', async () => {
    render(<App />);
    await user.click(launcher()!);
    expect(screen.getByText('The first thing')).toBeInTheDocument();
    expect(screen.getByText('1 of 2')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('The second thing')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText('The first thing')).toBeInTheDocument();
  });

  it('offers no Back on the first step', async () => {
    render(<App />);
    await user.click(launcher()!);
    expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
  });

  it('ends with Done rather than Next', async () => {
    render(<App />);
    await user.click(launcher()!);
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByRole('button', { name: 'Done' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Done' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('closes on Escape', async () => {
    render(<App />);
    await user.click(launcher()!);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('walks with the arrow keys', async () => {
    render(<App />);
    await user.click(launcher()!);
    await user.keyboard('{ArrowRight}');
    expect(screen.getByText('The second thing')).toBeInTheDocument();
    await user.keyboard('{ArrowLeft}');
    expect(screen.getByText('The first thing')).toBeInTheDocument();
  });

  it('skips a step whose target is not on this page', () => {
    // Pages differ by permission and by what a workspace has enabled, so "this step's control
    // is not here" is ordinary rather than exceptional — and a step pointing at nothing would
    // put the cut-out in the corner of the screen.
    render(<App targets={['one']} />);
    expect(launcher()).toBeInTheDocument();
  });

  it('offers no tour when none of the targets are present', () => {
    render(<App targets={[]} />);
    expect(launcher()).not.toBeInTheDocument();
  });
});

function Probe() {
  const { steps } = useTour();
  return <span data-testid="count">{steps.length}</span>;
}

describe('TourProvider', () => {
  it('replaces the steps when a page changes them', () => {
    const { rerender } = render(
      <TourProvider>
        <Page steps={STEPS} />
        <Probe />
      </TourProvider>,
    );
    expect(screen.getByTestId('count')).toHaveTextContent('2');

    rerender(
      <TourProvider>
        <Page steps={[STEPS[0]]} targets={['one']} />
        <Probe />
      </TourProvider>,
    );
    expect(screen.getByTestId('count')).toHaveTextContent('1');
  });

  it('refuses to be used outside a provider rather than silently doing nothing', () => {
    expect(() => render(<TourButton />)).toThrow(/TourProvider/);
  });
});
