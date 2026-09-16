/**
 * Response bodies arrive base64 encoded, because JSON has no byte array.
 */
export function decodeBase64(value: string): Uint8Array {
  if (value === "") return new Uint8Array();
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

/**
 * Decodes as UTF-8, replacing anything invalid. Use `looksBinary` first if you
 * need to know whether that is a reasonable thing to do.
 */
export function decodeText(bytes: Uint8Array): string {
  return new TextDecoder("utf-8", { fatal: false }).decode(bytes);
}

/**
 * A NUL byte, or a lot of other control bytes, means we should not pretend
 * this is text. Checking a prefix is enough and keeps large bodies cheap.
 */
export function looksBinary(bytes: Uint8Array): boolean {
  const sample = bytes.subarray(0, 1024);
  if (sample.length === 0) return false;

  let suspicious = 0;
  for (const byte of sample) {
    if (byte === 0) return true;
    const isControl = byte < 0x09 || (byte > 0x0d && byte < 0x20);
    if (isControl) suspicious += 1;
  }
  return suspicious / sample.length > 0.1;
}
