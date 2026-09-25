import {
  SearchQuery,
  closeSearchPanel,
  findNext,
  findPrevious,
  getSearchQuery,
  openSearchPanel,
  search,
  setSearchQuery,
} from "@codemirror/search";
import type { Extension } from "@codemirror/state";
import {
  EditorView,
  type Panel,
  type ViewUpdate,
  keymap,
  runScopeHandlers,
} from "@codemirror/view";

/**
 * Find, for every editor: the response body above all, and the request body
 * and generated code with it, since Ctrl/Cmd+F in any of them should mean the
 * same thing.
 *
 * CodeMirror's own search supplies the matching, the highlights and the
 * commands; only the bar is ours. Its stock panel is a form of checkboxes and
 * replace fields, and a body being read wants less than that: a field, where
 * you are among the matches, the way through them, and case. Plain text only —
 * no regular expressions — so what is typed is what is found.
 */

/*
 * Counting stops here. A two-megabyte body searched for a single letter has
 * tens of thousands of matches, and walking them all on every keystroke would
 * be felt; past this many, "999+" says everything the number would.
 */
const COUNT_LIMIT = 999;

export function findInEditor(): Extension {
  return [
    search({ top: true, literal: true, createPanel: createFindPanel }),
    keymap.of([
      { key: "Mod-f", run: openSearchPanel, scope: "editor search-panel", preventDefault: true },
      {
        key: "F3",
        run: findNext,
        shift: findPrevious,
        scope: "editor search-panel",
        preventDefault: true,
      },
      {
        key: "Mod-g",
        run: findNext,
        shift: findPrevious,
        scope: "editor search-panel",
        preventDefault: true,
      },
      { key: "Escape", run: closeSearchPanel, scope: "editor search-panel" },
    ]),
  ];
}

/** Opens the find bar on an editor from outside it, as Ctrl/Cmd+F elsewhere does. */
export function openFind(view: EditorView): void {
  openSearchPanel(view);
}

function createFindPanel(view: EditorView): Panel {
  let caseSensitive = getSearchQuery(view.state).caseSensitive;

  const dom = element("div", "ac-find");

  const input = element("input", "ac-find__field");
  input.type = "text";
  input.placeholder = "Find";
  input.setAttribute("aria-label", "Find");
  input.setAttribute("main-field", "true");
  input.spellcheck = false;
  input.autocomplete = "off";
  input.value = getSearchQuery(view.state).search;

  const count = element("span", "ac-find__count");
  count.setAttribute("aria-live", "polite");

  const matchCase = button("Aa", "Match case", () => {
    caseSensitive = !caseSensitive;
    matchCase.setAttribute("aria-pressed", String(caseSensitive));
    commit();
  });
  matchCase.classList.add("ac-find__case");
  matchCase.setAttribute("aria-pressed", String(caseSensitive));

  const previous = button(icon("M4 10l4-4 4 4"), "Previous match", () => findPrevious(view));
  const next = button(icon("M4 6l4 4 4-4"), "Next match", () => findNext(view));
  const close = button(icon("M4 4l8 8M12 4l-8 8"), "Close find", () => {
    closeSearchPanel(view);
    view.focus();
  });

  dom.append(input, count, matchCase, previous, next, close);

  /*
   * The query follows the field as it is typed, and the selection jumps to the
   * first match at or after where it already was — so typing more letters
   * narrows onto the same match rather than skipping on to the next one.
   */
  function commit() {
    const query = new SearchQuery({ search: input.value, caseSensitive, literal: true });
    if (query.eq(getSearchQuery(view.state))) return;

    /*
     * Not `findNext`: it selects the field's text once it has moved, ready for
     * the next search, and while typing that made each letter replace the one
     * before it. Found here instead, wrapping to the top if need be.
     */
    const from = view.state.selection.main.from;
    const match = query.search ? (firstMatch(query, from) ?? firstMatch(query, 0)) : null;
    view.dispatch({
      effects: [
        setSearchQuery.of(query),
        ...(match ? [EditorView.scrollIntoView(match.from, { y: "center" })] : []),
      ],
      ...(match ? { selection: { anchor: match.from, head: match.to } } : {}),
    });
  }

  function firstMatch(query: SearchQuery, from: number) {
    const found = query.getCursor(view.state, from).next();
    return found.done ? null : found.value;
  }

  input.addEventListener("input", commit);
  input.addEventListener("keydown", (event) => {
    // F3, Ctrl/Cmd+G, Escape and Ctrl/Cmd+F again, from the keymap above.
    if (runScopeHandlers(view, event, "search-panel")) {
      event.preventDefault();
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      if (event.shiftKey) findPrevious(view);
      else findNext(view);
    }
  });

  function refresh(state = view.state) {
    const query = getSearchQuery(state);
    if (!query.search || !query.valid) {
      count.textContent = "";
      dom.classList.remove("ac-find--none");
      return;
    }

    const selection = state.selection.main;
    const cursor = query.getCursor(state);
    let total = 0;
    let current = 0;
    for (let match = cursor.next(); !match.done; match = cursor.next()) {
      total += 1;
      if (match.value.from === selection.from && match.value.to === selection.to) current = total;
      if (total > COUNT_LIMIT) break;
    }

    dom.classList.toggle("ac-find--none", total === 0);
    if (total === 0) count.textContent = "No results";
    else if (total > COUNT_LIMIT) count.textContent = `${COUNT_LIMIT}+`;
    else count.textContent = current > 0 ? `${current} of ${total}` : `${total} found`;
  }

  return {
    dom,
    top: true,
    mount() {
      input.focus();
      input.select();
      refresh();
    },
    update(update: ViewUpdate) {
      for (const transaction of update.transactions) {
        for (const effect of transaction.effects) {
          // Opened again over a selection: the field takes the selected text.
          if (effect.is(setSearchQuery) && effect.value.search !== input.value) {
            input.value = effect.value.search;
            caseSensitive = effect.value.caseSensitive;
            matchCase.setAttribute("aria-pressed", String(caseSensitive));
          }
        }
      }
      if (
        update.docChanged ||
        update.selectionSet ||
        update.transactions.some((t) => t.effects.length)
      ) {
        refresh(update.state);
      }
    },
  };
}

function element<K extends keyof HTMLElementTagNameMap>(tag: K, className: string) {
  const node = document.createElement(tag);
  node.className = className;
  return node;
}

function button(content: string | SVGElement, label: string, onClick: () => void) {
  const node = element("button", "ac-find__button");
  node.type = "button";
  node.setAttribute("aria-label", label);
  node.title = label;
  if (typeof content === "string") node.textContent = content;
  else node.append(content);
  // Keeps focus in the field, so Enter still means "next" after a click.
  node.addEventListener("mousedown", (event) => event.preventDefault());
  node.addEventListener("click", onClick);
  return node;
}

/* The same 16-unit, currentColor stroke as the icons in `icons.tsx`. */
function icon(path: string): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("width", "13");
  svg.setAttribute("height", "13");
  svg.setAttribute("viewBox", "0 0 16 16");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.5");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  const line = document.createElementNS(ns, "path");
  line.setAttribute("d", path);
  svg.append(line);
  return svg;
}
