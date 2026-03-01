/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        base: '#0f1117',
        surface: '#1a1d27',
        elevated: '#20243a',
        'border-dim': '#2a2d3a',
        accent: '#00d4aa',
        'accent-dim': 'rgba(0,212,170,0.12)',
        danger: '#ff4757',
        warning: '#ffa502',
        muted: '#8b90a0',
        primary: '#e8eaf0',
      },
      fontFamily: {
        body: ['Outfit', 'sans-serif'],
        mono: ['"DM Mono"', 'monospace'],
      },
      animation: {
        'pulse-dot': 'pulse-dot 1.5s ease-in-out infinite',
        'slide-in-right': 'slide-in-right 200ms ease forwards',
        'fade-up': 'fade-up 150ms ease forwards',
      },
      keyframes: {
        'pulse-dot': {
          '0%, 100%': { opacity: '1' },
          '50%': { opacity: '0.3' },
        },
        'slide-in-right': {
          from: { transform: 'translateX(16px)', opacity: '0' },
          to: { transform: 'translateX(0)', opacity: '1' },
        },
        'fade-up': {
          from: { transform: 'translateY(6px)', opacity: '0' },
          to: { transform: 'translateY(0)', opacity: '1' },
        },
      },
    },
  },
  plugins: [],
}
