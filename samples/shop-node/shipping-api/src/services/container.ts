import { CarrierClient } from '../carriers/carrier-client.js';
import { DefaultShippingService, type ShippingService } from './shipping-service.js';

export const shippingService: ShippingService = new DefaultShippingService(
  new CarrierClient(process.env.CARRIER_URL ?? 'https://api.carrier.example'),
);
