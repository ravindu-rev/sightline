import type { OrderDto, PlaceOrderRequest, Product } from '@shop/shared';

const SHIPPING_URL: string = (import.meta as { env?: Record<string, string> }).env?.VITE_SHIPPING_URL ?? '';

export async function listProducts(): Promise<Product[]> {
  const response = await fetch('/api/Products');
  return response.json();
}

export async function placeOrder(order: PlaceOrderRequest): Promise<OrderDto> {
  const response = await fetch('/api/orders', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(order),
  });
  if (!response.ok) throw new Error(`The order was not placed (${response.status}).`);
  return response.json();
}

export async function trackShipment(id: string) {
  const response = await fetch(`${SHIPPING_URL}/api/shipments/${id}`);
  return response.json();
}
