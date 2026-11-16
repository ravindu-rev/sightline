/** What the C# Shop API takes and returns (Shop.Application/Orders). */
export interface PlaceOrderLine {
  productId: number;
  quantity: number;
}

export interface PlaceOrderRequest {
  customerEmail: string;
  lines: PlaceOrderLine[];
}

export interface OrderLine {
  productId: number;
  quantity: number;
  unitPrice: number;
}

export interface OrderDto {
  id: number;
  customerEmail: string;
  status: string;
  total: number;
  lines: OrderLine[];
}

export interface Product {
  id: number;
  name: string;
  price: number;
  stock: number;
}

export interface ShipmentQuote {
  price: number;
  currency: string;
  label: string;
}
