import { createBillingHandlers } from '../_shared/billing-core.mjs';
const handlers = createBillingHandlers({ env: Deno.env.toObject() });
Deno.serve(handlers.webhook);
