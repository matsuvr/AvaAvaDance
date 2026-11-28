import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://matsuvr.github.io',
  base: '/AvaAvaDance/',
  integrations: [starlight({
    title: 'AvaAvaDance 操作マニュアル',
    defaultLocale: 'root',
    locales: { root: { label: '日本語', lang: 'ja' } },
    sidebar: [
      { label: 'Windows版を使う', items: [
        { label: 'モデルを読み込む', slug: '' },
        { label: 'ポーズを付けて再生する', slug: 'animation' },
        { label: '作品を保存・出力する', slug: 'output' },
      ] },
      { label: '操作を調べる', items: [{ label: 'ショートカット早見表', slug: 'keys' }] },
    ],
  })],
});
