import { Injectable } from '@nestjs/common';
import type { OrderDto } from '@shop/shared';

/** Reads orders from the C# Shop API. */
@Injectable()
export class OrdersClient {
  private readonly baseUrl = process.env.SHOP_API_URL ?? 'http://localhost:5000';

  async find(id: number): Promise<OrderDto> {
    const response = await fetch(`${this.baseUrl}/api/orders/${id}`);
    return (await response.json()) as OrderDto;
  }
}
