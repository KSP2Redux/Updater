# design-sync notes

- The design system is NOT a pre-existing JS repo: it lives in `design-system/` and was hand-built (2026-07-09) as a web translation of the launcher's Avalonia styling. The launcher stays the source of truth - `design-system/build.mjs` copies fonts and inlines the LED button art from `Ksp2Redux.Tools.Launcher/Assets/` at build time, and `src/tokens.css` mirrors `Colors.axaml`/`Radii.axaml` by hand. If the launcher palette changes, update tokens.css to match.
- Build: `npm run build` inside `design-system/` (esbuild bundle + tsc declarations + CSS stitch). Node 22 / npm on Windows.
- Converter invocation from repo root: `--node-modules ./design-system/node_modules --entry ./design-system/dist/index.js`.
- Render check runs against the system Chrome via `DS_CHROMIUM_PATH="C:\Program Files\Google\Chrome\Application\chrome.exe"` - no playwright chromium is downloaded on this machine; the `playwright` npm package in `.ds-sync/` was installed with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`.
- All components are designed for dark backgrounds. Previews wrap stories in a local `DarkBackdrop` helper; without it, GlassPanel and the neutral Button wash out on the white preview card. `GlassPanel` has `cardMode: column` (stories are 440px+ wide).
- Hover/pressed states (`--krx-overlay-*` tints, button hover fills) can't render statically - not previewed, by design.

## Re-sync risks

- `tokens.css` duplicates values from `Colors.axaml` by hand - launcher palette changes do NOT propagate automatically; diff the two when the launcher's look changes.
- Button art and fonts are copied from the launcher's Assets at build time; if those files move or get renamed, `design-system/build.mjs` fails loudly.
- `DS_CHROMIUM_PATH` points at the system Chrome install; a Chrome uninstall breaks the render check (install playwright chromium instead).
- First upload was blocked on DesignSync authorization (no interactive /design-login in this environment) - if `.design-sync/config.json` has no `projectId`, no project exists yet and the first upload still needs the full §1 target-settlement flow.
