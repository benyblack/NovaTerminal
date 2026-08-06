import { defineConfig } from 'astro/config';
import tailwind from '@astrojs/tailwind';
import sitemap from '@astrojs/sitemap';

// `site` is the canonical origin (no trailing path) used for the sitemap
// and OpenGraph URLs. `base` is the path prefix Astro adds to every page
// and asset. For a project Pages site, set ASTRO_BASE to the repo name
// (e.g. `/NovaTerminal`). For a custom domain, leave ASTRO_BASE empty.
const site = process.env.ASTRO_SITE ?? 'https://benyblack.github.io';
const base = process.env.ASTRO_BASE ?? '/NovaTerminal';

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
