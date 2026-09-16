use api_client_core::{AuthConfig, HttpRequest, KeyValueEntry, RequestBody, RequestErrorKind};
use api_client_variables::{Environment, VariableSet, resolve_request, substitute};

fn vars() -> VariableSet {
    [("baseUrl", "https://api.example.com"), ("token", "abc123")]
        .into_iter()
        .collect()
}

#[test]
fn replaces_known_variables() {
    assert_eq!(
        substitute("{{baseUrl}}/users", &vars()).unwrap(),
        "https://api.example.com/users"
    );
}

#[test]
fn replaces_several_occurrences() {
    assert_eq!(
        substitute("{{token}}-{{token}}", &vars()).unwrap(),
        "abc123-abc123"
    );
}

#[test]
fn tolerates_whitespace_inside_the_braces() {
    assert_eq!(substitute("{{  token  }}", &vars()).unwrap(), "abc123");
}

#[test]
fn text_without_variables_is_untouched() {
    assert_eq!(
        substitute("https://example.com/x", &vars()).unwrap(),
        "https://example.com/x"
    );
}

#[test]
fn an_unclosed_placeholder_stays_literal() {
    assert_eq!(substitute("a {{token b", &vars()).unwrap(), "a {{token b");
}

#[test]
fn reports_every_missing_name_at_once() {
    let missing = substitute("{{a}}/{{b}}/{{a}}", &vars()).unwrap_err();
    assert_eq!(missing, vec!["a".to_string(), "b".to_string()]);
}

#[test]
fn resolves_across_the_whole_request() {
    let mut request = HttpRequest::get("{{baseUrl}}/users");
    request.query_params = vec![KeyValueEntry::new("key", "{{token}}")];
    request.headers = vec![KeyValueEntry::new("X-Token", "{{token}}")];
    request.auth = AuthConfig::Bearer {
        token: "{{token}}".to_string(),
    };
    request.body = RequestBody::Json {
        text: r#"{"url":"{{baseUrl}}"}"#.to_string(),
    };

    let resolved = resolve_request(&request, &vars()).unwrap();

    assert_eq!(resolved.url, "https://api.example.com/users");
    assert_eq!(resolved.query_params[0].value, "abc123");
    assert_eq!(resolved.headers[0].value, "abc123");
    assert_eq!(
        resolved.auth,
        AuthConfig::Bearer {
            token: "abc123".to_string()
        }
    );
    assert_eq!(
        resolved.body,
        RequestBody::Json {
            text: r#"{"url":"https://api.example.com"}"#.to_string()
        }
    );
}

#[test]
fn an_unresolved_variable_is_a_normalized_error() {
    let request = HttpRequest::get("{{nope}}/users");
    let error = resolve_request(&request, &vars()).unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::UnresolvedVariable);
    assert!(error.message.contains("{{nope}}"));
    assert!(error.detail.unwrap().contains("baseUrl"));
}

#[test]
fn disabled_rows_never_fail_on_their_variables() {
    let mut request = HttpRequest::get("{{baseUrl}}");
    request.headers = vec![KeyValueEntry::disabled("X-Old", "{{retired}}")];

    let resolved = resolve_request(&request, &vars()).unwrap();
    assert_eq!(resolved.headers[0].value, "{{retired}}");
}

#[test]
fn an_environment_contributes_only_its_enabled_rows() {
    let environment = Environment {
        id: "env".into(),
        name: "Local".into(),
        variables: vec![
            KeyValueEntry::new("host", "localhost"),
            KeyValueEntry::disabled("host2", "elsewhere"),
            KeyValueEntry::new("", "nameless"),
        ],
    };

    let set = VariableSet::from(&environment);
    assert_eq!(set.get("host"), Some("localhost"));
    assert_eq!(set.get("host2"), None);
    assert_eq!(set.names().collect::<Vec<_>>(), vec!["host"]);
}
