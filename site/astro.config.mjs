import { defineConfig } from 'astro/config';
import tailwind from '@astrojs/tailwind';
import sitemap from '@astrojs/sitemap';

// `site` is the canonical origin (no trailing path) used for the sitemap
// and OpenGraph URLs. `base` is the path prefix Astro adds to every page
// and asset. Defaults assume a project Pages site at `/NovaTerminal/`.
// The sentinel `__default__` (substituted by the workflow when the
// variable is unset) means "use the default". An empty value means
// "explicitly no prefix" — used for custom-domain deployments.
const DEFAULT_SITE = 'https://benyblack.github.io';
const DEFAULT_BASE = '/NovaTerminal';
const USE_DEFAULT = '__default__';
const resolveEnv = (name, fallback) => {
  const v = process.env[name];
  if (v === undefined || v === USE_DEFAULT) return fallback;
  return v;
};
const site = resolveEnv('ASTRO_SITE', DEFAULT_SITE);
const base = resolveEnv('ASTRO_BASE', DEFAULT_BASE);

// https://astro.build/config
export default defineConfig({
  site,
  base,
  trailingSlash: 'ignore',
  integrations: [
    tailwind({
      // We use a dedicated base CSS file (src/styles/global.css) so the
      // Tailwind directives are explicit; this keeps `apply` and component
      // classes in one place.
      applyBaseStyles: false,
    }),
    sitemap(),
  ],
  build: {
    // Inline tiny stylesheets so the page renders with a single round-trip
    // on the first paint; the rest of the build output is small.
    inlineStylesheets: 'auto',
  },
  compressHTML: true,
});
