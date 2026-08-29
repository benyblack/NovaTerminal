#!/usr/bin/env node
/**
 * Sync generated marketing assets into the Astro public directory so they
 * ship with the static build. Two sources, both kept in git under docs/:
 *
 *   docs/assets/screenshots/  -> site/public/screenshots/  (legacy product shots)
 *   docs/assets/shots/        -> site/public/shots/        (curated subset of the
 *                                marketing screenshot harness's output — only the
 *                                site-width variants + OG card the Astro pages
 *                                actually reference, not the full generated set)
 *
 * This script runs automatically before `astro dev` and `astro build` (see
 * the `predev`/`prebuild` hooks in site/package.json). Run it manually with
 * `npm run sync:assets` if you add new images.
 */

import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.resolve(here, '..');
const repoRoot = path.resolve(siteRoot, '..');

const ALLOWED_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg']);

async function exists(p) {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}

/** Copy every allowed-extension file in sourceDir into destDir. */
async function syncAll(label, sourceDir, destDir) {
  if (!(await exists(sourceDir))) {
    console.warn(
      `[${label}] source directory not found: ${path.relative(repoRoot, sourceDir)}\n` +
        '  Add the assets there or skip this step.',
    );
    return;
  }

  await fs.mkdir(destDir, { recursive: true });

  const entries = await fs.readdir(sourceDir, { withFileTypes: true });
  let copied = 0;
  for (const entry of entries) {
    if (!entry.isFile()) continue;
    const ext = path.extname(entry.name).toLowerCase();
    if (!ALLOWED_EXT.has(ext)) continue;
    await fs.copyFile(path.join(sourceDir, entry.name), path.join(destDir, entry.name));
    copied += 1;
  }

  console.log(
    `[${label}] copied ${copied} file(s) from ` +
      `${path.relative(repoRoot, sourceDir)}/ -> ` +
      `${path.relative(repoRoot, destDir)}/`,
  );
}

/** Copy only the named files from sourceDir into destDir, skipping ones that don't exist. */
async function syncSelected(label, sourceDir, destDir, fileNames) {
  if (!(await exists(sourceDir))) {
    console.warn(
      `[${label}] source directory not found: ${path.relative(repoRoot, sourceDir)}\n` +
        '  Run `scripts/shots.ps1 all --scale 2 --publish` to generate it.',
    );
    return;
  }

  await fs.mkdir(destDir, { recursive: true });

  let copied = 0;
  for (const name of fileNames) {
    const src = path.join(sourceDir, name);
    if (!(await exists(src))) {
      console.warn(`[${label}] expected file missing, skipping: ${path.relative(repoRoot, src)}`);
      continue;
    }
    await fs.copyFile(src, path.join(destDir, name));
    copied += 1;
  }

  console.log(
    `[${label}] copied ${copied}/${fileNames.length} file(s) from ` +
      `${path.relative(repoRoot, sourceDir)}/ -> ` +
      `${path.relative(repoRoot, destDir)}/`,
  );
}

// The site-width variants and OG card actually referenced by the Astro
// pages (see Screenshots.astro and Base.astro's default ogImage). Keep this
// list in sync with those references — it deliberately does not mirror the
// whole docs/assets/shots/ directory (masters, README variants, clips, and
// unreferenced WebP siblings would otherwise ship to the static site unused).
//
// Only 3 of the 5 -site.png variants below have a WebP sibling in
// docs/assets/shots/ (command-palette-site and themes-grid-site do not —
// see that directory's own conditional-emission policy), and og-card has
// none either, so those four are deliberately PNG-only here too.
const SHOTS_FILES = [
  'hero-split-site.png',
  'hero-split-site.webp',
  'command-palette-site.png',
  'tui-monitor-site.png',
  'tui-monitor-site.webp',
  'search-overlay-site.png',
  'search-overlay-site.webp',
  'themes-grid-site.png',
  'og-card.png',
];

try {
  await syncAll(
    'sync-screenshots',
    path.join(repoRoot, 'docs', 'assets', 'screenshots'),
    path.join(siteRoot, 'public', 'screenshots'),
  );
  await syncSelected(
    'sync-shots',
    path.join(repoRoot, 'docs', 'assets', 'shots'),
    path.join(siteRoot, 'public', 'shots'),
    SHOTS_FILES,
  );
} catch (err) {
  console.error('[sync-screenshots] failed:', err);
  process.exit(1);
}
