use serde_json::Value;

use crate::StorageError;
use crate::model::AppState;

/// Bump this whenever the on-disk shape changes, and add a step below.
pub const SCHEMA_VERSION: u32 = 1;

/// Brings a parsed state file up to [`SCHEMA_VERSION`].
///
/// Files written before versioning existed report version 0 and are treated as
/// a best-effort read against the current shape; `#[serde(default)]` on
/// [`AppState`] fills in anything that was missing.
pub fn migrate(mut value: Value) -> Result<AppState, StorageError> {
    let mut version = value.get("version").and_then(Value::as_u64).unwrap_or(0) as u32;

    if version > SCHEMA_VERSION {
        return Err(StorageError::Migration(format!(
            "this file was written by a newer version of the app (schema {version}, this build understands {SCHEMA_VERSION})"
        )));
    }

    while version < SCHEMA_VERSION {
        value = step(version, value)?;
        version += 1;
    }

    let mut state: AppState = serde_json::from_value(value)?;
    state.version = SCHEMA_VERSION;
    state.ensure_one_tab();
    Ok(state)
}

/// One migration step, from `from` to `from + 1`.
fn step(from: u32, mut value: Value) -> Result<Value, StorageError> {
    match from {
        // Pre-versioning files: stamp the version and let serde defaults do the rest.
        0 => {
            if let Some(object) = value.as_object_mut() {
                object.insert("version".into(), Value::from(1u32));
            } else {
                return Err(StorageError::Migration(
                    "the state file is not a JSON object".into(),
                ));
            }
            Ok(value)
        }
        other => Err(StorageError::Migration(format!(
            "no migration from schema {other}"
        ))),
    }
}
