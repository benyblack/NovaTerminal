# NovaTerminal site

Marketing and documentation landing page for [NovaTerminal](../README.md),
hosted on GitHub Pages.

- **Source:** [`site/`](./) (Astro + Tailwind, single-page with anchor sections)
- **Output:** static HTML/CSS/JS, no server runtime
- **Deploy:** GitHub Actions → Pages (see [`.github/workflows/pages.yml`](../.github/workflows/pages.yml))
- **Canonical screenshots:** [`docs/assets/screenshots/`](../docs/assets/screenshots/)
  — kept in git, synced into the build by `scripts/sync-screenshots.mjs`

## Develop

```bash
cd site
npm install
npm run dev      # http://localhost:4321
```

The `predev` hook runs `scripts/sync-screenshots.mjs` automatically, so any
file you drop into `docs/assets/screenshots/` shows up at
`/screenshots/<name>` on the dev server.

## Build

```bash
cd site
npm run build    # -> site/dist/
npm run preview  # serve site/dist/ locally
```

The build is pure static output: `index.html`, `_astro/*` (hashed CSS/JS),
`favicon.svg`, `og.svg`, `screenshots/*`, and `sitemap-index.xml`.

## Deploy

The workflow in [`.github/workflows/pages.yml`](../.github/workflows/pages.yml)
runs on every push to `main`:

1. Installs Node 20 + `site/` dependencies (`npm ci`)
2. Runs `npm run build` (with `prebuild` syncing screenshots first)
3. Uploads `site/dist/` as a Pages artifact
4. Publishes via `actions/deploy-pages@v4`

### One-time repo setup

1. **Settings → Pages → Source:** *GitHub Actions* (not "Deploy from a branch").
2. (Optional) **Settings → Pages → Custom domain:** point at your domain, then
   add a `CNAME` file under `site/public/` containing only the domain.
3. (Optional) **Settings → Secrets and variables → Variables:**
   - `ASTRO_SITE` — canonical origin (e.g. `https://novaterminal.dev`).
     Default: `https://benyblack.github.io`.
   - `ASTRO_BASE` — path prefix Astro adds to every URL. Default:
     `/NovaTerminal`. Set to empty for a custom-domain deployment.
   The workflow forwards both to the Astro build.

## Stack

- [Astro 4](https://astro.build/) — static-site framework, no client JS by
  default, ships only the bytes the page needs.
- [Tailwind CSS 3](https://tailwindcss.com/) — design tokens live in
  [`tailwind.config.mjs`](./tailwind.config.mjs); base styles in
  [`src/styles/global.css`](./src/styles/global.css).
- [`@astrojs/sitemap`](https://docs.astro.build/en/guides/integrations-guide/sitemap/)
  — emits `/sitemap-index.xml` so search engines can find the page.

## Editing content

- Page sections live in [`src/components/`](./src/components/). Each section
  is its own file so it's easy to reorder or hide without touching layout.
- The base layout (head, nav, footer) is in
  [`src/layouts/Base.astro`](./src/layouts/Base.astro).
- Brand colors and font stacks are defined in
  [`tailwind.config.mjs`](./tailwind.config.mjs) under
  `theme.extend.colors.{ink,nova}` and `theme.extend.fontFamily`.

## File map

```
site/
├── astro.config.mjs           # Astro config (site URL, integrations)
├── tailwind.config.mjs        # Design tokens
├── package.json               # Dependencies + scripts
├── public/                    # Served verbatim at the site root
│   ├── favicon.svg
│   ├── og.svg
│   ├── robots.txt
│   └── screenshots/           # Synced from docs/assets/screenshots/
├── scripts/
│   └── sync-screenshots.mjs   # Copies docs/assets/screenshots -> public/
├── src/
│   ├── components/            # One Astro file per page section
│   ├── layouts/
│   │   └── Base.astro
│   ├── pages/
│   │   └── index.astro        # Single-page entrypoint
│   └── styles/
│       └── global.css         # Tailwind layers + design tokens
└── README.md
```

## License

Same as the project: [MIT](../LICENSE).
