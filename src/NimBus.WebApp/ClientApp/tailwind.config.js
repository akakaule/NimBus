/** @type {import('tailwindcss').Config} */
export default {
  darkMode: 'class',
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        // NimBus primary (coral/orange — action and focus ONLY, never status)
        primary: {
          DEFAULT: '#E8743C',
          50: '#FBE7D7',
          100: '#F6D4B7',
          200: '#F3C597',
          300: '#EFB477',
          400: '#EC9B58',
          500: '#E8743C',
          600: '#D45F25',
          700: '#A94B1D',
          800: '#7E3815',
          900: '#52240D',
          tint: '#FCEEDD',
        },
        // Cream / warm-neutral surfaces (light theme)
        canvas: '#F4F2EA',
        surface: {
          DEFAULT: '#FAF8F2',
          2: '#ECE7DA',
        },
        ink: {
          DEFAULT: '#1A1814',
          2: '#4A463D',
          3: '#8A8473',
        },
        // Status hues (paired tints) — dedicated to status, never primary action
        status: {
          // Semantic NimBus statuses
          success: { DEFAULT: '#2E8F5E', 50: '#DCEFE4', ink: '#1F6B45' },
          warning: { DEFAULT: '#C98A1B', 50: '#F6E7C7', ink: '#8A5E0F' },
          danger:  { DEFAULT: '#C2412E', 50: '#F4D9D3', ink: '#8E2C1F' },
          info:    { DEFAULT: '#3A6FB0', 50: '#D8E3F2', ink: '#234E80' },
          // Legacy NimBus message-status mappings (preserved for existing callsites)
          failed: '#C2412E',
          pending: '#C98A1B',
          completed: '#2E8F5E',
          deferred: '#C98A1B',
          deadlettered: '#8E2C1F',
          skipped: '#8A8473',
          unsupported: '#C9C1AB',
        },
        // Purple — namespace pills and schema type tags
        nimbus: {
          purple: '#6B3FA3',
          'purple-50': '#ECE2F4',
        },
        // shadcn-style aliases driven by CSS vars (light/dark via :root / .dark)
        background: 'var(--background)',
        foreground: 'var(--foreground)',
        card: {
          DEFAULT: 'var(--card)',
          foreground: 'var(--card-foreground)',
        },
        muted: {
          DEFAULT: 'var(--muted)',
          foreground: 'var(--muted-foreground)',
        },
        accent: {
          DEFAULT: 'var(--accent)',
          foreground: 'var(--accent-foreground)',
        },
        popover: {
          DEFAULT: 'var(--popover)',
          foreground: 'var(--popover-foreground)',
        },
        border: {
          DEFAULT: 'var(--border)',
          strong: 'var(--border-strong)',
        },
        input: 'var(--input)',
        ring: 'var(--ring)',
        'delegate-blue': {
          DEFAULT: '#3A6FB0',
          50: '#D8E3F2',
          100: '#BFD0E5',
          200: '#A6BDD7',
          300: '#8DAACA',
          400: '#7497BC',
          500: '#3A6FB0',
          600: '#2E5990',
          700: '#234470',
          800: '#172E50',
          900: '#0C1930',
        },
      },
      fontFamily: {
        sans: [
          'Manrope',
          'ui-sans-serif',
          'system-ui',
          '-apple-system',
          'Segoe UI',
          'Roboto',
          'Helvetica Neue',
          'sans-serif',
        ],
        mono: [
          'JetBrains Mono',
          'ui-monospace',
          'SF Mono',
          'Menlo',
          'Monaco',
          'Consolas',
          'Liberation Mono',
          'Courier New',
          'monospace',
        ],
      },
      boxShadow: {
        'nb-sm':
          '0 1px 2px rgba(26,24,20,0.04), 0 1px 1px rgba(26,24,20,0.03)',
        'nb-md':
          '0 4px 10px rgba(26,24,20,0.06), 0 2px 4px rgba(26,24,20,0.04)',
        'nb-lg':
          '0 18px 40px rgba(26,24,20,0.08), 0 4px 12px rgba(26,24,20,0.04)',
      },
      borderRadius: {
        'nb-sm': '4px',
        'nb-md': '8px',
        'nb-lg': '12px',
      },
      keyframes: {
        'fade-in': {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        'fade-out': {
          '0%': { opacity: '1' },
          '100%': { opacity: '0' },
        },
        'zoom-in': {
          '0%': { opacity: '0', transform: 'scale(0.95)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
        'zoom-out': {
          '0%': { opacity: '1', transform: 'scale(1)' },
          '100%': { opacity: '0', transform: 'scale(0.95)' },
        },
        'slide-in-from-right': {
          '0%': { transform: 'translateX(100%)' },
          '100%': { transform: 'translateX(0)' },
        },
        'slide-in-from-left': {
          '0%': { transform: 'translateX(-100%)' },
          '100%': { transform: 'translateX(0)' },
        },
        'slide-in-from-top': {
          '0%': { transform: 'translateY(-100%)' },
          '100%': { transform: 'translateY(0)' },
        },
        'slide-in-from-bottom': {
          '0%': { transform: 'translateY(100%)' },
          '100%': { transform: 'translateY(0)' },
        },
        spin: {
          '0%': { transform: 'rotate(0deg)' },
          '100%': { transform: 'rotate(360deg)' },
        },
      },
      animation: {
        'fade-in': 'fade-in 0.2s ease-out',
        'fade-out': 'fade-out 0.2s ease-out',
        'zoom-in': 'zoom-in 0.2s ease-out',
        'zoom-out': 'zoom-out 0.2s ease-out',
        'slide-in-from-right': 'slide-in-from-right 0.3s ease-out',
        'slide-in-from-left': 'slide-in-from-left 0.3s ease-out',
        'slide-in-from-top': 'slide-in-from-top 0.3s ease-out',
        'slide-in-from-bottom': 'slide-in-from-bottom 0.3s ease-out',
        spin: 'spin 1s linear infinite',
        in: 'fade-in 0.2s ease-out, zoom-in 0.2s ease-out',
      },
    },
  },
  plugins: [],
};
