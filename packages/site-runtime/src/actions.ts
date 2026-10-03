import { z } from 'zod';
import { NAME, NODE_ID, isInternalPath, isSafeExternalHref } from './ids';
import { stateValueSchema } from './state';

/**
 * What a component may *do*, as data.
 *
 * Basic Mode D has no event handlers: a button does not carry a function, it carries one of
 * these, and the runtime carries it out. That is what lets drag and drop, the inspector and the
 * AI all offer behaviour from the same short menu, and what makes every action reviewable in a
 * diff. In the editor the runtime does not run them at all — a click there selects.
 *
 * `set-state` and `toggle-state` write the page's declared state (state.ts).
 */

const internalPath = z.string().refine(isInternalPath, 'must be a path on this site, starting with /');
const externalHref = z.string().refine(isSafeExternalHref, 'must be an http(s), mailto or tel link');
const nodeRef = z.string().regex(NODE_ID, 'must be a component id');

export const actionSchema = z.discriminatedUnion('type', [
  z.strictObject({ type: z.literal('navigate'), to: internalPath }),
  z.strictObject({ type: z.literal('open-external'), href: externalHref, newTab: z.boolean().optional() }),
  z.strictObject({ type: z.literal('scroll-to'), target: nodeRef }),
  z.strictObject({ type: z.literal('submit-form'), form: z.string().min(1).max(128) }),
  z.strictObject({ type: z.literal('open-modal'), modal: nodeRef }),
  z.strictObject({
    type: z.literal('show-toast'),
    message: z.string().min(1).max(200),
    tone: z.enum(['info', 'success', 'error']).optional(),
  }),
  z.strictObject({ type: z.literal('set-state'), key: z.string().regex(NAME), value: stateValueSchema }),
  z.strictObject({ type: z.literal('toggle-state'), key: z.string().regex(NAME) }),
]);

export type Action = z.infer<typeof actionSchema>;
export type ActionType = Action['type'];
