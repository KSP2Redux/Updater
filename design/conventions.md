# KSP2 Redux design conventions

This is the design language of the KSP2 Redux launcher: a dark, monospace, mission-control look. Everything is built for dark backgrounds.

## Setup

No provider or wrapper is required. One hard rule: **always build on a dark background**. Give the page/root a dark fill such as `#101418`, `#181818`, or a dark gradient (`linear-gradient(145deg, #101418 0%, #1c1410 100%)`) before placing components - `GlassPanel` is 55%-alpha dark glass and the `neutral` button is a translucent white fill, so both wash out on white.

## Styling idiom

No utility classes. Style your own layout glue with inline styles or your own CSS, always taking colors, radii, and fonts from the CSS custom properties defined in `styles.css`:

- Brand: `--krx-brand-red`, `--krx-brand-orange`
- Semantic: `--krx-success`, `--krx-success-hover`, `--krx-success-pressed`, `--krx-danger`, `--krx-danger-hover`, `--krx-warning`
- Surfaces: `--krx-glass-surface`, `--krx-glass-border`, `--krx-glass-shadow`, `--krx-surface-panel`, `--krx-input-surface`, `--krx-disabled-surface`
- Text: `--krx-text-primary` (white), `--krx-text-secondary` (#888), `--krx-text-muted` (#CCC), `--krx-text-faint`
- Overlays: `--krx-overlay-hover`, `--krx-overlay-pressed`, `--krx-scrim-soft`, `--krx-scrim-medium`, `--krx-scrim-strong`
- Radii: `--krx-radius-md` (8px, controls), `--krx-radius-lg` (12px, panels)
- Fonts: `--krx-font-mono` (JetBrains Mono - ALL text uses this, weight 300/400/700), `--krx-font-led` (Led Counter 7 - reserved for MainButton-style LED labels, never body text)

Body text is small (11-12px) JetBrains Mono; headings are the same face at 20-24px weight 700. Secondary metadata (dates, bylines) uses `--krx-text-secondary` at 10-12px.

## Components

- `GlassPanel` - the content surface. Panels of content sit in one of these over the dark backdrop.
- `MainButton` (`variant: install | update | launch | cancel`) - the single primary action of a screen, fixed 260x76 LED art. Use at most one visible at a time, like the launcher's sidebar. When `disabled`, set `title` to explain why.
- `Button` (`variant: success | danger | neutral`, `size: sm | md`) - all other actions. `success` for positive actions (refresh, install), `danger` for destructive ones, `neutral` for everything else.

## Where the truth lives

Read `styles.css` before styling anything - it carries every token above, the `@font-face` rules, and the `krx-*` component classes.

## Idiomatic example

```jsx
<div style={{ background: '#101418', minHeight: '100vh', padding: 24, fontFamily: 'var(--krx-font-mono)' }}>
  <GlassPanel style={{ width: 460 }}>
    <div style={{ fontSize: 24, fontWeight: 700, color: 'var(--krx-text-primary)' }}>Beta 7 Parts Preview</div>
    <div style={{ fontSize: 12, color: 'var(--krx-text-secondary)', marginBottom: 12 }}>Posted 2026-06-30</div>
    <Button variant="neutral" size="sm">View on blog</Button>
  </GlassPanel>
  <div style={{ marginTop: 16 }}>
    <MainButton variant="launch" />
  </div>
</div>
```
