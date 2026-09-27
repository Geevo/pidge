import { readFileSync } from "node:fs";
import { resolve } from "node:path";

import { expect, it } from "vitest";

import { PROTOCOL_VERSION } from "./protocol";

// The two are written by hand on either side of a process boundary. When they
// drifted, every handshake failed and nothing else noticed.
it("speaks the protocol version the sidecar speaks", () => {
  const rust = readFileSync(resolve(__dirname, "../../../../crates/protocol/src/lib.rs"), "utf8");
  const match = /pub const PROTOCOL_VERSION: u32 = (\d+);/.exec(rust);
  expect(match).not.toBeNull();
  expect(PROTOCOL_VERSION).toBe(Number(match![1]));
});
