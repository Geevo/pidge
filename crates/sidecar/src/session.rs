use std::io;

use api_client_protocol::{
    ClientEnvelope, ClientMessage, PROTOCOL_VERSION, ServerEnvelope, ServerMessage, encode_line,
    version_mismatch,
};
use api_client_session::Session;
use api_client_storage::Store;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::mpsc;

/// Reads protocol lines until stdin closes or a shutdown arrives.
///
/// Every handler below is a direct call into [`Session`]; the interesting logic
/// lives there and is shared with the desktop app.
pub async fn run(store: Store) -> io::Result<()> {
    let (outbound, mut outbound_rx) = mpsc::unbounded_channel::<String>();

    // One writer task, so concurrent requests cannot interleave their lines.
    let writer = tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(line) = outbound_rx.recv().await {
            if stdout.write_all(line.as_bytes()).await.is_err() {
                break;
            }
            if stdout.flush().await.is_err() {
                break;
            }
        }
    });

    let session = match Session::start(store) {
        Ok(session) => session,
        Err(err) => {
            send(
                &outbound,
                ServerEnvelope::new(
                    None,
                    ServerMessage::ProtocolError {
                        message: err.message.clone(),
                    },
                ),
            );
            drop(outbound);
            let _ = writer.await;
            return Err(io::Error::other(err.message));
        }
    };

    let mut handshaken = false;
    let mut lines = BufReader::new(tokio::io::stdin()).lines();

    while let Some(line) = lines.next_line().await? {
        if line.trim().is_empty() {
            continue;
        }

        let envelope: ClientEnvelope = match serde_json::from_str(line.trim()) {
            Ok(envelope) => envelope,
            Err(err) => {
                send(
                    &outbound,
                    ServerEnvelope::new(
                        None,
                        ServerMessage::ProtocolError {
                            message: format!("could not parse message: {err}"),
                        },
                    ),
                );
                continue;
            }
        };

        if let Some(mismatch) = version_mismatch(envelope.v) {
            send(&outbound, ServerEnvelope::new(envelope.id, mismatch));
            break;
        }

        let id = envelope.id.clone();

        if !handshaken && !matches!(envelope.msg, ClientMessage::Handshake { .. }) {
            send(
                &outbound,
                ServerEnvelope::new(
                    id,
                    ServerMessage::ProtocolError {
                        message: "handshake required before any other message".to_string(),
                    },
                ),
            );
            continue;
        }

        match envelope.msg {
            ClientMessage::Handshake {
                client_name,
                client_version,
            } => {
                handshaken = true;
                tracing::info!(client = %client_name, version = %client_version, "handshake");
                send(
                    &outbound,
                    ServerEnvelope::new(
                        id,
                        ServerMessage::HandshakeOk {
                            server_name: "api-client-sidecar".to_string(),
                            server_version: env!("CARGO_PKG_VERSION").to_string(),
                            protocol_version: PROTOCOL_VERSION,
                        },
                    ),
                );
            }

            ClientMessage::SendRequest { request, variables } => {
                let Some(id) = id else {
                    send(
                        &outbound,
                        ServerEnvelope::new(
                            None,
                            ServerMessage::ProtocolError {
                                message: "sendRequest needs a correlation id".to_string(),
                            },
                        ),
                    );
                    continue;
                };

                // Requests run concurrently; the writer task serializes replies.
                let session = session.clone();
                let outbound = outbound.clone();
                tokio::spawn(async move {
                    let outcome = session.send_with_overrides(request, variables).await;
                    let history_entry = outcome.history_entry;
                    let msg = match (outcome.response, outcome.error) {
                        (Some(response), _) => ServerMessage::RequestComplete {
                            response,
                            history_entry,
                        },
                        (None, Some(error)) => ServerMessage::RequestError {
                            error,
                            history_entry,
                        },
                        (None, None) => ServerMessage::ProtocolError {
                            message: "the request produced no result".to_string(),
                        },
                    };
                    send(&outbound, ServerEnvelope::new(Some(id), msg));
                });
            }

            ClientMessage::CancelRequest { request_id } => {
                let was_in_flight = session.cancel(&request_id);
                send(
                    &outbound,
                    ServerEnvelope::new(
                        Some(request_id),
                        ServerMessage::RequestCancelled { was_in_flight },
                    ),
                );
            }

            ClientMessage::LoadState => {
                send(
                    &outbound,
                    ServerEnvelope::new(
                        id,
                        ServerMessage::StateLoaded {
                            state: session.snapshot(),
                            recovery: session.recovery().map(|r| r.message.clone()),
                            storage_path: session.storage_path(),
                        },
                    ),
                );
            }

            ClientMessage::SaveState { state } => {
                let msg = match session.replace_state(state) {
                    Ok(()) => ServerMessage::StateSaved {
                        state: session.snapshot(),
                    },
                    Err(err) => ServerMessage::StorageError {
                        message: err.to_string(),
                    },
                };
                send(&outbound, ServerEnvelope::new(id, msg));
            }

            ClientMessage::SaveRequest {
                saved_request_id,
                name,
                request,
            } => {
                let msg = match session.save_request(saved_request_id, name, request) {
                    Ok(_) => ServerMessage::StateSaved {
                        state: session.snapshot(),
                    },
                    Err(err) => ServerMessage::StorageError {
                        message: err.to_string(),
                    },
                };
                send(&outbound, ServerEnvelope::new(id, msg));
            }

            ClientMessage::DeleteSavedRequest { saved_request_id } => {
                let msg = match session.delete_saved_request(&saved_request_id) {
                    Ok(_) => ServerMessage::StateSaved {
                        state: session.snapshot(),
                    },
                    Err(err) => ServerMessage::StorageError {
                        message: err.to_string(),
                    },
                };
                send(&outbound, ServerEnvelope::new(id, msg));
            }

            ClientMessage::ClearHistory => {
                let msg = match session.clear_history() {
                    Ok(()) => ServerMessage::StateSaved {
                        state: session.snapshot(),
                    },
                    Err(err) => ServerMessage::StorageError {
                        message: err.to_string(),
                    },
                };
                send(&outbound, ServerEnvelope::new(id, msg));
            }

            ClientMessage::Shutdown => break,
        }
    }

    drop(outbound);
    let _ = writer.await;
    Ok(())
}

fn send(outbound: &mpsc::UnboundedSender<String>, envelope: ServerEnvelope) {
    match encode_line(&envelope) {
        Ok(line) => {
            let _ = outbound.send(line);
        }
        Err(err) => tracing::error!(error = %err, "could not encode an outgoing message"),
    }
}
