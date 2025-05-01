// @ts-check
import {defineConfig} from 'astro/config';
import starlight from '@astrojs/starlight';
import tailwindcss from '@tailwindcss/vite';
import rehypeAstroRelativeMarkdownLinks from "astro-rehype-relative-markdown-links";

export default defineConfig({
    integrations: [
        starlight({
            title: 'Replicator',
            logo: {
                dark: "./src/assets/kurrent-logo-white.svg",
                light: "./src/assets/kurrent-logo-black.svg",
                replacesTitle: true
            },
            customCss: [
                './src/styles/global.css',
                './src/fonts/font-face.css',
                './src/styles/custom.css',
            ],
            favicon: './favicon.ico',
            social: [{icon: 'github', label: 'GitHub', href: 'https://github.com/kurrent-io/replicator'}],
            sidebar: [
                {
                    label: 'Introduction',
                    autogenerate: {directory: 'intro'},
                },
                {
                    label: 'Features',
                    autogenerate: {directory: 'features'},
                },
                {
                    label: 'Deployment',
                    autogenerate: {directory: 'deployment'},
                },
            ],
        }),
    ],

    vite: {
        plugins: [tailwindcss()],
    },
    markdown: {
        rehypePlugins: [rehypeAstroRelativeMarkdownLinks],
    },
});