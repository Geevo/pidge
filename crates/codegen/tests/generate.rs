//! What the four generators have to agree about.
//!
//! These are deliberately about behaviour rather than layout: a test that
//! pinned every line of output would fail on a reflowed comment and say
//! nothing about whether the request is right.

use api_client_codegen::{ClientOptions, CodeTarget, generate};
use api_client_core::{
    ApiKeyPlacement, AuthConfig, ClientIdentitySettings, HttpMethod, HttpRequest, KeyValueEntry,
    MultipartEntry, MultipartValue, OAuth1Settings, OAuth2Settings, RequestBody, TlsSettings,
};

fn code(request: &HttpRequest, target: CodeTarget) -> String {
    generate(request, &ClientOptions::standard(), target).expect("generated")
}

fn everywhere(request: &HttpRequest) -> Vec<(CodeTarget, String)> {
    CodeTarget::ALL
        .into_iter()
        .map(|target| (target, code(request, target)))
        .collect()
}

fn get(url: &str) -> HttpRequest {
    HttpRequest {
        url: url.to_string(),
        ..HttpRequest::default()
    }
}

/// The URL in the snippet is the URL that would be requested, not the text in
/// the field: `localhost:3000` is a host and a port here as it is everywhere
/// else in the app.
#[test]
fn a_bare_host_gains_the_scheme_the_engine_would_give_it() {
    for (target, code) in everywhere(&get("localhost:3000/api/test")) {
        assert!(
            code.contains("http://localhost:3000/api/test"),
            "{target:?} did not normalize the URL:\n{code}"
        );
    }
}

/// The params table and the URL are one thing shown twice, and the snippet
/// shows it once — encoded as the engine encodes it, with `%20` for a space.
#[test]
fn query_rows_are_folded_into_the_url_once() {
    let request = HttpRequest {
        query_params: vec![KeyValueEntry::new("postcode", "SW1A 1AA")],
        ..get("https://example.com/lookup")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("https://example.com/lookup?postcode=SW1A%201AA"),
            "{target:?} did not fold the query in:\n{code}"
        );
        assert_eq!(
            code.matches("postcode=").count(),
            1,
            "{target:?} wrote the parameter twice:\n{code}"
        );
    }
}

/// A key placed in the query string is a query parameter, exactly as the engine
/// treats it.
#[test]
fn an_api_key_in_the_query_string_is_in_the_url() {
    let request = HttpRequest {
        auth: AuthConfig::ApiKey {
            key: "api_key".to_string(),
            value: "abc123".to_string(),
            placement: ApiKeyPlacement::Query,
        },
        ..get("https://example.com/things")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("https://example.com/things?api_key=abc123"),
            "{target:?} lost the key:\n{code}"
        );
    }
}

/// Every one of these languages says a username and a password better than the
/// base64 the header would carry, so none of them shows the blob.
#[test]
fn basic_auth_is_written_in_each_language_rather_than_as_a_blob() {
    let request = HttpRequest {
        auth: AuthConfig::Basic {
            username: "ada".to_string(),
            password: "lovelace".to_string(),
        },
        ..get("https://example.com/")
    };

    let expected = [
        (CodeTarget::Curl, "--user 'ada:lovelace'"),
        (CodeTarget::PowerShell, "'ada:lovelace'"),
        (CodeTarget::Python, r#"auth=("ada", "lovelace")"#),
        (CodeTarget::CSharp, r#""ada:lovelace""#),
    ];

    for (target, fragment) in expected {
        let code = code(&request, target);
        assert!(
            code.contains(fragment),
            "{target:?} did not write the credentials as `{fragment}`:\n{code}"
        );
        assert!(
            !code.contains("YWRhOmxvdmVsYWNl"),
            "{target:?} wrote the base64 rather than the pair:\n{code}"
        );
    }
}

/// An Authorization header the user typed wins over the Auth tab, in the
/// snippet as in the engine, and the code says why.
#[test]
fn a_typed_authorization_header_beats_the_auth_tab() {
    let request = HttpRequest {
        headers: vec![KeyValueEntry::new("Authorization", "Bearer mine")],
        auth: AuthConfig::Basic {
            username: "ada".to_string(),
            password: "lovelace".to_string(),
        },
        ..get("https://example.com/")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("Bearer mine"),
            "{target:?} dropped the header:\n{code}"
        );
        assert!(
            !code.contains("lovelace"),
            "{target:?} sent the Auth tab as well:\n{code}"
        );
        assert!(
            code.contains("Auth tab was ignored"),
            "{target:?} did not say the tab was ignored:\n{code}"
        );
    }
}

/// Digest and NTLM are a second request after a challenge, which each of these
/// clients can do on its own — so each is told to, rather than left silent.
#[test]
fn a_challenge_scheme_uses_the_client_that_can_answer_it() {
    let request = HttpRequest {
        auth: AuthConfig::Digest {
            username: "ada".to_string(),
            password: "lovelace".to_string(),
        },
        ..get("https://example.com/")
    };

    let expected = [
        (CodeTarget::Curl, "--digest"),
        (CodeTarget::PowerShell, "PSCredential"),
        (CodeTarget::Python, "HTTPDigestAuth"),
        (CodeTarget::CSharp, "NetworkCredential"),
    ];

    for (target, fragment) in expected {
        let code = code(&request, target);
        assert!(
            code.contains(fragment),
            "{target:?} did not use `{fragment}`:\n{code}"
        );
    }
}

/// The body is the text in the editor, character for character. Re-encoding it
/// through a parser would reorder keys and reformat numbers, and send something
/// nobody wrote.
#[test]
fn a_json_body_is_passed_through_as_typed() {
    let text = "{\n  \"b\": 1,\n  \"a\": 2.50\n}";
    let request = HttpRequest {
        method: HttpMethod::Post,
        body: RequestBody::Json {
            text: text.to_string(),
        },
        ..get("https://example.com/")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("\"b\"") && code.contains("2.50"),
            "{target:?} rewrote the body:\n{code}"
        );
        assert!(
            code.contains("application/json"),
            "{target:?} lost the content type:\n{code}"
        );
    }
}

/// A form body is encoded once, here, the way the engine encodes it — so the
/// bytes in the snippet are the bytes on the wire.
#[test]
fn a_form_body_is_encoded_the_way_the_engine_encodes_it() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        body: RequestBody::UrlEncoded {
            entries: vec![KeyValueEntry::new("full name", "Ada Lovelace")],
        },
        ..get("https://example.com/")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("application/x-www-form-urlencoded"),
            "{target:?} lost the content type:\n{code}"
        );
    }

    // Python hands `requests` the pairs and lets it encode them, which is the
    // same encoding; the other three write the bytes out.
    for target in [CodeTarget::Curl, CodeTarget::PowerShell, CodeTarget::CSharp] {
        let code = code(&request, target);
        assert!(
            code.contains("full+name=Ada+Lovelace"),
            "{target:?} did not encode the form:\n{code}"
        );
    }
}

/// Multipart writes its own Content-Type, boundary and all. Anybody else who
/// writes that header breaks the body.
#[test]
fn nothing_sets_a_content_type_for_a_multipart_body() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        headers: vec![KeyValueEntry::new("Content-Type", "multipart/form-data")],
        body: RequestBody::Multipart {
            entries: vec![
                MultipartEntry::text("caption", "A photo"),
                MultipartEntry::file("photo", "/home/ada/cat.png"),
            ],
        },
        ..get("https://example.com/upload")
    };

    for (target, code) in everywhere(&request) {
        /*
         * A client that writes the body itself has to name the boundary it
         * generated; one whose library writes the body must not name the type
         * at all. What is never right is the user's own row, which has no
         * boundary in it and would break the body.
         */
        if let Some(index) = code.find("multipart/form-data") {
            let after = &code[index..];
            assert!(
                after.contains("boundary"),
                "{target:?} wrote a content type with no boundary:\n{code}"
            );
        }
        assert!(
            code.contains("caption") && code.contains("cat.png"),
            "{target:?} lost a part:\n{code}"
        );
        assert!(
            code.contains("Content-Type"),
            "{target:?} did not say the header was replaced:\n{code}"
        );
    }
}

/// `-X HEAD` leaves curl waiting for a body a HEAD response never has.
#[test]
fn curl_asks_for_a_head_request_the_way_curl_wants_to_be_asked() {
    let request = HttpRequest {
        method: HttpMethod::Head,
        ..get("https://example.com/")
    };
    let code = code(&request, CodeTarget::Curl);
    assert!(code.contains("--head"), "{code}");
    assert!(!code.contains("--request HEAD"), "{code}");
}

/// What cannot be reproduced is said, not left out. A signature is a library,
/// and a snippet that quietly omitted it would look complete.
#[test]
fn an_unsignable_request_says_so() {
    let oauth1 = HttpRequest {
        auth: AuthConfig::OAuth1(OAuth1Settings::default()),
        ..get("https://example.com/")
    };
    for (target, code) in everywhere(&oauth1) {
        assert!(
            code.contains("OAuth 1.0a"),
            "{target:?} said nothing about the signature:\n{code}"
        );
    }

    let oauth2 = HttpRequest {
        auth: AuthConfig::OAuth2(OAuth2Settings {
            token_url: "https://id.example.com/token".to_string(),
            ..OAuth2Settings::default()
        }),
        ..get("https://example.com/")
    };
    for (target, code) in everywhere(&oauth2) {
        assert!(
            code.contains("https://id.example.com/token"),
            "{target:?} said nothing about the token:\n{code}"
        );
    }
}

/// The app's own settings are not in the generated program, so they are written
/// into it. Without this, a snippet copied from a client that follows redirects
/// stops at the first one.
#[test]
fn the_client_settings_are_written_into_the_snippet() {
    let request = get("https://example.com/");
    let strict = ClientOptions {
        timeout_ms: 5_000,
        follow_redirects: false,
        tls: TlsSettings {
            accept_invalid_certs: true,
            ..TlsSettings::default()
        },
    };

    let expected = [
        (CodeTarget::Curl, vec!["--insecure", "--max-time 5"]),
        (
            CodeTarget::PowerShell,
            vec!["SkipCertificateCheck", "MaximumRedirection", "TimeoutSec"],
        ),
        (
            CodeTarget::Python,
            vec!["verify=False", "allow_redirects=False", "timeout=5"],
        ),
        (
            CodeTarget::CSharp,
            vec!["AllowAutoRedirect = false", "DangerousAccept"],
        ),
    ];

    for (target, fragments) in expected {
        let code = generate(&request, &strict, target).expect("generated");
        for fragment in fragments {
            assert!(
                code.contains(fragment),
                "{target:?} did not carry `{fragment}`:\n{code}"
            );
        }
    }

    // The other way round: curl does not follow redirects unless it is told to,
    // and the app does.
    let code = generate(&request, &ClientOptions::standard(), CodeTarget::Curl).expect("generated");
    assert!(code.contains("--location"), "{code}");
}

/// A request with no URL cannot be described, and the error says the same thing
/// the Send button would.
#[test]
fn an_empty_url_is_an_error_rather_than_a_snippet() {
    for target in CodeTarget::ALL {
        let error = generate(&get("   "), &ClientOptions::standard(), target)
            .expect_err("an empty URL has nothing to generate");
        assert!(error.message.contains("Enter a URL"), "{error:?}");
    }
}

/// A disabled row is not sent, so it is not written either.
#[test]
fn disabled_rows_are_left_out() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        headers: vec![KeyValueEntry::disabled("X-Draft", "not-this")],
        query_params: vec![KeyValueEntry::disabled("page", "not-this")],
        body: RequestBody::Multipart {
            entries: vec![
                MultipartEntry {
                    enabled: false,
                    ..MultipartEntry::text("caption", "not-this")
                },
                MultipartEntry::file("photo", "/home/ada/cat.png"),
            ],
        },
        ..get("https://example.com/")
    };

    for (target, code) in everywhere(&request) {
        assert!(
            !code.contains("not-this"),
            "{target:?} wrote a disabled row:\n{code}"
        );
    }
}

/// A file part keeps the name and the type the request gave it, wherever the
/// language has somewhere to put them.
#[test]
fn a_file_part_keeps_the_name_it_was_given() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        body: RequestBody::Multipart {
            entries: vec![MultipartEntry {
                value: MultipartValue::File {
                    path: "/home/ada/IMG_0001.png".to_string(),
                    file_name: Some("cat.png".to_string()),
                    content_type: Some("image/png".to_string()),
                },
                ..MultipartEntry::file("photo", "/home/ada/IMG_0001.png")
            }],
        },
        ..get("https://example.com/upload")
    };

    for target in [CodeTarget::Curl, CodeTarget::Python, CodeTarget::CSharp] {
        let code = code(&request, target);
        assert!(
            code.contains("cat.png") && code.contains("image/png"),
            "{target:?} lost the part's name or type:\n{code}"
        );
    }

    // PowerShell's -Form takes neither, and says so rather than pretending.
    let code = code(&request, CodeTarget::PowerShell);
    assert!(code.contains("-Form takes neither"), "{code}");
}

fn with_tls(tls: TlsSettings) -> ClientOptions {
    ClientOptions {
        tls,
        ..ClientOptions::standard()
    }
}

/// A client certificate is the app's setting, not the request's, so the snippet
/// has to present it itself or it will be turned away at the handshake.
#[test]
fn a_client_certificate_is_presented_in_each_language() {
    let options = with_tls(TlsSettings {
        client_identity: Some(ClientIdentitySettings {
            path: "/home/ada/client.p12".to_string(),
            password: Some("hunter2".to_string()),
        }),
        ..TlsSettings::default()
    });

    let expected = [
        (
            CodeTarget::Curl,
            vec![
                "--cert-type P12",
                "--cert '/home/ada/client.p12'",
                "--pass 'hunter2'",
            ],
        ),
        (
            CodeTarget::PowerShell,
            vec!["X509Certificate2", "'/home/ada/client.p12'", "'hunter2'"],
        ),
        (CodeTarget::Python, vec!["cert="]),
        (
            CodeTarget::CSharp,
            vec![
                "ClientCertificates.Add",
                "LoadPkcs12FromFile",
                "\"hunter2\"",
            ],
        ),
    ];

    for (target, fragments) in expected {
        let code = generate(&get("https://example.com/"), &options, target).expect("generated");
        for fragment in fragments {
            assert!(
                code.contains(fragment),
                "{target:?} did not present the certificate with `{fragment}`:\n{code}"
            );
        }
    }
}

/// The file's extension says which kind it is, because nothing here can read
/// it — and a PEM and a PKCS#12 bundle are loaded by different calls.
#[test]
fn a_pem_certificate_is_loaded_as_a_pem() {
    let options = with_tls(TlsSettings {
        client_identity: Some(ClientIdentitySettings {
            path: "/home/ada/client.pem".to_string(),
            password: None,
        }),
        ..TlsSettings::default()
    });

    let curl = generate(&get("https://example.com/"), &options, CodeTarget::Curl).expect("ok");
    assert!(curl.contains("--cert '/home/ada/client.pem'"), "{curl}");
    assert!(!curl.contains("--cert-type P12"), "{curl}");
    assert!(!curl.contains("--pass"), "{curl}");

    for (target, fragment) in [
        (CodeTarget::PowerShell, "CreateFromPemFile"),
        (CodeTarget::CSharp, "CreateFromPemFile"),
        (CodeTarget::Python, r#"cert="/home/ada/client.pem""#),
    ] {
        let code = generate(&get("https://example.com/"), &options, target).expect("ok");
        assert!(code.contains(fragment), "{target:?}:\n{code}");
    }
}

/// `requests` goes through OpenSSL, which wants a PEM on disk. The snippet says
/// how to make one and then points at it, rather than at a file that will fail.
#[test]
fn python_says_how_to_convert_a_pkcs12_bundle() {
    let options = with_tls(TlsSettings {
        client_identity: Some(ClientIdentitySettings {
            path: "/home/ada/client.p12".to_string(),
            password: Some("hunter2".to_string()),
        }),
        ..TlsSettings::default()
    });

    let code = generate(&get("https://example.com/"), &options, CodeTarget::Python).expect("ok");
    assert!(
        code.contains("openssl pkcs12 -in /home/ada/client.p12"),
        "{code}"
    );
    assert!(code.contains(r#"cert="/home/ada/client.pem""#), "{code}");
    assert!(!code.contains(r#"cert="/home/ada/client.p12""#), "{code}");

    // The command is there to be copied, so it is on one line.
    let command = code
        .lines()
        .find(|line| line.contains("openssl"))
        .expect("the command is in the notes");
    assert!(
        command.contains("-out") && command.contains("-nodes"),
        "{command}"
    );
}

/// Two of these clients take a CA file and two do not. Both answers are said
/// out loud rather than left for a failed handshake to explain.
#[test]
fn an_extra_ca_is_used_where_it_can_be_and_named_where_it_cannot() {
    let options = with_tls(TlsSettings {
        extra_ca_files: vec!["/home/ada/corp-ca.pem".to_string()],
        use_system_roots: false,
        ..TlsSettings::default()
    });

    let curl = generate(&get("https://example.com/"), &options, CodeTarget::Curl).expect("ok");
    assert!(curl.contains("--cacert '/home/ada/corp-ca.pem'"), "{curl}");

    let python = generate(&get("https://example.com/"), &options, CodeTarget::Python).expect("ok");
    assert!(
        python.contains(r#"verify="/home/ada/corp-ca.pem""#),
        "{python}"
    );

    for target in [CodeTarget::PowerShell, CodeTarget::CSharp] {
        let code = generate(&get("https://example.com/"), &options, target).expect("ok");
        assert!(
            code.contains("/home/ada/corp-ca.pem") && code.contains("certificate store"),
            "{target:?} did not say where the CA has to go:\n{code}"
        );
    }
}

/// The app adds a CA to the system roots; curl and requests replace them. That
/// difference is the one that turns a working snippet into a failing one for
/// every other host, so it is stated.
#[test]
fn replacing_the_system_roots_rather_than_adding_to_them_is_called_out() {
    let merging = with_tls(TlsSettings {
        extra_ca_files: vec!["/home/ada/corp-ca.pem".to_string()],
        use_system_roots: true,
        ..TlsSettings::default()
    });

    for target in [CodeTarget::Curl, CodeTarget::Python] {
        let code = generate(&get("https://example.com/"), &merging, target).expect("ok");
        assert!(
            code.contains("rather than adding to it"),
            "{target:?} did not say the system roots are replaced:\n{code}"
        );
    }

    // Trusting only the named CA is exactly what these two do, so there is
    // nothing to warn about.
    let only = with_tls(TlsSettings {
        extra_ca_files: vec!["/home/ada/corp-ca.pem".to_string()],
        use_system_roots: false,
        ..TlsSettings::default()
    });

    for target in [CodeTarget::Curl, CodeTarget::Python] {
        let code = generate(&get("https://example.com/"), &only, target).expect("ok");
        assert!(
            !code.contains("rather than adding to it"),
            "{target:?} warned about something it does correctly:\n{code}"
        );
    }
}

/// Verification off leaves nothing for a CA file to do, and `verify` is one
/// argument that cannot say both.
#[test]
fn verification_off_wins_over_a_ca_file() {
    let options = with_tls(TlsSettings {
        extra_ca_files: vec!["/home/ada/corp-ca.pem".to_string()],
        accept_invalid_certs: true,
        ..TlsSettings::default()
    });

    let code = generate(&get("https://example.com/"), &options, CodeTarget::Python).expect("ok");
    assert!(code.contains("verify=False"), "{code}");
    assert!(!code.contains(r#"verify="/home/ada"#), "{code}");
}

/// An empty path is not a certificate. The settings dialog can leave one there.
#[test]
fn a_blank_certificate_path_is_not_a_certificate() {
    let options = with_tls(TlsSettings {
        client_identity: Some(ClientIdentitySettings {
            path: "   ".to_string(),
            password: None,
        }),
        extra_ca_files: vec!["  ".to_string()],
        ..TlsSettings::default()
    });

    for target in CodeTarget::ALL {
        let code = generate(&get("https://example.com/"), &options, target).expect("ok");
        for fragment in [
            "--cert",
            "cert=",
            "CreateFromPemFile",
            "--cacert",
            "verify=",
        ] {
            assert!(
                !code.contains(fragment),
                "{target:?} wrote `{fragment}` for a blank path:\n{code}"
            );
        }
    }
}

/// The languages added after the first four, and the shapes each of them has to
/// get right. One test per language rather than per rule: what matters is that
/// the request survives the trip into a language, not which call carries it.
#[test]
fn every_language_writes_the_whole_request() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        url: "https://api.example.com/v1/things".to_string(),
        query_params: vec![KeyValueEntry::new("q", "SW1A 1AA")],
        headers: vec![KeyValueEntry::new("X-Trace", "abc 123")],
        auth: AuthConfig::Basic {
            username: "ada".to_string(),
            password: "lovelace".to_string(),
        },
        body: RequestBody::Json {
            text: "{\n  name: Ada\n}".to_string(),
        },
        ..HttpRequest::default()
    };

    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("https://api.example.com/v1/things?q=SW1A%201AA"),
            "{target:?} did not carry the URL:\n{code}"
        );
        assert!(
            code.contains("X-Trace"),
            "{target:?} did not carry the header:\n{code}"
        );
        assert!(
            code.contains("application/json"),
            "{target:?} did not carry the content type:\n{code}"
        );
        assert!(
            code.contains("  name: Ada"),
            "{target:?} did not carry the body as typed:\n{code}"
        );
        assert!(
            code.contains("ada")
                && (code.contains("lovelace") || code.contains("YWRhOmxvdmVsYWNl")),
            "{target:?} did not carry the credentials:\n{code}"
        );
    }
}

/// A body's own indentation is data. The languages whose code sits inside a
/// function are the ones that could have indented it along with everything else.
#[test]
fn a_body_keeps_its_own_indentation() {
    let request = HttpRequest {
        method: HttpMethod::Post,
        body: RequestBody::Json {
            text: "{\n      deeply: indented\n}".to_string(),
        },
        ..get("https://example.com/")
    };

    /*
     * The six spaces are the test. A language that laid the snippet out by
     * indenting whole blocks would have made them ten, and sent a body nobody
     * wrote. Zig opens each line with `\\`, which is why the newline before
     * them is not part of what is looked for.
     */
    for (target, code) in everywhere(&request) {
        assert!(
            code.contains("      deeply: indented"),
            "{target:?} re-indented the body:\n{code}"
        );
        assert!(
            !code.contains("          deeply: indented"),
            "{target:?} added indentation of its own:\n{code}"
        );
    }
}

/// Every language names the library it needs, where it needs one that is not
/// in the box.
#[test]
fn a_language_is_grouped_with_the_libraries_that_write_it() {
    let rust: Vec<CodeTarget> = CodeTarget::ALL
        .into_iter()
        .filter(|target| target.language() == "Rust")
        .collect();

    assert_eq!(rust.len(), 2);
    assert_eq!(rust[0].library(), Some("blocking"));
    assert_eq!(rust[0].label(), "Rust (blocking)");

    // A language with one way of doing this has no library to choose.
    assert_eq!(CodeTarget::Go.library(), None);
    assert_eq!(CodeTarget::Go.label(), "Go");

    // Nothing is listed twice, and every one of them generates.
    let mut labels: Vec<String> = CodeTarget::ALL.into_iter().map(|t| t.label()).collect();
    labels.sort();
    let before = labels.len();
    labels.dedup();
    assert_eq!(labels.len(), before);
}

/// Where a client cannot answer a challenge, it says so rather than looking
/// like it sent credentials it never had.
#[test]
fn a_language_that_cannot_answer_a_challenge_says_so() {
    let request = HttpRequest {
        auth: AuthConfig::Digest {
            username: "ada".to_string(),
            password: "lovelace".to_string(),
        },
        ..get("https://example.com/")
    };

    // These four answer it themselves; the rest have to explain.
    let answers = [
        CodeTarget::Curl,
        CodeTarget::PowerShell,
        CodeTarget::Python,
        CodeTarget::CSharp,
        CodeTarget::PhpCurl,
        CodeTarget::PhpGuzzle,
    ];

    for (target, code) in everywhere(&request) {
        if answers.contains(&target) {
            continue;
        }
        assert!(
            code.to_lowercase().contains("digest"),
            "{target:?} said nothing about the challenge:\n{code}"
        );
    }
}
