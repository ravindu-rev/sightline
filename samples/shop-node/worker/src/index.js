const { syncProducts } = require('./sync');

syncProducts().catch((err) => console.error(err));
setInterval(() => syncProducts().catch((err) => console.error(err)), 60_000);
