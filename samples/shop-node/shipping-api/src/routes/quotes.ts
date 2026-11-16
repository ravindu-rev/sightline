import type { Router } from 'express';
import { shippingService } from '../services/container.js';

/** Adds the quote routes to a router; the app decides where it is mounted. */
export function registerQuoteRoutes(router: Router) {
  router.post('/quotes', async (req, res) => {
    const quote = await shippingService.quote(Number(req.body.weightKg), String(req.body.country));
    res.json(quote);
  });
}
