/** Runtime configuration, sourced from Vite env vars (see .env.example). */
export const config = {
  apiBaseUrl: import.meta.env.VITE_API_BASE_URL ?? '',
  demoSlug: import.meta.env.VITE_DEMO_SLUG ?? '',
  demoContentType: import.meta.env.VITE_DEMO_CONTENT_TYPE ?? '',
};
