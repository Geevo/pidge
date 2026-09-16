//! Serializes `Vec<u8>` as base64 so response bodies survive JSON transport
//! without exploding into an array of numbers.

use base64::prelude::{BASE64_STANDARD, Engine as _};
use serde::{Deserialize, Deserializer, Serializer};

pub fn serialize<S: Serializer>(bytes: &[u8], serializer: S) -> Result<S::Ok, S::Error> {
    serializer.serialize_str(&BASE64_STANDARD.encode(bytes))
}

pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<u8>, D::Error> {
    let encoded = String::deserialize(deserializer)?;
    BASE64_STANDARD
        .decode(encoded.as_bytes())
        .map_err(serde::de::Error::custom)
}
