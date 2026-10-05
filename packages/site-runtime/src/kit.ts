import type { Responsive } from './document';
import type { PropDefinition, PropGroup } from './props';

/**
 * The building blocks every built-in component module shares: reading props safely, turning
 * responsive props into classes, and declaring select props with human labels and help.
 */

export type Props = Record<string, unknown>;

export function choice<T extends string>(value: unknown, allowed: readonly T[], fallback: T): T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : fallback;
}

/**
 * A responsive prop's classes: the desktop value's class, plus `t-`/`m-` prefixed ones for any
 * tablet or mobile override. Each value goes through `choice` like any other.
 */
export function variants(props: Props, responsive: Responsive | undefined) {
  return <T extends string>(name: string, allowed: readonly T[], fallback: T, cls: (value: T) => string): string => {
    const parts = [cls(choice(props[name], allowed, fallback))];
    const tablet = responsive?.tablet?.[name];
    const mobile = responsive?.mobile?.[name];
    if (tablet !== undefined) parts.push(`t-${cls(choice(tablet, allowed, fallback))}`);
    if (mobile !== undefined) parts.push(`m-${cls(choice(mobile, allowed, fallback))}`);
    return parts.join(' ');
  };
}

export function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

/** Where a setting goes in the inspector, and the help text that explains it. */
export interface Meta {
  group: PropGroup;
  description: string;
  /** Shown only while another setting has one of these values (see PropDefinition.showIf). */
  showIf?: { prop: string; is: string[] };
}

/**
 * A choice. Every option needs a label a person would say — "Left", not "start"; "Heading 1 —
 * page title", not "1" — so the labels are required, one per value, and a test holds the line.
 */
export function select<T extends string>(name: string, label: string, values: readonly T[], fallback: T, labels: Record<T, string>, meta: Meta) {
  return {
    kind: 'select' as const,
    name,
    label,
    options: values.map((value) => ({ value, label: labels[value] })),
    default: fallback,
    ...meta,
  } satisfies PropDefinition;
}

/** A select whose value may differ on tablet and mobile (see `variants`). */
export function responsiveSelect<T extends string>(name: string, label: string, values: readonly T[], fallback: T, labels: Record<T, string>, meta: Meta) {
  return { ...select(name, label, values, fallback, labels, meta), responsive: true } satisfies PropDefinition;
}

