/** @type {import('tailwindcss').Config} */
export default {
  content: ['./src/**/*.{astro,html,js,jsx,md,mdx,svelte,ts,tsx,vue}'],
  theme: {
    extend: {
      colors: {
        // Subtle dark palette inspired by terminals — but warmer than pure
        // black so screenshots and product copy don't feel like a void.
        ink: {
          50: '#f5f7fa',
          100: '#e4e7eb',
          200: '#cbd2d9',
          300: '#9aa5b1',
          400: '#7b8794',
          500: '#616e7c',
          600: '#52606d',
          700: '#3e4c59',
          800: '#323f4b',
          900: '#1f2933',
          950: '#11161d',
        },
        nova: {
          // Used for accents, links, and the brand mark.
          50: '#eef9ff',
          100: '#daf0ff',
          200: '#bee5ff',
          300: '#91d5ff',
          400: '#5dbcff',
          500: '#3a9eff',
          600: '#1f7ff5',
          700: '#1a68e1',
          800: '#1a54b6',
          900: '#1a498f',
        },
      },
      fontFamily: {
        // The terminal screenshots show monospace UI chrome, so we lean into
        // a system mono stack for code and a clean sans for body text.
        sans: [
          'Inter',
          'ui-sans-serif',
          'system-ui',
          '-apple-system',
          'Segoe UI',
          'Roboto',
          'sans-serif',
        ],
        mono: [
          'JetBrains Mono',
          'Fira Code',
          'ui-monospace',
          'SFMono-Regular',
          'Menlo',
          'Consolas',
          'monospace',
        ],
      },
      maxWidth: {
        '8xl': '88rem',
      },
      backgroundImage: {
        'hero-grid':
          'radial-gradient(circle at top, rgba(58,158,255,0.18), transparent 55%), radial-gradient(circle at bottom right, rgba(31,127,245,0.12), transparent 60%)',
      },
      typography: () => ({}),
    },
  },
  plugins: [],
};
