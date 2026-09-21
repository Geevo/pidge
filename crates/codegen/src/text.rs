//! Shared spelling: the pieces every generator needs, so four languages cannot
//! disagree about what the same request is.

use api_client_core::KeyValueEntry;
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, utf8_percent_encode};

/// A timeout as seconds, for languages that count in them. No trailing zeros:
/// 30000 is `30`, 1500 is `1.5`.
pub fn seconds(milliseconds: u64) -> String {
    if milliseconds.is_multiple_of(1000) {
        return (milliseconds / 1000).to_string();
    }
    format!("{}", milliseconds as f64 / 1000.0)
}

/// Everything `application/x-www-form-urlencoded` escapes. A space is `+` here,
/// which is the one place that rule holds — the engine writes the same bytes.
const FORM: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'*')
    .remove(b'-')
    .remove(b'.')
    .remove(b'_');

/// A form body, encoded exactly as the engine encodes it.
pub fn form_encoded(entries: &[KeyValueEntry]) -> String {
    entries
        .iter()
        .filter(|entry| entry.is_active())
        .map(|entry| {
            format!(
                "{}={}",
                form_encode(entry.name.trim()),
                form_encode(&entry.value)
            )
        })
        .collect::<Vec<_>>()
        .join("&")
}

fn form_encode(value: &str) -> String {
    utf8_percent_encode(value, FORM)
        .to_string()
        .replace("%20", "+")
}

/// Headers as a dictionary can hold them: one entry per name.
///
/// Python takes a repeated key and keeps the last; PowerShell will not parse a
/// hash literal with one at all. Folding repeats into a comma-separated value
/// is what HTTP itself says two rows of the same name mean, and keeps both.
/// The flag is for the note that says so.
pub fn fold(headers: &[(String, String)]) -> (Vec<(String, String)>, bool) {
    let mut folded: Vec<(String, String)> = Vec::new();
    let mut collided = false;

    for (name, value) in headers {
        match folded
            .iter_mut()
            .find(|(existing, _)| existing.eq_ignore_ascii_case(name))
        {
            Some((_, existing)) => {
                existing.push_str(", ");
                existing.push_str(value);
                collided = true;
            }
            None => folded.push((name.clone(), value.clone())),
        }
    }

    (folded, collided)
}

/// The note [`fold`] earns when it had to.
pub const FOLDED_NOTE: &str = "Two header rows share a name. They are sent here as one comma-separated value, \
     which is what a repeated header means.";

/// Pulls `Content-Type` out of a header list, for the languages that set it
/// somewhere other than with the rest — .NET puts it on the content, and
/// PowerShell takes it as its own parameter.
pub fn take_content_type(headers: &mut Vec<(String, String)>) -> Option<String> {
    let index = headers
        .iter()
        .position(|(name, _)| name.eq_ignore_ascii_case("content-type"))?;
    Some(headers.remove(index).1)
}

/// A note as comment lines, wrapped so none of them runs off the pane.
///
/// `prefix` is the comment marker and the space after it: `# ` or `// `.
///
/// A line of the note that starts with a space is left exactly as it is. That
/// is for a command somebody is meant to copy: wrapping one turns it into three
/// lines that cannot be pasted, which is worse than a line that runs long.
pub fn comment(prefix: &str, note: &str) -> String {
    note.lines()
        .map(|line| {
            if line.starts_with(char::is_whitespace) {
                format!("{prefix}{line}")
            } else {
                wrap(prefix, line)
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn wrap(prefix: &str, note: &str) -> String {
    const WIDTH: usize = 80;
    let mut lines: Vec<String> = Vec::new();
    let mut line = String::new();

    for word in note.split_whitespace() {
        let candidate = if line.is_empty() {
            word.len() + prefix.len()
        } else {
            line.len() + prefix.len() + 1 + word.len()
        };
        if !line.is_empty() && candidate > WIDTH {
            lines.push(format!("{prefix}{line}"));
            line = String::new();
        }
        if !line.is_empty() {
            line.push(' ');
        }
        line.push_str(word);
    }
    if !line.is_empty() {
        lines.push(format!("{prefix}{line}"));
    }

    lines.join("\n")
}

/**
 * Marks a line as the inside of a multi-line string literal.
 *
 * The body of a request is carried into the snippet verbatim, and in a language
 * whose code sits inside a function the snippet is then indented — which would
 * put four spaces inside the body and send something nobody wrote. A literal
 * says where it begins and ends, rather than the indenter guessing, and the
 * mark is taken out again once everything has been laid out.
 */
pub const KEEP: char = '\u{1}';

/// Marks every line of `block` after the first, which is where a literal's own
/// content starts.
pub fn keep(block: &str) -> String {
    let mut lines = block.lines();
    let first = lines.next().unwrap_or_default().to_string();
    let rest: Vec<String> = lines.map(|line| format!("{KEEP}{line}")).collect();
    if rest.is_empty() {
        return first;
    }
    format!("{first}\n{}", rest.join("\n"))
}

/// Takes the marks out, once nothing is going to be indented again.
pub fn settle(block: String) -> String {
    block.replace(KEEP, "")
}

/// Indents every line of a block by `spaces`, leaving blank lines blank and
/// leaving the inside of a literal exactly where it is.
pub fn indent(block: &str, spaces: usize) -> String {
    let pad = " ".repeat(spaces);
    block
        .lines()
        .map(|line| {
            if line.is_empty() || line.starts_with(KEEP) {
                line.to_string()
            } else {
                format!("{pad}{line}")
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
}

/// The file name a multipart part is sent under, which is the path's own last
/// component when nothing else was given. The same rule the engine uses.
pub fn file_name(path: &str, given: Option<&String>) -> String {
    if let Some(name) = given {
        return name.clone();
    }
    std::path::Path::new(path)
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "file".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seconds_drop_a_whole_number_of_zeros() {
        assert_eq!(seconds(30_000), "30");
        assert_eq!(seconds(1_500), "1.5");
        assert_eq!(seconds(0), "0");
    }

    /// The one place `+` means space. The query string is not this.
    #[test]
    fn a_form_body_writes_a_space_as_a_plus() {
        let entries = vec![KeyValueEntry::new("full name", "Ada Lovelace")];
        assert_eq!(form_encoded(&entries), "full+name=Ada+Lovelace");
    }

    #[test]
    fn a_plus_in_a_form_value_is_escaped() {
        let entries = vec![KeyValueEntry::new("phone", "+44 7700 900000")];
        assert_eq!(form_encoded(&entries), "phone=%2B44+7700+900000");
    }

    /// A body's own indentation is data, and survives the code around it
    /// being laid out.
    #[test]
    fn the_inside_of_a_literal_is_not_indented() {
        let literal = keep("r#\"{\n  \"a\": 1\n}\"#");
        let block = indent(&format!("let payload = {literal};"), 4);

        assert_eq!(settle(block), "    let payload = r#\"{\n  \"a\": 1\n}\"#;");
    }

    #[test]
    fn a_note_is_wrapped_rather_than_left_to_run_off() {
        let note = comment("# ", &"word ".repeat(30));
        assert!(note.lines().count() > 1);
        assert!(note.lines().all(|line| line.len() <= 80));
        assert!(note.lines().all(|line| line.starts_with("# ")));
    }

    /// A command is there to be copied, and three lines of one cannot be.
    #[test]
    fn an_indented_line_is_left_exactly_as_it_is() {
        let command = "openssl pkcs12 -in /a/very/long/path/to/a/client/bundle.p12 \
                       -out /a/very/long/path/to/a/client/bundle.pem -nodes";
        let note = comment("# ", &format!("Convert it first:\n  {command}"));

        let lines: Vec<&str> = note.lines().collect();
        assert_eq!(lines[0], "# Convert it first:");
        assert_eq!(lines[1], format!("#   {command}"));
        assert_eq!(lines.len(), 2);
    }

    #[test]
    fn repeated_header_names_are_folded_into_one() {
        let headers = vec![
            ("Accept".to_string(), "text/html".to_string()),
            ("accept".to_string(), "application/json".to_string()),
        ];
        let (folded, collided) = fold(&headers);
        assert!(collided);
        assert_eq!(
            folded,
            vec![(
                "Accept".to_string(),
                "text/html, application/json".to_string()
            )]
        );
    }
}
