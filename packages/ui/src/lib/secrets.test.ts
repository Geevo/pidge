import { expect, it } from "vitest";

import redact from "../../../../src/Pidge.Core/Redact.cs?raw";
import { SECRET_HEADERS, isSecretHeader, looksSecret } from "./secrets";

it("masks the same headers the host keeps secret", () => {
  const list = /string\[\] SecretHeaders =\s*\[([\s\S]*?)\];/.exec(redact);
  expect(list).not.toBeNull();
  const names = [...list![1]!.matchAll(/"([^"]+)"/g)].map((match) => match[1]);
  expect([...SECRET_HEADERS]).toEqual(names);
});

it("recognises a secret header whatever its case", () => {
  expect(isSecretHeader(" Authorization ")).toBe(true);
  expect(isSecretHeader("X-API-Key")).toBe(true);
  expect(isSecretHeader("Accept")).toBe(false);
});

it("guesses at variables by name", () => {
  for (const name of ["token", "apiKey", "DB_PASSWORD", "clientSecret", "session_id"]) {
    expect(looksSecret(name)).toBe(true);
  }
  for (const name of ["baseUrl", "host", "userId", "version"]) {
    expect(looksSecret(name)).toBe(false);
  }
});
