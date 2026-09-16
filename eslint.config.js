import js from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: [
      "**/dist/**",
      "**/media/webview*.js",
      "**/node_modules/**",
      "target/**",
      // Generated from the Rust types by `pnpm gen:types`.
      "packages/ui/src/generated/**",
    ],
  },

  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked,

  {
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
      globals: { ...globals.browser, ...globals.node },
    },
    rules: {
      "@typescript-eslint/consistent-type-imports": "error",
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      "no-console": ["warn", { allow: ["warn", "error"] }],
    },
  },

  {
    files: ["packages/ui/**/*.{ts,tsx}", "apps/*/src/**/*.{ts,tsx}"],
    plugins: { "react-hooks": reactHooks, "react-refresh": reactRefresh },
    rules: reactHooks.configs.recommended.rules,
  },

  {
    files: ["**/*.test.ts", "**/*.test.tsx", "packages/ui/src/test/**"],
    rules: {
      "@typescript-eslint/no-unsafe-assignment": "off",
      "@typescript-eslint/no-unsafe-member-access": "off",
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  },

  // Build scripts and flat configs live outside any tsconfig, so they get the
  // untyped rule set.
  {
    files: ["**/*.mjs", "**/*.js", "**/*.config.ts", "scripts/**"],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: {
      parserOptions: { projectService: false, project: false },
      globals: globals.node,
    },
    rules: {
      "no-console": "off",
    },
  },
);
