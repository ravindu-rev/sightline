import { useEffect, useState } from 'react';
import { formatMoney, type Product } from '@shop/shared';
import { listProducts } from '../api/client';

export function ProductList() {
  const [products, setProducts] = useState<Product[]>([]);

  useEffect(() => {
    listProducts().then(setProducts);
  }, []);

  return (
    <ul>
      {products.map((p) => (
        <li key={p.id}>
          {p.name} {formatMoney(p.price)}
        </li>
      ))}
    </ul>
  );
}
