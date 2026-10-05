import { thumbnail } from '@dcms/gjs-blocks';
import { CODE_PREFIX, type ComponentDefinition } from '@dcms/site-runtime';
import {
  AppWindow,
  Columns2,
  FileCode,
  FileText,
  Frame,
  Heading,
  Image,
  Images,
  LayoutGrid,
  LayoutList,
  ListChecks,
  MapPin,
  Menu,
  Minus,
  PanelsTopLeft,
  Quote,
  Shapes,
  Tag,
  MousePointerClick,
  MoveVertical,
  Pilcrow,
  Puzzle,
  Rows3,
  Square,
  SquarePlay,
  SquareStack,
  TextCursorInput,
  ClipboardList,
  type LucideIcon,
} from 'lucide-react';

/**
 * How each component looks in the builder's own UI — an icon for lists and the inspector
 * header, a wireframe thumbnail for the palette. Editor-only, so it lives here and never in the
 * runtime a site ships.
 *
 * The wireframes are Mode A's hand-drawn archetypes (`@dcms/gjs-blocks` thumbnails): the same
 * visual language in both builders, and one place to draw a new shape.
 */
export interface ComponentLook {
  icon: LucideIcon;
  /** A thumbnail archetype from @dcms/gjs-blocks. */
  archetype: string;
}

export const COMPONENT_LOOKS: Readonly<Record<string, ComponentLook>> = {
  'dcms.nav': { icon: Menu, archetype: 'navbar' },
  'dcms.section': { icon: Rows3, archetype: 'section' },
  'dcms.container': { icon: Square, archetype: 'article' },
  'dcms.stack': { icon: SquareStack, archetype: 'stack' },
  'dcms.grid': { icon: LayoutGrid, archetype: 'gridBoxes' },
  'dcms.split': { icon: Columns2, archetype: 'heroSplit' },
  'dcms.spacer': { icon: MoveVertical, archetype: 'spacer' },
  'dcms.collection': { icon: LayoutList, archetype: 'cardGrid' },
  'dcms.richtext': { icon: FileText, archetype: 'article' },
  'dcms.form': { icon: ClipboardList, archetype: 'form' },
  'dcms.field': { icon: TextCursorInput, archetype: 'form' },
  'dcms.modal': { icon: AppWindow, archetype: 'cta' },
  'dcms.heading': { icon: Heading, archetype: 'heading' },
  'dcms.text': { icon: Pilcrow, archetype: 'text' },
  'dcms.image': { icon: Image, archetype: 'image' },
  'dcms.button': { icon: MousePointerClick, archetype: 'button' },
  'dcms.card': { icon: PanelsTopLeft, archetype: 'card' },
  'dcms.icon': { icon: Shapes, archetype: 'icon' },
  'dcms.badge': { icon: Tag, archetype: 'badge' },
  'dcms.list': { icon: ListChecks, archetype: 'list' },
  'dcms.divider': { icon: Minus, archetype: 'divider' },
  'dcms.quote': { icon: Quote, archetype: 'quote' },
  'dcms.video': { icon: SquarePlay, archetype: 'media' },
  'dcms.gallery': { icon: Images, archetype: 'gallery' },
  'dcms.map': { icon: MapPin, archetype: 'map' },
  'dcms.embed': { icon: Frame, archetype: 'embed' },
};

const TENANT: ComponentLook = { icon: Puzzle, archetype: 'card' };
const DEVELOPER: ComponentLook = { icon: FileCode, archetype: 'embed' };
const UNKNOWN: ComponentLook = { icon: Square, archetype: 'section' };

/** A component's look: its own, or its kind's — a site's own component, a developer's, unknown. */
export function lookOf(definition: Pick<ComponentDefinition, 'type' | 'template'>): ComponentLook {
  return (
    COMPONENT_LOOKS[definition.type] ??
    (definition.template ? TENANT : definition.type.startsWith(CODE_PREFIX) ? DEVELOPER : UNKNOWN)
  );
}

/** The palette's wireframe for a component, as SVG markup owned by the block library. */
export function thumbnailOf(definition: Pick<ComponentDefinition, 'type' | 'template'>): string {
  return thumbnail(lookOf(definition).archetype);
}
