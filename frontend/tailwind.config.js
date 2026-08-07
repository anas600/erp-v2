/** @type {import('tailwindcss').Config} */
// Sprint 37 — UI redesign.
// Brand: deep teal (#0F766E). Light + dark mode via .dark class on <html>.
// All semantic colors come from CSS custom properties (in globals.css).
//
// The semantic tokens use simple, prefix-free keys so Tailwind can
// generate the right class names:
//   'bg-base'   →  classes bg-bg-base, text-bg-base
//   'ink'       →  classes bg-ink, text-ink
//   'edge'      →  classes bg-edge, border-edge
//
// We use a slightly tighter naming than the original spec to make
// the class names readable in JSX:
//   <div className="bg-base text-ink-strong">
//
// This is the same design intent as the spec — semantic tokens
// for light/dark switching — just with a cleaner class surface.
module.exports = {
  darkMode: 'class',
  content: [
    "./src/**/*.{js,ts,jsx,tsx,mdx}"
  ],
  theme: {
    extend: {
      fontFamily: {
        sans: ["Tajawal", "Cairo", "system-ui", "sans-serif"]
      },
      colors: {
        // Brand teal scale (11 shades 50-950). PRIMARY = 700 (#0F766E)
        brand: {
          50:  '#F0FDFA',
          100: '#CCFBF1',
          200: '#99F6E4',
          300: '#5EEAD4',
          400: '#2DD4BF',
          500: '#14B8A6',
          600: '#0D9488',
          700: '#0F766E',
          800: '#115E59',
          900: '#134E4A',
          950: '#042F2E'
        },
        // Legacy alias — pre-Sprint 37 code uses `primary-600/700/800`.
        // We re-map the entire primary scale to brand teal so the old
        // class names now produce the new color, no migration needed.
        primary: {
          50:  '#F0FDFA',
          100: '#CCFBF1',
          200: '#99F6E4',
          300: '#5EEAD4',
          400: '#2DD4BF',
          500: '#14B8A6',
          600: '#0D9488',
          700: '#0F766E',
          800: '#115E59',
          900: '#134E4A',
          950: '#042F2E'
        },
        // Semantic tokens (light/dark variants defined in globals.css).
        // Simple keys, no prefix — Tailwind handles bg-/text-/border- generation.
        // Usage: <div className="bg-canvas text-ink-strong border-edge">
        // Why `canvas` and not `base`? Because `text-base` is the built-in
        // font-size class. If we re-defined `base` as a color, it would
        // shadow the size and break every heading that used `text-base`.
        canvas: 'rgb(var(--bg-primary) / <alpha-value>)',
        raised: 'rgb(var(--bg-surface) / <alpha-value>)',
        edge:   'rgb(var(--border-default) / <alpha-value>)',
        ink: {
          DEFAULT:    'rgb(var(--text-primary) / <alpha-value>)',
          strong:     'rgb(var(--text-primary) / <alpha-value>)',
          muted:      'rgb(var(--text-secondary) / <alpha-value>)',
          subtle:     'rgb(var(--text-muted) / <alpha-value>)',
          brand:      'rgb(var(--text-brand) / <alpha-value>)',
          danger:     'rgb(var(--text-danger) / <alpha-value>)',
          success:    'rgb(var(--text-success) / <alpha-value>)',
          warning:    'rgb(var(--text-warning) / <alpha-value>)'
        },
        tint: {
          brand:   'rgb(var(--bg-brand-light) / <alpha-value>)',
          danger:  'rgb(var(--bg-danger-light) / <alpha-value>)',
          success: 'rgb(var(--bg-success-light) / <alpha-value>)',
          warning: 'rgb(var(--bg-warning-light) / <alpha-value>)'
        }
      },
      borderRadius: {
        'card': '12px'
      },
      backgroundImage: {
        'hero-gradient': 'linear-gradient(135deg, #0F766E 0%, #0A0A0A 100%)',
        'brand-gradient': 'linear-gradient(135deg, #0F766E 0%, #134E4A 100%)'
      }
    }
  },
  plugins: []
};
