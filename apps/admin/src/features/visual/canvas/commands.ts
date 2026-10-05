import type { Registry } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { toast } from 'sonner';
import { copyStyle, duplicate, move, pasteStyle, remove, selectParent, type Outcome } from './operations';

/** Editor event: show the keyboard shortcut sheet. */
export const SHOW_SHORTCUTS = 'dcms:shortcuts';

/** GrapesJS's single-letter navigation keys fire while typing anywhere that is not an input. */
const SURPRISING = ['core:component-next', 'core:component-prev', 'core:component-enter', 'core:component-exit'];

/**
 * The canvas's keyboard (Mode D v2, U3.1). Every binding runs an operation the right-click menu
 * also offers; a refusal is said, never silent. GrapesJS skips them while text is being typed.
 * Commands are registered again with each registry, so they always place by the current one.
 */
export function registerCanvasCommands(editor: Editor, registry: Registry): void {
  const say = (out: Outcome) => {
    if (!out.ok) toast.error(out.reason);
  };
  const commands: Record<string, [keys: string, run: (ed: Editor) => void]> = {
    'dcms:select-parent': ['escape', (ed) => void selectParent(ed)],
    'dcms:duplicate': ['⌘+d, ctrl+d', (ed) => say(duplicate(ed, registry))],
    'dcms:move-up': ['alt+up', (ed) => say(move(ed, -1))],
    'dcms:move-down': ['alt+down', (ed) => say(move(ed, 1))],
    'dcms:copy-style': ['⌘+alt+c, ctrl+alt+c', (ed) => say(copyStyle(ed, registry))],
    'dcms:paste-style': ['⌘+alt+v, ctrl+alt+v', (ed) => say(pasteStyle(ed, registry))],
    [SHOW_SHORTCUTS]: ['shift+/', (ed) => ed.trigger(SHOW_SHORTCUTS)],
  };
  for (const [id, [keys, run]] of Object.entries(commands)) {
    editor.Commands.add(id, { run });
    // Bound once: keys name the command, which is looked up when pressed. Re-adding a keymap
    // leaves the old binding live in keymaster, and a key would then run twice.
    if (!editor.Keymaps.get(id)) editor.Keymaps.add(id, keys, id, { prevent: true });
  }
  // GrapesJS binds its own keys when the editor loads, after the plugins, so they are replaced
  // by command rather than unbound: Delete keeps a selection, the letter keys do nothing.
  editor.Commands.add('core:component-delete', {
    run: (ed: Editor) => {
      const gone = ed.getSelected();
      if (!gone) return [];
      const out = remove(ed);
      say(out);
      return out.ok ? [gone] : [];
    },
  });
  for (const id of SURPRISING) editor.Commands.add(id, { run: () => {} });
}
