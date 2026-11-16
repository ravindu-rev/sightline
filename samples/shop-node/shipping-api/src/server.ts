import express from 'express';
import { shipmentsRouter } from './routes/shipments.js';
import { registerQuoteRoutes } from './routes/quotes.js';

const app = express();
app.use(express.json());

app.use('/api/shipments', shipmentsRouter);

const api = express.Router();
registerQuoteRoutes(api);
app.use('/api', api);

app.get('/health', (_req, res) => {
  res.json({ ok: true });
});

app.listen(Number(process.env.PORT ?? 4000));
