/**
 * The editor's theme decides the colours, not a setting of the app's. VS Code
 * marks the body with the kind of theme and changes the mark when the theme
 * changes, without reloading the webview.
 */
export function followEditorTheme(): void {
  const root = document.documentElement;
  const apply = (): void => {
    const classes = document.body.classList;
    const light =
      classes.contains("vscode-light") || classes.contains("vscode-high-contrast-light");
    root.setAttribute("data-theme", light ? "light" : "dark");
  };
  root.setAttribute("data-syntax", "vsCode");
  apply();
  new MutationObserver(apply).observe(document.body, { attributeFilter: ["class"] });
}
