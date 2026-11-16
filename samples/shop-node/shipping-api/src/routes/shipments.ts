import { Router } from 'express';
import { requireAuth } from '../middleware/auth.js';
import { shippingService } from '../services/container.js';

export const shipmentsRouter = Router();

shipmentsRouter.get('/', async (_req, res) => {
  res.json(await shippingService.list());
});

shipmentsRouter.get('/:id', async (req, res) => {
  const shipment = await shippingService.find(req.params.id);
  if (!shipment) return res.sendStatus(404);
  res.json(shipment);
});

shipmentsRouter.post('/', requireAuth, async (req, res) => {
  const { orderId, address } = req.body;
  const shipment = await shippingService.create(Number(orderId), String(address));
  res.status(201).json(shipment);
});
