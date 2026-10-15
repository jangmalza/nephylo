import { createInternationalBillingHandlers } from '../_shared/international-billing-core.mjs';
Deno.serve(createInternationalBillingHandlers({ env: Deno.env.toObject() }).webhook);
