// ESLint 9 flat config for the SPA. Kept deliberately small: TypeScript itself (npm run typecheck)
// is the type authority, so this layer only catches what the compiler does not.
import js from '@eslint/js'
import globals from 'globals'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**', '*.tsbuildinfo'] },

  js.configs.recommended,
  ...tseslint.configs.recommended,

  {
    files: ['src/**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: globals.browser,
    },
    rules: {
      // The API layer returns `unknown` from JSON.parse and narrows deliberately; an unused
      // argument prefixed with _ is an intentional signature match.
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      // Debug statements must not reach the bundle; warnings and errors may.
      'no-console': ['error', { allow: ['warn', 'error'] }],
    },
  },

  {
    files: ['*.config.{js,ts}'],
    languageOptions: { globals: globals.node },
  },
)
