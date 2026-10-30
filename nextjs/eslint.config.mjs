import { defineConfig, globalIgnores } from "eslint/config";
import { fixupConfigRules, fixupPluginRules } from "@eslint/compat";
import react from "eslint-plugin-react";
import unusedImports from "eslint-plugin-unused-imports";
import _import from "eslint-plugin-import";
import typescriptEslint from "@typescript-eslint/eslint-plugin";
import jsxA11Y from "eslint-plugin-jsx-a11y";
import prettier from "eslint-plugin-prettier";
import globals from "globals";
import tsParser from "@typescript-eslint/parser";
import path from "node:path";
import { fileURLToPath } from "node:url";
import js from "@eslint/js";
import { FlatCompat } from "@eslint/eslintrc";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const compat = new FlatCompat({
    baseDirectory: __dirname,
    recommendedConfig: js.configs.recommended,
    allConfig: js.configs.all
});

export default defineConfig([globalIgnores([
    ".now/*",
    "**/*.css",
    "**/.changeset",
    "**/dist",
    "esm/*",
    "public/*",
    "tests/*",
    "scripts/*",
    "**/*.config.js",
    "**/.DS_Store",
    "**/node_modules",
    "**/coverage",
    "**/.next",
    "**/.open-next",
    "**/.wrangler",
    "**/build",
    "!**/.commitlintrc.cjs",
    "!**/.lintstagedrc.cjs",
    "!**/jest.config.js",
    "!**/plopfile.js",
    "!**/react-shim.js",
    "!**/tsup.config.ts",
]), {
    extends: fixupConfigRules(compat.extends(
        "plugin:react/recommended",
        "plugin:prettier/recommended",
        "plugin:react-hooks/recommended",
        "plugin:jsx-a11y/recommended",
        "plugin:@next/next/recommended-legacy",
    )),

    plugins: {
        react: fixupPluginRules(react),
        "unused-imports": unusedImports,
        import: fixupPluginRules(_import),
        "@typescript-eslint": typescriptEslint,
        "jsx-a11y": fixupPluginRules(jsxA11Y),
        prettier: fixupPluginRules(prettier),
    },

    languageOptions: {
        globals: {
            ...Object.fromEntries(Object.entries(globals.browser).map(([key]) => [key, "off"])),
            ...globals.node,
        },

        parser: tsParser,
        ecmaVersion: 12,
        sourceType: "module",

        parserOptions: {
            ecmaFeatures: {
                jsx: true,
            },

            // Type information for the type-aware @typescript-eslint rules below.
            projectService: true,
            tsconfigRootDir: __dirname,
        },
    },

    settings: {
        react: {
            version: "detect",
        },
    },

    files: ["**/*.ts", "**/*.tsx"],

    rules: {
        "no-console": "warn",
        "react/no-unescaped-entities": "warn",
        "react/prop-types": "off",
        "react/jsx-uses-react": "off",
        "react/react-in-jsx-scope": "off",
        "react-hooks/exhaustive-deps": "off",

        // eslint-plugin-react-hooks 5->7 merged in the React Compiler's
        // correctness rules as part of "recommended". Of those, 2 -
        // set-state-in-effect (5 files) and immutability (2 files) - flag
        // real (pre-existing, not introduced by this upgrade) patterns that
        // would require actual behavioral component rewrites to satisfy -
        // out of scope for a lint-tooling/dependency-upgrade PR. Left off
        // deliberately; revisit as dedicated app-code follow-up work.
        //
        // The rest of "recommended" (error-boundaries, purity,
        // set-state-in-render, unsupported-syntax, config, gating, refs,
        // globals, use-memo, preserve-manual-memoization,
        // static-components, incompatible-library) is left on - verified
        // clean (no violations) as of this bump. rules-of-hooks (the
        // original, stable rule) also stays on.
        "react-hooks/set-state-in-effect": "off",
        "react-hooks/immutability": "off",

        "jsx-a11y/click-events-have-key-events": "warn",
        "jsx-a11y/interactive-supports-focus": "warn",
        "prettier/prettier": "warn",
        "no-unused-vars": "off",
        "unused-imports/no-unused-vars": "off",
        "unused-imports/no-unused-imports": "warn",

        "@typescript-eslint/no-unused-vars": ["warn", {
            args: "after-used",
            ignoreRestSiblings: false,
            argsIgnorePattern: "^_.*?$",
        }],

        // Type-aware: they catch an untyped JSON.parse / .json() read or an
        // unawaited promise, which neither a grep nor tsc --noEmit sees.
        "@typescript-eslint/no-unsafe-argument": "error",
        "@typescript-eslint/no-unsafe-member-access": "error",
        "@typescript-eslint/no-unsafe-assignment": "error",
        "@typescript-eslint/no-unsafe-call": "error",
        "@typescript-eslint/no-floating-promises": "error",

        "import/order": ["warn", {
            groups: [
                "type",
                "builtin",
                "object",
                "external",
                "internal",
                "parent",
                "sibling",
                "index",
            ],

            pathGroups: [{
                pattern: "~/**",
                group: "external",
                position: "after",
            }],

            "newlines-between": "always",
        }],

        "react/self-closing-comp": "warn",

        "react/jsx-sort-props": ["warn", {
            callbacksLast: true,
            shorthandFirst: true,
            noSortAlphabetically: false,
            reservedFirst: true,
        }],

        "padding-line-between-statements": ["warn", {
            blankLine: "always",
            prev: "*",
            next: "return",
        }, {
                blankLine: "always",
                prev: ["const", "let", "var"],
                next: "*",
            }, {
                blankLine: "any",
                prev: ["const", "let", "var"],
                next: ["const", "let", "var"],
            }],
    },
}, {
    // tsconfig excludes the Worker entry (it imports the generated
    // `.open-next/worker.js`) and the `npm run cf-typegen` output, so the
    // project service has no types for either.
    files: ["worker.ts", "worker-configuration.d.ts"],

    languageOptions: {
        parserOptions: {
            projectService: false,
        },
    },

    rules: {
        "@typescript-eslint/no-unsafe-argument": "off",
        "@typescript-eslint/no-unsafe-member-access": "off",
        "@typescript-eslint/no-unsafe-assignment": "off",
        "@typescript-eslint/no-unsafe-call": "off",
        "@typescript-eslint/no-floating-promises": "off",
    },
}]);