import { slotClassName, type SlotLayout } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { SLOT } from './tree';

/** A slot view's own state: the layout its parent component asked for, re-applied after GrapesJS resets the element. */
export interface SlotViewState {
  el: HTMLElement;
  /** What the parent component last asked this slot to look like. */
  layout?: SlotLayout;
  /** The classes `layout` added, so the next layout can take exactly those away again. */
  applied?: string[];
}

export function applyLayout(view: SlotViewState): void {
  const { el } = view;
  for (const cls of view.applied ?? []) el.classList.remove(cls);
  const classes = slotClassName(view.layout).split(' ');
  el.classList.add(...classes);
  view.applied = classes;
  if (view.layout?.style) Object.assign(el.style, view.layout.style);
}

export function slotOf(model: Component, name: string): Component | undefined {
  return model.components().models.find((c) => c.get(SLOT) === name);
}

