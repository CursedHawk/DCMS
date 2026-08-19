/**
 * The contract between the two bridges that touch shared regions.
 *
 * `RegionChrome` paints the regions as plain DOM around the page; `PreviewBridge`
 * fills any plugin placeholders inside them with live content. Because a repaint
 * throws the painted content away with the node that held it, the chrome bridge
 * announces every repaint rather than leaving the preview to notice — GrapesJS
 * emits no event for DOM we created ourselves.
 */

/** Attribute marking a painted region; its value is the region id. */
export const CHROME_ATTR = 'data-dcms-chrome';

/** Editor event fired after the chrome has been (re)painted. */
export const CHROME_EVENT = 'dcms:chrome';
