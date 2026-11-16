const axios = require('axios');

const api = axios.create({ baseURL: process.env.SHOP_API_URL || 'http://localhost:5000' });

/** Keeps the catalog of the C# Shop API in step with the supplier's feed. */
async function syncProducts() {
  const { data: products } = await api.get('/api/Products');
  for (const item of readFeed()) {
    if (!products.some((p) => p.name === item.name)) {
      await api.post('/api/Products', { name: item.name, price: item.price, stock: item.stock });
    }
  }
}

function readFeed() {
  return [{ name: 'Kettle', price: 39.5, stock: 12 }];
}

module.exports = { syncProducts };
