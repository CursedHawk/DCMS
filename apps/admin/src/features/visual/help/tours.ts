import type { TourStep } from '@dcms/ui';
import type { Editor } from 'grapesjs';
import type { TFunction } from 'i18next';
import { useVisual } from '../store';

/**
 * The builder's tours (Mode D v2, U5): one around the whole builder, which starts by itself on
 * someone's first visit, and short ones for single tasks, started from the Help menu. The steps
 * point at `<TourTarget>`s in the builder; one whose target is not on screen is skipped.
 */

export const MINI_TOURS = ['firstPage', 'content', 'reusable', 'publish'] as const;
export type MiniTour = (typeof MINI_TOURS)[number];

type Until = TourStep['until'];

function step(t: TFunction, target: string, key: string, placement?: TourStep['placement'], until?: Until): TourStep {
  return { target, title: t(`visual.tour.${key}.title`), body: t(`visual.tour.${key}.body`), placement, until };
}

/** Moves on the first time the canvas fires `event` — inserting a section fires one per part. */
function onEditor(editor: Editor | null, event: string): Until {
  return (done) => {
    if (!editor) return () => {};
    let fired = false;
    const once = () => {
      if (fired) return;
      fired = true;
      done();
    };
    editor.on(event, once);
    return () => void editor.off(event, once);
  };
}

/** Moves on once the builder is in the state the step asks for. */
function onState(test: (s: ReturnType<typeof useVisual.getState>) => boolean): Until {
  return (done) => useVisual.subscribe((s) => test(s) && done());
}

export function builderTour(t: TFunction): TourStep[] {
  return [
    step(t, 'visual.palette', 'builder.palette', 'right'),
    step(t, 'visual.canvas', 'builder.canvas', 'left'),
    step(t, 'visual.inspector', 'builder.inspector', 'left'),
    step(t, 'visual.views', 'builder.views'),
    step(t, 'visual.devices', 'builder.devices'),
    step(t, 'visual.rail.pages', 'builder.pages', 'right'),
    step(t, 'visual.rail.theme', 'builder.theme', 'right'),
    step(t, 'visual.rail.mine', 'builder.mine', 'right'),
    step(t, 'visual.rail.agent', 'builder.agent', 'right'),
    step(t, 'visual.publish', 'builder.publish'),
    step(t, 'visual.help', 'builder.help'),
  ];
}

/**
 * A task's steps. Those that ask for something wait for it, and the "reusable" one, started
 * outside the studio, ends when the studio opens — the builder then continues it there.
 */
export function miniTour(t: TFunction, which: MiniTour, editor: Editor | null, inStudio: boolean, continueInStudio: () => void): TourStep[] {
  switch (which) {
    case 'firstPage':
      return [
        step(t, 'visual.palette', 'firstPage.section', 'right', onEditor(editor, 'component:add')),
        step(t, 'visual.canvas', 'firstPage.text', 'left'),
        step(t, 'visual.inspector', 'firstPage.picture', 'left'),
        step(t, 'visual.views', 'firstPage.preview', 'bottom', onState((s) => s.view === 'preview')),
      ];
    case 'content':
      return [
        step(t, 'visual.palette', 'content.collection', 'right', onEditor(editor, 'component:add')),
        step(t, 'visual.inspector', 'content.source', 'left'),
        step(t, 'visual.canvas', 'content.fields', 'left'),
        step(t, 'visual.rail.pages', 'content.detail', 'right'),
      ];
    case 'reusable':
      return inStudio
        ? [
            step(t, 'studio.tab.settings', 'reusable.settings', 'left'),
            step(t, 'studio.tab.slots', 'reusable.slots', 'left'),
            step(t, 'studio.tab.preview', 'reusable.preview', 'left'),
            step(t, 'studio.tab.versions', 'reusable.versions', 'left'),
            step(t, 'studio.done', 'reusable.done', 'left'),
          ]
        : [
            step(t, 'visual.rail.mine', 'reusable.mine', 'right'),
            step(t, 'visual.canvas', 'reusable.make', 'left', (done) =>
              useVisual.subscribe((s) => {
                if (s.target?.kind !== 'component') return;
                done();
                continueInStudio();
              }),
            ),
          ];
    case 'publish':
      return [
        step(t, 'visual.rail.problems', 'publish.problems', 'right'),
        step(t, 'visual.publish', 'publish.publish'),
        step(t, 'visual.rail.deploy', 'publish.deploy', 'right'),
      ];
  }
}
