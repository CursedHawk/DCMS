/**
 * Component registry metadata. Maps a ComponentNode.type to its editor metadata:
 * a prop JSON Schema (drives the inspector + AI output), default props, whether
 * it accepts children, the plugin it requires (palette is filtered to enabled
 * plugins), and — for data-bound components — the binding contract.
 */
export interface BindingContract {
  /** Prop that receives the bound data. */
  propPath: string;
  /** Content type the bound plugin instance must expose. */
  contentType: string;
}

export interface ComponentRegistration {
  type: string;
  displayName: string;
  category: 'layout' | 'content' | 'plugin';
  propSchema: Record<string, unknown>;
  defaultProps: Record<string, unknown>;
  acceptsChildren: boolean;
  /** Plugin id that must be enabled for this component to be offered. */
  requiredPluginId?: string;
  binding?: BindingContract;
}

const obj = (properties: Record<string, unknown>, required: string[] = []) => ({
  type: 'object',
  properties,
  required,
  additionalProperties: false,
});
const str = (title: string) => ({ type: 'string', title });
const enumStr = (title: string, values: string[]) => ({ type: 'string', title, enum: values });

export const registry: ComponentRegistration[] = [
  // ---- layout primitives ----
  { type: 'Section', displayName: 'Section', category: 'layout', propSchema: obj({}), defaultProps: {}, acceptsChildren: true },
  { type: 'Stack', displayName: 'Stack', category: 'layout', propSchema: obj({ gap: str('Gap') }), defaultProps: { gap: '1rem' }, acceptsChildren: true },
  { type: 'Grid', displayName: 'Grid', category: 'layout', propSchema: obj({ columns: { type: 'integer', title: 'Columns' } }), defaultProps: { columns: 3 }, acceptsChildren: true },
  {
    type: 'Hero',
    displayName: 'Hero',
    category: 'layout',
    propSchema: obj({ title: str('Title'), subtitle: str('Subtitle') }, ['title']),
    defaultProps: { title: 'Headline', subtitle: 'Supporting text' },
    acceptsChildren: true,
  },
  // ---- content primitives ----
  {
    type: 'Heading',
    displayName: 'Heading',
    category: 'content',
    propSchema: obj({ text: str('Text'), variant: enumStr('Level', ['h1', 'h2', 'h3', 'h4']) }, ['text']),
    defaultProps: { text: 'Heading', variant: 'h2' },
    acceptsChildren: false,
  },
  {
    type: 'Text',
    displayName: 'Text',
    category: 'content',
    propSchema: obj({ text: str('Text') }, ['text']),
    defaultProps: { text: 'Lorem ipsum' },
    acceptsChildren: false,
  },
  {
    type: 'Image',
    displayName: 'Image',
    category: 'content',
    propSchema: obj({ src: str('Image URL'), alt: str('Alt text') }, ['src']),
    defaultProps: { src: '', alt: '' },
    acceptsChildren: false,
  },
  {
    type: 'Button',
    displayName: 'Button',
    category: 'content',
    propSchema: obj({ label: str('Label'), href: str('Link') }, ['label']),
    defaultProps: { label: 'Click me', href: '#' },
    acceptsChildren: false,
  },
  // ---- plugin-backed components (data-bound) ----
  pluginComp('BlogList', 'Blog List', 'blog', 'items', 'post'),
  pluginComp('ArticleView', 'Article', 'articles', 'item', 'article'),
  pluginComp('GalleryGrid', 'Image Gallery', 'image-gallery', 'items', 'gallery'),
  pluginComp('CarouselView', 'Carousel', 'carousel', 'slides', 'slide'),
  pluginComp('VideoPlayer', 'Video', 'video-gallery', 'items', 'video'),
  pluginComp('AudioPlayer', 'Audio', 'audio-library', 'tracks', 'track'),
  pluginComp('DownloadList', 'Downloads', 'file-downloads', 'files', 'file'),
  pluginComp('SearchBox', 'Search', 'search', 'results', 'result'),
  // ChatWidget is plugin-backed but not data-bound — it boots a live connection
  // to the chat hub rather than rendering published content.
  {
    type: 'ChatWidget',
    displayName: 'Live Chat',
    category: 'plugin',
    propSchema: obj({ heading: str('Heading'), greeting: str('Greeting') }),
    defaultProps: { heading: 'Chat with us', greeting: 'Hi! How can we help?' },
    acceptsChildren: false,
    requiredPluginId: 'live-chat',
  },
];

function pluginComp(
  type: string,
  displayName: string,
  pluginId: string,
  propPath: string,
  contentType: string,
): ComponentRegistration {
  return {
    type,
    displayName,
    category: 'plugin',
    propSchema: obj({ heading: str('Heading') }),
    defaultProps: {},
    acceptsChildren: false,
    requiredPluginId: pluginId,
    binding: { propPath, contentType },
  };
}

export function findRegistration(type: string): ComponentRegistration | undefined {
  return registry.find((r) => r.type === type);
}

/** Registry entries available given the set of enabled plugin ids. */
export function availableComponents(enabledPluginIds: ReadonlySet<string>): ComponentRegistration[] {
  return registry.filter((r) => !r.requiredPluginId || enabledPluginIds.has(r.requiredPluginId));
}
