import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { QuestionCard } from './QuestionCard';

const questions = [
  { question: 'Which layout?', header: 'Layout', options: [{ label: 'Cards' }, { label: 'List' }] },
  { question: 'Which sections?', multiSelect: true, options: [{ label: 'News' }, { label: 'Events' }] },
];

describe('QuestionCard', () => {
  it('sends the chosen options and the person\'s own answer, once every question has one', () => {
    const onAnswer = vi.fn();
    render(<QuestionCard questions={questions} onAnswer={onAnswer} />);
    const send = screen.getByRole('button', { name: 'assistant.ask.send' });

    fireEvent.click(screen.getByLabelText('Cards'));
    expect(send).toBeDisabled();

    fireEvent.click(screen.getByLabelText('News'));
    // Typing your own answer chooses it, beside the ticked option.
    fireEvent.change(screen.getByLabelText(/Which sections\? — assistant.ask.other/), { target: { value: 'Job openings' } });
    fireEvent.click(send);

    expect(onAnswer).toHaveBeenCalledWith([{ selected: ['Cards'] }, { selected: ['News'], other: 'Job openings' }]);
  });

  it('a single choice is replaced by writing your own', () => {
    const onAnswer = vi.fn();
    render(<QuestionCard questions={[questions[0]]} onAnswer={onAnswer} />);
    fireEvent.click(screen.getByLabelText('Cards'));
    fireEvent.change(screen.getByLabelText(/Which layout\? — assistant.ask.other/), { target: { value: 'Masonry' } });
    fireEvent.click(screen.getByRole('button', { name: 'assistant.ask.send' }));
    expect(onAnswer).toHaveBeenCalledWith([{ selected: [], other: 'Masonry' }]);
  });

  it('can be skipped', () => {
    const onAnswer = vi.fn();
    render(<QuestionCard questions={questions} onAnswer={onAnswer} />);
    fireEvent.click(screen.getByRole('button', { name: 'assistant.ask.skip' }));
    expect(onAnswer).toHaveBeenCalledWith(null);
  });
});
