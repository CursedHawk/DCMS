import type { Node, Registry } from '@dcms/site-runtime';
import { nodeFromStarter } from '../canvas/tree';
import { SECTION_TEMPLATES } from './sections';

/**
 * Page templates (Mode D v2, U2.4): a new page that already has the usual bands for its kind,
 * picked from the section library. A suggested title and address come with it; both stay the
 * author's to change.
 */
export interface PageTemplate {
  id: string;
  label: string;
  description: string;
  title: string;
  path: string;
  sections: readonly string[];
}

export const PAGE_TEMPLATES: readonly PageTemplate[] = [
  { id: 'blank', label: 'Blank', description: 'An empty page to fill yourself.', title: '', path: '', sections: [] },
  { id: 'landing', label: 'Landing page', description: 'Hero, features, testimonials, pricing and a call to action.', title: 'Welcome', path: '/welcome', sections: ['hero-centred', 'features-grid', 'testimonials', 'pricing', 'cta-band'] },
  { id: 'about', label: 'About us', description: 'Your story, the team, numbers and a way to get in touch.', title: 'About us', path: '/about', sections: ['hero-split', 'text-image', 'stats', 'team', 'cta-band'] },
  { id: 'services', label: 'Services', description: 'What you offer, how working with you goes, and common questions.', title: 'Services', path: '/services', sections: ['hero-centred', 'services-cards', 'steps', 'faq', 'cta-band'] },
  { id: 'pricing', label: 'Pricing', description: 'Plans side by side, what is included, and questions about paying.', title: 'Pricing', path: '/pricing', sections: ['pricing', 'features-checklist', 'faq'] },
  { id: 'contact', label: 'Contact', description: 'A contact form beside your address and a map, plus questions.', title: 'Contact', path: '/contact', sections: ['contact', 'faq'] },
  { id: 'gallery', label: 'Gallery', description: 'A photo gallery and a video.', title: 'Gallery', path: '/gallery', sections: ['gallery-section', 'video-section'] },
];

/** The page's bands, as fresh nodes: every page made from a template gets its own ids. */
export function pageBody(template: PageTemplate, registry: Registry): Node[] {
  return template.sections.map((id) => nodeFromStarter(SECTION_TEMPLATES.find((s) => s.id === id)!.tree, registry));
}
