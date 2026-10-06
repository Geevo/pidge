// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * Which colours the editors highlight with.
 *
 * Separate from `Theme` because the two answer different questions: the
 * theme is the application's own furniture, and this is the code inside it.
 * Each of the borrowed palettes has a light and a dark form, and the one
 * used follows whichever the theme above resolves to.
 */
export type SyntaxTheme = "app" | "vsCode" | "one" | "github";
