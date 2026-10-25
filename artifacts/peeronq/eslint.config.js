import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    // Vendored shadcn output and build artifacts are not ours to lint.
    ignores: ['dist', 'src/components/ui/**', 'src/hooks/use-toast.ts', 'src/hooks/use-mobile.tsx'],
  },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // React Compiler rules flag idiomatic patterns used throughout this
      // prototype (data loading in effects, Date.now() while filtering).
      // Kept visible as warnings instead of blocking the lint gate.
      'react-hooks/set-state-in-effect': 'warn',
      'react-hooks/purity': 'warn',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
    },
  },
  {
    files: ['src/test/**/*.{ts,tsx}', 'vitest.config.ts', 'vite.config.ts'],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
  },
);
