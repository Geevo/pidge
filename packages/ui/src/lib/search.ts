/**
 * Whether a list entry matches what was typed into its drawer's search.
 *
 * Every word of the query has to appear somewhere in the entry's fields, in
 * any order and any case, so "post users" finds `POST /users` and "users post"
 * finds it too. A blank query matches everything.
 */
export function matchesSearch(query: string, fields: readonly (string | null)[]): boolean {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;

  const haystack = fields
    .filter((field): field is string => field !== null)
    .join(" ")
    .toLowerCase();
  return terms.every((term) => haystack.includes(term));
}
