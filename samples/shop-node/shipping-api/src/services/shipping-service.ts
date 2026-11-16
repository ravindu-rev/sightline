import { formatMoney, type ShipmentQuote } from '@shop/shared';
import { CarrierClient } from '../carriers/carrier-client.js';
import { prisma } from '../db.js';

export interface ShippingService {
  list(): Promise<unknown[]>;
  find(id: string): Promise<unknown | null>;
  create(orderId: number, address: string): Promise<unknown>;
  quote(weightKg: number, country: string): Promise<ShipmentQuote>;
}

export class DefaultShippingService implements ShippingService {
  constructor(private readonly carrier: CarrierClient) {}

  list() {
    return prisma.shipment.findMany();
  }

  find(id: string) {
    return prisma.shipment.findUnique({ where: { id } });
  }

  async create(orderId: number, address: string) {
    const quote = await this.quote(1, 'CA');
    return prisma.shipment.create({ data: { orderId, address, price: quote.price } });
  }

  async quote(weightKg: number, country: string): Promise<ShipmentQuote> {
    const price = await this.carrier.rate(weightKg, country);
    return { price, currency: 'USD', label: formatMoney(price) };
  }
}
