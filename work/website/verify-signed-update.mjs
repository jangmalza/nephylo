import assert from 'node:assert/strict';
import { constants, createHash, createPublicKey, verify } from 'node:crypto';

export function verifySignedUpdate(envelopeText, publicXml, installer, release) {
  assert.ok(Buffer.byteLength(envelopeText, 'utf8') <= 16384, 'Signed manifest size must be bounded');
  const envelope = JSON.parse(envelopeText);
  assert.deepEqual(Object.keys(envelope).sort(), ['payloadBase64', 'signatureBase64']);
  assert.equal(typeof envelope.payloadBase64, 'string');
  assert.equal(typeof envelope.signatureBase64, 'string');
  const payloadBytes = Buffer.from(envelope.payloadBase64, 'base64');
  const signature = Buffer.from(envelope.signatureBase64, 'base64');
  assert.equal(payloadBytes.toString('base64'), envelope.payloadBase64, 'Payload Base64 must be canonical');
  assert.equal(signature.toString('base64'), envelope.signatureBase64, 'Signature Base64 must be canonical');
  assert.ok(payloadBytes.length > 0 && payloadBytes.length <= 4096);
  assert.equal(signature.length, 256, 'Release signature must use RSA 2048');
  const parts = publicXml.match(/^\s*<RSAKeyValue>\s*<Modulus>([A-Za-z0-9+/=]+)<\/Modulus>\s*<Exponent>([A-Za-z0-9+/=]+)<\/Exponent>\s*<\/RSAKeyValue>\s*$/);
  assert.ok(parts, 'Release key must contain only the canonical public RSA fields');
  assert.equal(Buffer.from(parts[1], 'base64').length, 256, 'Release public modulus must be RSA 2048');
  const key = createPublicKey({ format: 'jwk', key: { kty: 'RSA', n: Buffer.from(parts[1], 'base64').toString('base64url'), e: Buffer.from(parts[2], 'base64').toString('base64url') } });
  assert.ok(verify('RSA-SHA256', payloadBytes, { key, padding: constants.RSA_PKCS1_PADDING }, signature), 'Signed update metadata signature mismatch');
  const payload = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(payloadBytes));
  assert.deepEqual(Object.keys(payload).sort(), ['bytes', 'fileName', 'schemaVersion', 'sha256', 'version']);
  assert.equal(payload.schemaVersion, 1);
  assert.match(payload.version, /^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$/);
  assert.ok(payload.version.split('.').every(part => Number(part) <= 65535));
  assert.equal(payload.version, release.version, 'Signed metadata version must match legacy release metadata');
  assert.equal(payload.fileName, `Nephylo-Setup-${release.version}.exe`, 'Signed installer must have its canonical immutable filename');
  assert.ok(Number.isSafeInteger(payload.bytes) && payload.bytes >= 64 && payload.bytes <= 100 * 1024 * 1024);
  assert.match(payload.sha256, /^[a-f0-9]{64}$/);
  assert.equal(installer.length, payload.bytes, 'Signed installer size mismatch');
  assert.equal(createHash('sha256').update(installer).digest('hex'), payload.sha256, 'Signed installer digest mismatch');
  assert.equal(payload.bytes, release.installer.bytes, 'Stable and signed installer sizes must match');
  assert.equal(payload.sha256, release.installer.sha256, 'Stable and signed installer digests must match');
  return payload;
}
