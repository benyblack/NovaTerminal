#!/usr/bin/env node
/**
 * Sync the canonical product screenshots into the Astro public directory
 * so they ship with the static build.
 *
 * Source:  docs/assets/screenshots/  (kept in git)
 * Dest:    site/public/screenshots/ (served at /screenshots/*)
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

const sourceDir = path.join(repoRoot, 'docs', 'assets', 'screenshots');
const destDir = path.join(siteRoot, 'public', 'screenshots');

const ALLOWED_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg']);

async function exists(p) {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}

async function main() {
  if (!(await exists(sourceDir))) {
    console.warn(
      `[sync-screenshots] source directory not found: ${path.relative(
        repoRoot,
        sourceDir,
      )}\n` +
        '  Add the screenshots there or skip this step.',
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
    const src = path.join(sourceDir, entry.name);
    const dst = path.join(destDir, entry.name);
    await fs.copyFile(src, dst);
    copied += 1;
  }

  console.log(
    `[sync-screenshots] copied ${copied} file(s) from ` +
      `${path.relative(repoRoot, sourceDir)}/ -> ` +
      `${path.relative(repoRoot, destDir)}/`,
  );
}

try {
  await main();
} catch (err) {
  console.error('[sync-screenshots] failed:', err);
  process.exit(1);
}
