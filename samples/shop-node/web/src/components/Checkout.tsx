import { useState } from 'react';
import { placeOrder, trackShipment } from '../api/client';

export function Checkout() {
  const [status, setStatus] = useState('');

  const submit = async () => {
    const order = await placeOrder({ customerEmail: 'ada@example.com', lines: [{ productId: 1, quantity: 2 }] });
    const shipment = await trackShipment(String(order.id));
    setStatus(`Order ${order.id}: ${order.status}, shipment ${shipment.status ?? 'pending'}`);
  };

  return (
    <section>
      <button onClick={submit}>Place order</button>
      <p>{status}</p>
    </section>
  );
}
