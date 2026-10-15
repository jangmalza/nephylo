import assert from 'node:assert/strict';
import { constants, createHash, generateKeyPairSync, sign } from 'node:crypto';
import { verifySignedUpdate } from './verify-signed-update.mjs';

const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = publicKey.export({ format: 'jwk' });
const xml = `<RSAKeyValue><Modulus>${Buffer.from(jwk.n, 'base64url').toString('base64')}</Modulus><Exponent>${Buffer.from(jwk.e, 'base64url').toString('base64')}</Exponent></RSAKeyValue>`;
const installer = Buffer.alloc(128, 0); installer.write('MZ');
const digest = createHash('sha256').update(installer).digest('hex');
const release = { version: '1.21.0', installer: { bytes: installer.length, sha256: digest } };
const payload = { schemaVersion: 1, version: '1.21.0', fileName: 'Nephylo-Setup-1.21.0.exe', bytes: installer.length, sha256: digest };
function envelope(data, padding = constants.RSA_PKCS1_PADDING) {
  const bytes = Buffer.from(JSON.stringify(data));
  return JSON.stringify({ payloadBase64: bytes.toString('base64'), signatureBase64: sign('RSA-SHA256', bytes, { key: privateKey, padding }).toString('base64') });
}
let checks = 0;
function reject(operation) { assert.throws(operation); checks++; }
assert.deepEqual(verifySignedUpdate(envelope(payload), xml, installer, release), payload); checks++;
const changed = Buffer.from(installer); changed[127] = 1;
reject(() => verifySignedUpdate(envelope(payload), xml, changed, release));
reject(() => verifySignedUpdate(envelope(payload), xml, installer.subarray(0, 127), release));
const alteredPayload = JSON.parse(envelope(payload)); alteredPayload.payloadBase64 = Buffer.from(JSON.stringify({ ...payload, version: '1.21.1' })).toString('base64');
reject(() => verifySignedUpdate(JSON.stringify(alteredPayload), xml, installer, release));
const alteredSignature = JSON.parse(envelope(payload)); const sig = Buffer.from(alteredSignature.signatureBase64, 'base64'); sig[0] ^= 1; alteredSignature.signatureBase64 = sig.toString('base64');
reject(() => verifySignedUpdate(JSON.stringify(alteredSignature), xml, installer, release));
reject(() => verifySignedUpdate(envelope(payload, constants.RSA_PKCS1_PSS_PADDING), xml, installer, release));
reject(() => verifySignedUpdate(envelope({ ...payload, fileName: '../Nephylo-Setup.exe' }), xml, installer, release));
reject(() => verifySignedUpdate(envelope({ ...payload, version: '01.21.0' }), xml, installer, release));
reject(() => verifySignedUpdate(envelope({ ...payload, extra: true }), xml, installer, release));
reject(() => verifySignedUpdate(envelope(payload), xml.replace('</RSAKeyValue>', '<D>private</D></RSAKeyValue>'), installer, release));
reject(() => verifySignedUpdate(envelope(payload), xml, installer, { ...release, version: '1.21.1' }));
reject(() => verifySignedUpdate(envelope(payload), xml, installer, { ...release, installer: { ...release.installer, sha256: '0'.repeat(64) } }));
console.log(`PASS: ${checks} signed metadata checks (payload/signature/artifact tampering, schema, version and legacy installer agreement).`);
