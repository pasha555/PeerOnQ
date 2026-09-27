import path from 'path';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, type ConfigEnv, type PluginOption } from 'vite';

import runtimeErrorOverlay from '@replit/vite-plugin-runtime-error-modal';
import { createLocalDownloadTelemetryPluginFromEnvironment } from './server/localDownloadTelemetry';

function stripClientOnlyDirectives() {
  return {
    name: 'peeronq-strip-client-only-directives',
    enforce: 'pre' as const,
    transform(code: string, id: string) {
      const normalized = id.replaceAll('\\', '/');
      if (!normalized.includes('/artifacts/peeronq/src/components/ui/')) return null;
      if (!/^\s*['"]use client['"];/.test(code)) return null;
      return { code: code.replace(/^\s*['"]use client['"];\s*/, ''), map: null };
    },
  };
}

function readRequiredServerPort() {
  const rawPort = process.env.PORT;

  if (!rawPort) {
    throw new Error(
      'PORT environment variable is required but was not provided.',
    );
  }

  const port = Number(rawPort);

  if (Number.isNaN(port) || port <= 0) {
    throw new Error(`Invalid PORT value: "${rawPort}"`);
  }

  return port;
}

function resolvePort(command: ConfigEnv['command']): number | undefined {
  return command === 'serve' ? readRequiredServerPort() : undefined;
}

function resolveBasePath(command: ConfigEnv['command']): string {
  const basePath = process.env.BASE_PATH ?? (command === 'build' ? '/' : null);
  if (!basePath) {
    throw new Error('BASE_PATH environment variable is required but was not provided.');
  }
  return basePath;
}

function portConfig(port: number | undefined): { port?: number } {
  return port === undefined ? {} : { port };
}

function runtimePlugins(command: ConfigEnv['command']): PluginOption[] {
  return command === 'serve'
    ? [runtimeErrorOverlay(), ...createLocalDownloadTelemetryPluginFromEnvironment()]
    : [];
}

async function developmentPlugins(): Promise<PluginOption[]> {
  if (process.env.NODE_ENV === 'production' || process.env.REPL_ID === undefined) return [];
  const [cartographerModule, bannerModule] = await Promise.all([
    import('@replit/vite-plugin-cartographer'),
    import('@replit/vite-plugin-dev-banner'),
  ]);
  return [
    cartographerModule.cartographer({ root: path.resolve(import.meta.dirname, '..') }),
    bannerModule.devBanner(),
  ];
}

export default defineConfig(async ({ command }) => {
  const port = resolvePort(command);
  const basePath = resolveBasePath(command);
  const replitPlugins = await developmentPlugins();

  return {
    base: basePath,
    plugins: [
      stripClientOnlyDirectives(),
      react(),
      tailwindcss(),
      ...runtimePlugins(command),
      ...replitPlugins,
    ],
    resolve: {
      alias: {
        '@': path.resolve(import.meta.dirname, 'src'),
        '@assets': path.resolve(
          import.meta.dirname,
          '..',
          '..',
          'attached_assets',
        ),
      },
      dedupe: ['react', 'react-dom'],
    },
    root: path.resolve(import.meta.dirname),
    build: {
      outDir: path.resolve(import.meta.dirname, 'dist/public'),
      emptyOutDir: true,
      rollupOptions: {
        output: {
          manualChunks(id) {
            if (!id.includes('node_modules')) return undefined;
            if (/[\\/]node_modules[\\/].pnpm[\\/](?:recharts|d3-|victory-)/.test(id)) return 'charts-vendor';
            if (/[\\/]node_modules[\\/].pnpm[\\/](?:@radix-ui|cmdk|vaul|embla-carousel)/.test(id)) return 'ui-vendor';
            return 'vendor';
          },
        },
      },
    },
    server: {
      ...portConfig(port),
      strictPort: true,
      host: '0.0.0.0',
      allowedHosts: true,
      fs: {
        strict: true,
      },
    },
    preview: {
      ...portConfig(port),
      host: '0.0.0.0',
      allowedHosts: true,
    },
  };
});
