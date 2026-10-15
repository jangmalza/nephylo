// Deployment entry points use this handler instead of the archived billing
// implementation. Even old enabled secrets cannot create or renew a charge.
export function createRetiredBillingHandler(env = {}) {
  let site = 'https://nephylo.com';
  try {
    const value = new URL(env.SITE_URL || site);
    if (value.protocol === 'https:' && !value.username && !value.password) site = value.origin;
  } catch { /* Keep the official origin. */ }
  return async function retiredBilling(request) {
    const origin = request.headers.get('origin');
    const headers = { 'content-type': 'application/json', 'cache-control': 'no-store', 'vary': 'Origin' };
    if (origin && origin !== site) return new Response(JSON.stringify({ error: 'FORBIDDEN' }), { status: 403, headers });
    if (origin === site) {
      headers['access-control-allow-origin'] = site;
      headers['access-control-allow-headers'] = 'authorization, apikey, content-type, x-client-info';
      headers['access-control-allow-methods'] = 'POST, OPTIONS';
    }
    if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers });
    return new Response(JSON.stringify({ enabled: false, free: true, error: 'BILLING_RETIRED',
      message: 'Nephylo is free. No Nephylo account or payment is required.' }), { status: 410, headers });
  };
}
