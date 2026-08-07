import type { APIRoute } from 'astro';

// Emit `robots.txt` with an absolute Sitemap URL so crawlers can find
// the sitemap regardless of the configured Pages base. `import.meta.env.BASE_URL`
// is the configured path prefix (empty string for custom-domain
// deployments with no prefix).
export const GET: APIRoute = ({ site }) => {
  const basePrefix = (import.meta.env.BASE_URL ?? '/').endsWith('/')
    ? import.meta.env.BASE_URL
    : `${import.meta.env.BASE_URL}/`;
  const sitemapUrl = new URL(`${basePrefix}sitemap-index.xml`, site).toString();
  const body = `User-agent: *\nAllow: /\n\nSitemap: ${sitemapUrl}\n`;
  return new Response(body, {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
