// Build for @ksp2redux/design: bundles the JS, stitches dist/styles.css, inlines
// the launcher's LED button art as data URLs, and copies its font files - the
// launcher's Assets stay the single source of truth for art and fonts.
import {build} from 'esbuild';
import {cp, mkdir, readFile, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const launcherAssets = path.join(root, '..', '..', 'Ksp2Redux.Tools.Launcher', 'Assets');
const dist = path.join(root, 'dist');

await mkdir(path.join(dist, 'fonts'), {recursive: true});

await build({
    entryPoints: [path.join(root, 'src', 'index.ts')],
    bundle: true,
    format: 'esm',
    outfile: path.join(dist, 'index.js'),
    external: ['react', 'react-dom', 'react/jsx-runtime'],
});

const fonts = ['JetBrainsMono-Light.ttf', 'JetBrainsMono-Regular.ttf', 'JetBrainsMono-Bold.ttf', 'led_counter-7.ttf'];
for (const f of fonts) {
    await cp(path.join(launcherAssets, 'Fonts', f), path.join(dist, 'fonts', f));
}

const artCss = (await Promise.all(
    ['install', 'update', 'launch', 'cancel'].map(async v => {
        const png = await readFile(path.join(launcherAssets, `button-${v}.png`));
        return `.krx-main-button--${v} { background-image: url("data:image/png;base64,${png.toString('base64')}"); }`;
    }),
)).join('\n');

const css = [
    await readFile(path.join(root, 'src', 'tokens.css'), 'utf8'),
    await readFile(path.join(root, 'src', 'fonts.css'), 'utf8'),
    await readFile(path.join(root, 'src', 'components.css'), 'utf8'),
    artCss,
].join('\n');
await writeFile(path.join(dist, 'styles.css'), css);

console.log('built dist/index.js, dist/styles.css, dist/fonts/');
